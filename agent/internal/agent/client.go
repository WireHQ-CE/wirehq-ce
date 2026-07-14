package agent

import (
	"bytes"
	"context"
	"crypto/tls"
	"encoding/json"
	"fmt"
	"io"
	"net/http"
	"time"
)

// ---- Wire DTOs (camelCase JSON, matching the ASP.NET agent gateway in WireHQ.Modules.Orchestration) ----

type enrollRequest struct {
	Token    string `json:"token"`
	Csr      string `json:"csr"`
	Name     string `json:"name,omitempty"`
	Platform string `json:"platform,omitempty"`
}

type enrollResponse struct {
	AgentID          string `json:"agentId"`
	CertificatePEM   string `json:"certificatePem"`
	CaCertificatePEM string `json:"caCertificatePem"`
}

// Job is a signed deployment bundle the agent must verify, apply, and report on. When AgentManaged is true
// the bundle has no PrivateKey line — the agent injects its locally-held interface key after verification.
type Job struct {
	JobID         string `json:"jobId"`
	InstanceID    string `json:"instanceId"`
	InterfaceName string `json:"interfaceName"`
	Bundle        string `json:"bundle"`
	Signature     string `json:"signature"`
	AgentManaged  bool   `json:"agentManaged"`
	// CorrelationID is the originating deploy's correlation id (the W3C trace id). The agent stamps it on its
	// logs and echoes it back on the result, so the deploy is one trace browser → API → job → agent → back.
	CorrelationID string `json:"correlationId,omitempty"`
}

type jobResult struct {
	Status            string `json:"status"`
	AppliedConfigHash string `json:"appliedConfigHash,omitempty"`
	Error             string `json:"error,omitempty"`
	// InterfacePublicKey is the agent-generated interface public key, sent on a successful AgentManaged
	// apply so WireHQ can record it (it never receives the private key).
	InterfacePublicKey string `json:"interfacePublicKey,omitempty"`
	// CorrelationID echoes the job's correlation id back to close the loop (the gateway also holds it on the
	// job row, authoritatively).
	CorrelationID string `json:"correlationId,omitempty"`
}

// Task is an opaque unit of non-WireGuard work off the generic agent-task channel (a directory sync is the
// first kind). The agent switches on Kind and interprets PayloadJson. (docs/23-ldap-directory-sync.md §6/§7)
type Task struct {
	TaskID      string `json:"taskId"`
	Kind        string `json:"kind"`
	PayloadJSON string `json:"payloadJson"`
}

// taskResultRequest wraps the opaque result the owning provider interprets (e.g. a directory snapshot).
type taskResultRequest struct {
	Result any `json:"result"`
}

// PeerTelemetry is one peer's observed handshake + transfer (from `wg show <iface> dump`).
type PeerTelemetry struct {
	PublicKey          string     `json:"publicKey"`
	LastHandshakeAtUTC *time.Time `json:"lastHandshakeAtUtc,omitempty"`
	RxBytes            int64      `json:"rxBytes"`
	TxBytes            int64      `json:"txBytes"`
	Endpoint           string     `json:"endpoint,omitempty"`
}

type telemetryRequest struct {
	InstanceID string          `json:"instanceId"`
	Peers      []PeerTelemetry `json:"peers"`
}

// InstanceStatus is the agent's observed runtime status for one managed interface — drift is agent-computed
// (current on-disk config vs what the agent last applied).
type InstanceStatus struct {
	InstanceID string `json:"instanceId"`
	ConfigHash string `json:"configHash,omitempty"`
	Drift      bool   `json:"drift"`
}

type statusRequest struct {
	Instances []InstanceStatus `json:"instances"`
}

type heartbeatRequest struct {
	Version string `json:"version,omitempty"`
}

// DiagEvent is one structured step the agent reports to the telemetry plane (docs/15 §9): a named step with a
// timing + outcome, which the gateway re-emits as an OTel span event + a structured log (self-diagnostics
// visible without SSH). Outcome is "ok" or "failed"; level is info/warn/error.
type DiagEvent struct {
	Name       string            `json:"name"`
	AtUTC      time.Time         `json:"atUtc"`
	DurationMs float64           `json:"durationMs,omitempty"`
	Level      string            `json:"level,omitempty"`
	Outcome    string            `json:"outcome,omitempty"`
	Message    string            `json:"message,omitempty"`
	Attributes map[string]string `json:"attributes,omitempty"`
}

// diagnosticsRequest batches an activity's step events. jobId/instanceId are omitted (→ null server-side) when
// empty; when a jobId is present the gateway parents the span to that deploy's authoritative trace id.
type diagnosticsRequest struct {
	JobID         string      `json:"jobId,omitempty"`
	InstanceID    string      `json:"instanceId,omitempty"`
	CorrelationID string      `json:"correlationId,omitempty"`
	Events        []DiagEvent `json:"events"`
}

// Client makes authenticated (mTLS) calls to the gateway once the agent is enrolled.
type Client struct {
	server string
	http   *http.Client
}

