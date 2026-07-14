package agent

import (
	"context"
	"errors"
	"os"
	"path/filepath"
	"testing"
)

type fakeRunner struct {
	calls    [][]string
	failUp   bool
	dumpData string
}

func (f *fakeRunner) Run(_ context.Context, name string, args ...string) (string, error) {
	f.calls = append(f.calls, append([]string{name}, args...))
	if name == "wg" {
		return f.dumpData, nil
	}
	if f.failUp && name == "wg-quick" && len(args) > 0 && args[0] == "up" {
		return "bring-up failed", errors.New("wg-quick up exit 1")
	}
	return "", nil
}

func TestApply_WritesConfigAndBringsTheInterfaceUp(t *testing.T) {
	runner := &fakeRunner{}
	applier := &Applier{Runner: runner, ConfigDir: t.TempDir()}

	if err := applier.Apply(context.Background(), "wg0", "[Interface]\nListenPort = 51820\n"); err != nil {
		t.Fatalf("expected apply to succeed, got %v", err)
	}

	written, _ := os.ReadFile(filepath.Join(applier.ConfigDir, "wg0.conf"))
	if string(written) != "[Interface]\nListenPort = 51820\n" {
		t.Fatalf("config not written: %q", string(written))
	}
	// down (best-effort reload) then up.
	if len(runner.calls) < 2 || runner.calls[len(runner.calls)-1][1] != "up" {
		t.Fatalf("expected wg-quick up last, got %v", runner.calls)
	}
}

func TestApply_RollsBackToThePreviousConfigOnFailure(t *testing.T) {
	dir := t.TempDir()
	path := filepath.Join(dir, "wg0.conf")
	previous := "[Interface]\n# previous good\n"
	if err := os.WriteFile(path, []byte(previous), 0o600); err != nil {
		t.Fatal(err)
	}

	applier := &Applier{Runner: &fakeRunner{failUp: true}, ConfigDir: dir}
	if err := applier.Apply(context.Background(), "wg0", "[Interface]\n# broken\n"); err == nil {
		t.Fatal("expected apply to fail when wg-quick up fails")
	}

	restored, _ := os.ReadFile(path)
	if string(restored) != previous {
		t.Fatalf("expected rollback to the previous config, got %q", string(restored))
	}
}

func TestParseDump_ReadsPeerTelemetry(t *testing.T) {
	dump := "PRIVKEY\tIFACEPUB\t51820\toff\n" +
		"PEERPUB\t(none)\t203.0.113.5:51820\t10.0.0.2/32\t1700000000\t1024\t2048\toff\n"

	peers := parseDump(dump)
	if len(peers) != 1 {
		t.Fatalf("expected 1 peer, got %d", len(peers))
	}
	p := peers[0]
	if p.PublicKey != "PEERPUB" || p.Endpoint != "203.0.113.5:51820" || p.RxBytes != 1024 || p.TxBytes != 2048 {
		t.Fatalf("unexpected peer: %+v", p)
	}
	if p.LastHandshakeAtUTC == nil || p.LastHandshakeAtUTC.Unix() != 1700000000 {
		t.Fatalf("expected handshake time, got %v", p.LastHandshakeAtUTC)
	}
}
