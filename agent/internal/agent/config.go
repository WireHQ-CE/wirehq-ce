// Package agent is the WireHQ outbound-only mTLS agent: it enrols once, then loops — pulling signed
// deployment jobs over mTLS, verifying their signature, applying WireGuard config locally, and reporting
// status + telemetry back. No inbound ports. (ADR-028, docs/13-agent.md)
package agent

import (
	"encoding/json"
	"fmt"
	"os"
	"path/filepath"
)

// Config is the agent's runtime configuration (flags + on-disk state directory).
type Config struct {
	// Server is the base URL of the WireHQ agent gateway, e.g. https://wirehq.example.com:28443.
	Server string
	// StateDir holds the enrolled identity (client key/cert, the org CA cert, the agent id).
	StateDir string
	// InsecureSkipVerify trusts any server certificate — DEVELOPMENT ONLY (self-signed dev gateway).
	InsecureSkipVerify bool
}

func (c Config) keyPath() string   { return filepath.Join(c.StateDir, "agent.key") }
func (c Config) certPath() string  { return filepath.Join(c.StateDir, "agent.crt") }
func (c Config) caPath() string    { return filepath.Join(c.StateDir, "ca.crt") }
func (c Config) statePath() string { return filepath.Join(c.StateDir, "agent.json") }

// interfaceKeyPath is the locally-generated WireGuard private key for an AgentManaged interface.
func (c Config) interfaceKeyPath(iface string) string {
	return filepath.Join(c.StateDir, "iface-"+iface+".key")
}

// Identity is the persisted result of enrolment.
type Identity struct {
	AgentID string `json:"agentId"`
}

// IsEnrolled reports whether a client key + cert + CA are already on disk.
func (c Config) IsEnrolled() bool {
	for _, p := range []string{c.keyPath(), c.certPath(), c.caPath()} {
		if _, err := os.Stat(p); err != nil {
			return false
		}
	}
	return true
}

func (c Config) saveIdentity(id Identity) error {
	if err := os.MkdirAll(c.StateDir, 0o700); err != nil {
		return fmt.Errorf("create state dir: %w", err)
	}
	data, err := json.MarshalIndent(id, "", "  ")
	if err != nil {
		return err
	}
	return os.WriteFile(c.statePath(), data, 0o600)
}

func (c Config) loadIdentity() (Identity, error) {
	var id Identity
	data, err := os.ReadFile(c.statePath())
	if err != nil {
		return id, err
	}
	return id, json.Unmarshal(data, &id)
}

func (c Config) appliedStatePath(iface string) string {
	return filepath.Join(c.StateDir, "applied-"+iface+".json")
}

// AppliedState records what the agent last applied to an interface, so it can later detect drift — the
// on-disk config changing out from under it (host-side tampering).
type AppliedState struct {
	Interface  string `json:"interface"`
	InstanceID string `json:"instanceId"`
	ConfigHash string `json:"configHash"`
}

func (c Config) saveAppliedState(s AppliedState) error {
	if err := os.MkdirAll(c.StateDir, 0o700); err != nil {
		return fmt.Errorf("create state dir: %w", err)
	}
	data, err := json.MarshalIndent(s, "", "  ")
	if err != nil {
		return err
	}
	return os.WriteFile(c.appliedStatePath(s.Interface), data, 0o600)
}

// loadAppliedStates returns the recorded state for every interface the agent has applied a config to.
func (c Config) loadAppliedStates() ([]AppliedState, error) {
	matches, err := filepath.Glob(filepath.Join(c.StateDir, "applied-*.json"))
	if err != nil {
		return nil, err
	}
	states := make([]AppliedState, 0, len(matches))
	for _, path := range matches {
		data, readErr := os.ReadFile(path)
		if readErr != nil {
			continue
		}
		var s AppliedState
		if json.Unmarshal(data, &s) == nil && s.Interface != "" {
			states = append(states, s)
		}
	}
	return states, nil
}