// NewClient builds an mTLS client that presents the enrolled client certificate.
func NewClient(cfg Config) (*Client, error) {
	cert, err := tls.LoadX509KeyPair(cfg.certPath(), cfg.keyPath())
	if err != nil {
		return nil, fmt.Errorf("load client certificate (is the agent enrolled?): %w", err)
	}
	return &Client{
		server: cfg.Server,
		http: &http.Client{
			Timeout: 90 * time.Second,
			Transport: &http.Transport{
				TLSClientConfig: &tls.Config{
					Certificates:       []tls.Certificate{cert},
					InsecureSkipVerify: cfg.InsecureSkipVerify, //nolint:gosec // dev-only flag; trusts the self-signed dev gateway
				},
			},
		},
	}, nil
}

// Heartbeat reports liveness + the agent version.
func (c *Client) Heartbeat(ctx context.Context, version string) error {
	return c.do(ctx, http.MethodPost, "/agent/v1/heartbeat", heartbeatRequest{Version: version}, nil)
}

// Jobs pulls the agent's pending signed deployment bundles.
func (c *Client) Jobs(ctx context.Context) ([]Job, error) {
	var jobs []Job
	if err := c.do(ctx, http.MethodGet, "/agent/v1/jobs", nil, &jobs); err != nil {
		return nil, err
	}
	return jobs, nil
}

// ReportResult closes a job after the agent applied (or failed to apply) it. interfacePublicKey is the
// agent-generated interface public key for an AgentManaged success (empty otherwise); correlationID echoes
// the originating deploy's correlation id back.
func (c *Client) ReportResult(ctx context.Context, jobID, status, appliedHash, errMsg, interfacePublicKey, correlationID string) error {
	return c.do(ctx, http.MethodPost, "/agent/v1/jobs/"+jobID+"/result",
		jobResult{Status: status, AppliedConfigHash: appliedHash, Error: errMsg, InterfacePublicKey: interfacePublicKey, CorrelationID: correlationID}, nil)
}

// Tasks pulls the agent's pending non-WireGuard tasks (directory syncs) off the generic task channel.
func (c *Client) Tasks(ctx context.Context) ([]Task, error) {
	var tasks []Task
	if err := c.do(ctx, http.MethodGet, "/agent/v1/tasks", nil, &tasks); err != nil {
		return nil, err
	}
	return tasks, nil
}

// ReportTaskResult posts an opaque result for a task (e.g. a pulled directory snapshot).
func (c *Client) ReportTaskResult(ctx context.Context, taskID string, result any) error {
	return c.do(ctx, http.MethodPost, "/agent/v1/tasks/"+taskID+"/result", taskResultRequest{Result: result}, nil)
}

// ReportTelemetry pushes observed peer telemetry for an instance.
func (c *Client) ReportTelemetry(ctx context.Context, instanceID string, peers []PeerTelemetry) error {
	return c.do(ctx, http.MethodPost, "/agent/v1/telemetry",
		telemetryRequest{InstanceID: instanceID, Peers: peers}, nil)
}

// ReportStatus pushes the agent's observed runtime status (incl. drift) for its managed interfaces.
func (c *Client) ReportStatus(ctx context.Context, instances []InstanceStatus) error {
	return c.do(ctx, http.MethodPost, "/agent/v1/status", statusRequest{Instances: instances}, nil)
}

// ReportDiagnostics forwards a batch of the agent's structured step events to the telemetry plane (docs/15 §9).
// Best-effort: an empty batch is a no-op, and callers log (never fail the deploy on) a reporting error.
func (c *Client) ReportDiagnostics(ctx context.Context, jobID, instanceID, correlationID string, events []DiagEvent) error {
	if len(events) == 0 {
		return nil
	}
	return c.do(ctx, http.MethodPost, "/agent/v1/diagnostics",
		diagnosticsRequest{JobID: jobID, InstanceID: instanceID, CorrelationID: correlationID, Events: events}, nil)
}

func (c *Client) do(ctx context.Context, method, path string, body, out any) error {
	var reader io.Reader
	if body != nil {
		encoded, err := json.Marshal(body)
		if err != nil {
			return err
		}
		reader = bytes.NewReader(encoded)
	}

	req, err := http.NewRequestWithContext(ctx, method, c.server+path, reader)
	if err != nil {
		return err
	}
	if body != nil {
		req.Header.Set("Content-Type", "application/json")
	}

	resp, err := c.http.Do(req)
	if err != nil {
		return err
	}
	defer resp.Body.Close()

	if resp.StatusCode < 200 || resp.StatusCode >= 300 {
		snippet, _ := io.ReadAll(io.LimitReader(resp.Body, 512))
		return fmt.Errorf("%s %s: %s: %s", method, path, resp.Status, string(snippet))
	}

	if out != nil {
		return json.NewDecoder(resp.Body).Decode(out)
	}
	return nil
}
