package agent

import (
	"crypto/ecdh"
	"crypto/rand"
	"encoding/base64"
	"fmt"
	"os"
	"strings"
)

// LoadOrCreateInterfaceKey returns the base64 private + public WireGuard (X25519) keys for an AgentManaged
// interface, generating and persisting a fresh keypair under the state dir on first use. The private key
// NEVER leaves the host — WireHQ only ever learns the public key (reported in the job result). Stdlib only:
// crypto/ecdh X25519 produces raw 32-byte keys interoperable with `wg`/`wg genkey`. (ADR-028)
func LoadOrCreateInterfaceKey(cfg Config, iface string) (privB64, pubB64 string, err error) {
	path := cfg.interfaceKeyPath(iface)
	if data, readErr := os.ReadFile(path); readErr == nil {
		return interfaceKeyFromStored(strings.TrimSpace(string(data)))
	}

	priv, err := ecdh.X25519().GenerateKey(rand.Reader)
	if err != nil {
		return "", "", fmt.Errorf("generate interface key: %w", err)
	}
	privB64 = base64.StdEncoding.EncodeToString(priv.Bytes())

	if err := os.MkdirAll(cfg.StateDir, 0o700); err != nil {
		return "", "", fmt.Errorf("create state dir: %w", err)
	}
	if err := os.WriteFile(path, []byte(privB64+"\n"), 0o600); err != nil {
		return "", "", fmt.Errorf("persist interface key: %w", err)
	}
	return privB64, base64.StdEncoding.EncodeToString(priv.PublicKey().Bytes()), nil
}

func interfaceKeyFromStored(privB64 string) (string, string, error) {
	raw, err := base64.StdEncoding.DecodeString(privB64)
	if err != nil {
		return "", "", fmt.Errorf("decode stored interface key: %w", err)
	}
	priv, err := ecdh.X25519().NewPrivateKey(raw)
	if err != nil {
		return "", "", fmt.Errorf("load interface key: %w", err)
	}
	return privB64, base64.StdEncoding.EncodeToString(priv.PublicKey().Bytes()), nil
}

// InjectPrivateKey inserts a PrivateKey line into the [Interface] section of an AgentManaged bundle (which is
// delivered key-less). The signed + hashed bundle the agent verifies and reports is the ORIGINAL key-less
// text — injection happens only on the copy written to disk, so it stays outside the integrity surface.
// Returns the config unchanged if it has no [Interface] header.
func InjectPrivateKey(config, privKeyB64 string) string {
	lines := strings.Split(config, "\n")
	for i, line := range lines {
		if strings.TrimSpace(line) == "[Interface]" {
			injected := make([]string, 0, len(lines)+1)
			injected = append(injected, lines[:i+1]...)
			injected = append(injected, "PrivateKey = "+privKeyB64)
			injected = append(injected, lines[i+1:]...)
			return strings.Join(injected, "\n")
		}
	}
	return config
}
