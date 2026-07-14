package agent

import (
	"encoding/base64"
	"strings"
	"testing"
)

func TestLoadOrCreateInterfaceKey_GeneratesThenLoadsStably(t *testing.T) {
	cfg := Config{StateDir: t.TempDir()}

	priv1, pub1, err := LoadOrCreateInterfaceKey(cfg, "wg0")
	if err != nil {
		t.Fatalf("first call should generate a key: %v", err)
	}

	// Both keys must be base64 of raw 32 bytes (the WireGuard / `wg genkey` format).
	for name, key := range map[string]string{"private": priv1, "public": pub1} {
		raw, decErr := base64.StdEncoding.DecodeString(key)
		if decErr != nil || len(raw) != 32 {
			t.Fatalf("%s key is not 32-byte base64: %q (err %v, len %d)", name, key, decErr, len(raw))
		}
	}
	if priv1 == pub1 {
		t.Fatal("public key must differ from the private key")
	}

	// A second call loads the persisted key and derives the same public key — the server identity is stable.
	priv2, pub2, err := LoadOrCreateInterfaceKey(cfg, "wg0")
	if err != nil {
		t.Fatalf("second call should load the persisted key: %v", err)
	}
	if priv2 != priv1 || pub2 != pub1 {
		t.Fatalf("key not stable across calls: (%s,%s) vs (%s,%s)", priv1, pub1, priv2, pub2)
	}

	// A different interface gets its own key.
	priv3, _, err := LoadOrCreateInterfaceKey(cfg, "wg1")
	if err != nil {
		t.Fatalf("wg1 key: %v", err)
	}
	if priv3 == priv1 {
		t.Fatal("each interface should have its own key")
	}
}

func TestInjectPrivateKey_InsertsAfterInterfaceHeader(t *testing.T) {
	bundle := "[Interface]\nAddress = 10.8.0.1/24\nListenPort = 51820\n\n[Peer]\nPublicKey = abc\nAllowedIPs = 10.8.0.2/32\n"
	out := InjectPrivateKey(bundle, "PRIVB64")

	lines := strings.Split(out, "\n")
	if lines[0] != "[Interface]" || lines[1] != "PrivateKey = PRIVB64" {
		t.Fatalf("PrivateKey not injected right after [Interface]: %q", out)
	}
	// The rest of the bundle is preserved (the peer block survives unchanged).
	if !strings.Contains(out, "[Peer]\nPublicKey = abc\nAllowedIPs = 10.8.0.2/32") {
		t.Fatalf("peer block altered: %q", out)
	}
	if strings.Count(out, "PrivateKey =") != 1 {
		t.Fatalf("expected exactly one PrivateKey line: %q", out)
	}
}

func TestInjectPrivateKey_NoInterfaceHeaderIsUnchanged(t *testing.T) {
	if got := InjectPrivateKey("not a config", "PRIVB64"); got != "not a config" {
		t.Fatalf("expected unchanged config, got %q", got)
	}
}
