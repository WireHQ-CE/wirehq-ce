package agent

import (
	"context"
	"crypto/sha256"
	"encoding/hex"
	"fmt"
	"os"
	"os/exec"
	"path/filepath"
	"strconv"
	"strings"
	"time"
)

// Runner executes a host command and returns its combined output. Abstracted so the applier is unit-testable
// with a fake, and the live test can inject fake `wg`/`wg-quick` shims (no kernel module — ADR-002).
type Runner interface {
	Run(ctx context.Context, name string, args ...string) (string, error)
}

type execRunner struct{}

func (execRunner) Run(ctx context.Context, name string, args ...string) (string, error) {
	// All call sites pass a literal command name ("wg-quick"/"wg"); exec.Command does NOT invoke a shell
	// (argv is passed directly — no ;|$() interpretation), and the interface name in args arrives via the
	// signature-verified job from the trusted control plane. Not a command-injection vector.
	// nosemgrep: go.lang.security.audit.dangerous-exec-command.dangerous-exec-command
	out, err := exec.CommandContext(ctx, name, args...).CombinedOutput()
	return string(out), err
}

// Applier writes a rendered config to disk and (re)brings the interface up with `wg-quick`, rolling back to
// the previous config if the bring-up fails. (mirrors the SSH provider's backup → write → up → rollback)
type Applier struct {
	Runner    Runner
	ConfigDir string
}

// NewApplier builds an applier that shells out to the real `wg-quick`/`wg` against /etc/wireguard.
func NewApplier() *Applier {
	return &Applier{Runner: execRunner{}, ConfigDir: "/etc/wireguard"}
}

// Apply writes the interface config and brings it up, rolling back on failure.
func (a *Applier) Apply(ctx context.Context, iface, config string) error {
	if err := os.MkdirAll(a.ConfigDir, 0o700); err != nil {
		return fmt.Errorf("create config dir: %w", err)
	}
	path := filepath.Join(a.ConfigDir, iface+".conf")

	previous, hadPrevious := readIfExists(path)
	if err := os.WriteFile(path, []byte(config), 0o600); err != nil {
		return fmt.Errorf("write config: %w", err)
	}

	// A reload is down-then-up; the first down is best-effort (the interface may not be up yet).
	_, _ = a.Runner.Run(ctx, "wg-quick", "down", iface)
	if out, err := a.Runner.Run(ctx, "wg-quick", "up", iface); err != nil {
		a.rollback(ctx, iface, path, previous, hadPrevious)
		return fmt.Errorf("wg-quick up failed (rolled back): %v: %s", err, strings.TrimSpace(out))
	}
	return nil
}

func (a *Applier) rollback(ctx context.Context, iface, path string, previous []byte, hadPrevious bool) {
	if hadPrevious {
		_ = os.WriteFile(path, previous, 0o600)
		_, _ = a.Runner.Run(ctx, "wg-quick", "up", iface)
		return
	}
	_ = os.Remove(path)
	_, _ = a.Runner.Run(ctx, "wg-quick", "down", iface)
}

// ConfigHash returns the sha256 (hex) of the interface's on-disk config and whether the file exists. Used to
// detect drift — the deployed config changing out from under what the agent last applied.
func (a *Applier) ConfigHash(iface string) (string, bool) {
	data, err := os.ReadFile(filepath.Join(a.ConfigDir, iface+".conf"))
	if err != nil {
		return "", false
	}
	sum := sha256.Sum256(data)
	return hex.EncodeToString(sum[:]), true
}

// Telemetry parses `wg show <iface> dump` into per-peer handshake + transfer.
func (a *Applier) Telemetry(ctx context.Context, iface string) ([]PeerTelemetry, error) {
	out, err := a.Runner.Run(ctx, "wg", "show", iface, "dump")
	if err != nil {
		return nil, fmt.Errorf("wg show dump: %w", err)
	}
	return parseDump(out), nil
}

// parseDump reads `wg show <iface> dump`. The first line is the interface; each subsequent line is a peer:
// public-key  preshared-key  endpoint  allowed-ips  latest-handshake  rx  tx  keepalive (tab-separated).
func parseDump(dump string) []PeerTelemetry {
	lines := strings.Split(strings.TrimSpace(dump), "\n")
	var peers []PeerTelemetry
	for i, line := range lines {
		if i == 0 || strings.TrimSpace(line) == "" {
			continue // the interface line / blanks
		}
		fields := strings.Split(line, "\t")
		if len(fields) < 7 {
			continue
		}

		peer := PeerTelemetry{PublicKey: fields[0]}
		if fields[2] != "" && fields[2] != "(none)" {
			peer.Endpoint = fields[2]
		}
		if handshake, err := strconv.ParseInt(fields[4], 10, 64); err == nil && handshake > 0 {
			at := time.Unix(handshake, 0).UTC()
			peer.LastHandshakeAtUTC = &at
		}
		peer.RxBytes, _ = strconv.ParseInt(fields[5], 10, 64)
		peer.TxBytes, _ = strconv.ParseInt(fields[6], 10, 64)
		peers = append(peers, peer)
	}
	return peers
}

func readIfExists(path string) ([]byte, bool) {
	data, err := os.ReadFile(path)
	if err != nil {
		return nil, false
	}
	return data, true
}
