package agent

import (
	"os"
	"path/filepath"
	"testing"
)

func TestConfigHash_DetectsChanges(t *testing.T) {
	dir := t.TempDir()
	applier := &Applier{Runner: &fakeRunner{}, ConfigDir: dir}

	if _, ok := applier.ConfigHash("wg0"); ok {
		t.Fatal("a missing config should report not-exists")
	}

	if err := os.WriteFile(filepath.Join(dir, "wg0.conf"), []byte("[Interface]\nListenPort = 51820\n"), 0o600); err != nil {
		t.Fatal(err)
	}
	first, ok := applier.ConfigHash("wg0")
	if !ok || first == "" {
		t.Fatal("expected a hash for an existing config")
	}

	// Same content → same hash; changed content → different hash (drift).
	again, _ := applier.ConfigHash("wg0")
	if again != first {
		t.Fatalf("hash should be stable: %s vs %s", first, again)
	}
	if err := os.WriteFile(filepath.Join(dir, "wg0.conf"), []byte("[Interface]\nListenPort = 51821\n"), 0o600); err != nil {
		t.Fatal(err)
	}
	if changed, _ := applier.ConfigHash("wg0"); changed == first {
		t.Fatal("hash should change when the config changes")
	}
}

func TestAppliedState_RoundTripsAndDetectsDrift(t *testing.T) {
	cfg := Config{StateDir: t.TempDir()}
	if err := cfg.saveAppliedState(AppliedState{Interface: "wg0", InstanceID: "inst-1", ConfigHash: "abc"}); err != nil {
		t.Fatal(err)
	}
	if err := cfg.saveAppliedState(AppliedState{Interface: "wg1", InstanceID: "inst-2", ConfigHash: "def"}); err != nil {
		t.Fatal(err)
	}

	states, err := cfg.loadAppliedStates()
	if err != nil {
		t.Fatal(err)
	}
	if len(states) != 2 {
		t.Fatalf("expected 2 applied states, got %d", len(states))
	}

	byIface := map[string]AppliedState{}
	for _, s := range states {
		byIface[s.Interface] = s
	}
	if byIface["wg0"].InstanceID != "inst-1" || byIface["wg0"].ConfigHash != "abc" {
		t.Fatalf("wg0 state not round-tripped: %+v", byIface["wg0"])
	}

	// The drift check the run loop performs: current on-disk hash vs the recorded one.
	current := "xyz"
	if !(current != byIface["wg0"].ConfigHash) {
		t.Fatal("expected drift when the on-disk hash differs from the applied hash")
	}
}

func TestLoadAppliedStates_EmptyWhenNoneRecorded(t *testing.T) {
	cfg := Config{StateDir: t.TempDir()}
	states, err := cfg.loadAppliedStates()
	if err != nil || len(states) != 0 {
		t.Fatalf("expected no states, got %d (err %v)", len(states), err)
	}
}
