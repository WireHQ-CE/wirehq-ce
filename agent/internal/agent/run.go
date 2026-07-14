package agent

import (
	"context"
	"crypto/sha256"
	"encoding/hex"
	"fmt"
	"log"
	"os"
	"time"
)

// Version is reported on heartbeat. Bumped per agent release.
const Version = "0.1.0"

// Loop is the agent's main cycle: heartbeat, drain signed jobs (verify → apply → report), push telemetry.
type Loop struct {
	cfg     Config
	client  *Client
	applier *Applier
	caCert  []byte
}

// NewLoop wires the run loop from an enrolled state directory.
func NewLoop(cfg Config) (*Loop, error) {
	if !cfg.IsEnrolled() {
		return nil, fmt.Errorf("agent is not enrolled (run `wirehq-agent enroll` first)")
	}
	client, err := NewClient(cfg)
	if err != nil {
		return nil, err
	}
	caCert, err := os.ReadFile(cfg.caPath())
	if err != nil {
		return nil, fmt.Errorf("read ca certificate: %w", err)
	}
	return &Loop{cfg: cfg, client: client, applier: NewApplier(), caCert: caCert}, nil
}

// Run loops until the context is cancelled, polling every interval.
func (l *Loop) Run(ctx context.Context, interval time.Duration) error {
	ticker := time.NewTicker(interval)
	defer ticker.Stop()

	l.tick(ctx)
	for {
		select {
		case <-ctx.Done():
			return ctx.Err()
		case <-ticker.C:
			l.tick(ctx)
		}
	}
}

// Once runs a single poll cycle (heartbeat → drain jobs → telemetry) and returns — for cron-style
// scheduling and for deterministic end-to-end tests.
func (l *Loop) Once(ctx context.Context) { l.tick(ctx) }

func (l *Loop) tick(ctx context.Context) {
	if err := l.client.Heartbeat(ctx, Version); err != nil {
		log.Printf("heartbeat failed: %v", err)
	}

	jobs, err := l.client.Jobs(ctx)
	if err != nil {
		log.Printf("poll jobs failed: %v", err)
		return
	}
	for _, job := range jobs {
		l.process(ctx, job)
	}

	// Non-WireGuard work (directory syncs) off the generic task channel.
	l.processTasks(ctx)

	l.reportStatus(ctx)
}

// reportStatus reports per-interface drift — the current on-disk config vs what the agent last applied — for
// every interface it manages, so WireHQ can surface drift for Pull instances it cannot probe inline.
func (l *Loop) reportStatus(ctx context.Context) {
	states, err := l.cfg.loadAppliedStates()
	if err != nil || len(states) == 0 {
		return
	}

	reports := make([]InstanceStatus, 0, len(states))
	for _, s := range states {
		current, ok := l.applier.ConfigHash(s.Interface)
		reports = append(reports, InstanceStatus{
			InstanceID: s.InstanceID,
			ConfigHash: current,
			Drift:      !ok || current != s.ConfigHash,
		})
	}

	if err := l.client.ReportStatus(ctx, reports); err != nil {
		log.Printf("report status failed: %v", err)
	}
}

// stepRecorder collects the agent's structured step events for one job so they can be forwarded to the
// telemetry plane (docs/15 §9) as OTel span events + logs (self-diagnostics visible without SSH).
type stepRecorder struct{ events []DiagEvent }

// step records one completed step with its duration and outcome. err != nil ⇒ a failed step (error level).
func (r *stepRecorder) step(name string, start time.Time, err error) {
	e := DiagEvent{
		Name:       name,
		AtUTC:      start.UTC(),
		DurationMs: float64(time.Since(start).Microseconds()) / 1000.0,
		Level:      "info",
		Outcome:    "ok",
	}
	if err != nil {
		e.Level = "error"
		e.Outcome = "failed"
		e.Message = err.Error()
	}
	r.events = append(r.events, e)
}

// process verifies, applies, and reports a single job — the signature is checked BEFORE the config is touched.
// Log lines carry the deploy's correlation id (corr=…) so the agent's edge logs chain to the originating
// request, that id round-trips back on every result, and the per-step timings/outcomes are forwarded to the
// telemetry plane on exit (one batch per job, best-effort).
func (l *Loop) process(ctx context.Context, job Job) {
	tag := fmt.Sprintf("job %s corr=%s", job.JobID, job.CorrelationID)
	rec := &stepRecorder{}
	defer func() {
		if err := l.client.ReportDiagnostics(ctx, job.JobID, job.InstanceID, job.CorrelationID, rec.events); err != nil {
			log.Printf("%s report diagnostics failed: %v", tag, err)
		}
	}()

	start := time.Now()
	if err := VerifyBundle(l.caCert, []byte(job.Bundle), job.Signature); err != nil {
		rec.step("verify", start, err)
		log.Printf("%s rejected: %v", tag, err)
		l.report(ctx, job.JobID, job.CorrelationID, "Failed", "", "bundle signature verification failed", "")
		return
	}
	rec.step("verify", start, nil)

	// AgentManaged bundles arrive without a PrivateKey line; inject our locally-held interface key (the
	// signature was just verified on the key-less bytes). WireHQ never holds this private key — we report
	// only the public key on success.
	configToApply := job.Bundle
	interfacePublicKey := ""
	if job.AgentManaged {
		start = time.Now()
		priv, pub, err := LoadOrCreateInterfaceKey(l.cfg, job.InterfaceName)
		if err != nil {
			rec.step("key_setup", start, err)
			log.Printf("%s key setup failed: %v", tag, err)
			l.report(ctx, job.JobID, job.CorrelationID, "Failed", "", err.Error(), "")
			return
		}
		rec.step("key_setup", start, nil)
		configToApply = InjectPrivateKey(job.Bundle, priv)
		interfacePublicKey = pub
	}

	start = time.Now()
	if err := l.applier.Apply(ctx, job.InterfaceName, configToApply); err != nil {
		rec.step("apply", start, err)
		log.Printf("%s apply failed: %v", tag, err)
		l.report(ctx, job.JobID, job.CorrelationID, "Failed", "", err.Error(), "")
		return
	}
	rec.step("apply", start, nil)

	// The reported hash is of the WireHQ-signed (key-less) bundle, so it round-trips against WireHQ's
	// desired render — the injected key is a local detail outside the integrity surface.
	sum := sha256.Sum256([]byte(job.Bundle))
	l.report(ctx, job.JobID, job.CorrelationID, "Succeeded", hex.EncodeToString(sum[:]), "", interfacePublicKey)
	log.Printf("%s applied to %s", tag, job.InterfaceName)

	// Record the on-disk hash we just wrote so later polls can detect drift (the config changing locally).
	if hash, ok := l.applier.ConfigHash(job.InterfaceName); ok {
		if err := l.cfg.saveAppliedState(AppliedState{Interface: job.InterfaceName, InstanceID: job.InstanceID, ConfigHash: hash}); err != nil {
			log.Printf("record applied state for %s failed: %v", job.InterfaceName, err)
		}
	}

	start = time.Now()
	peers, err := l.applier.Telemetry(ctx, job.InterfaceName)
	if err != nil {
		rec.step("telemetry", start, err)
		log.Printf("telemetry collection failed: %v", err)
		return
	}
	if err := l.client.ReportTelemetry(ctx, job.InstanceID, peers); err != nil {
		rec.step("telemetry", start, err)
		log.Printf("telemetry report failed: %v", err)
		return
	}
	rec.step("telemetry", start, nil)
}

func (l *Loop) report(ctx context.Context, jobID, correlationID, status, hash, errMsg, interfacePublicKey string) {
	if err := l.client.ReportResult(ctx, jobID, status, hash, errMsg, interfacePublicKey, correlationID); err != nil {
		log.Printf("report result for job %s failed: %v", jobID, err)
	}
}
