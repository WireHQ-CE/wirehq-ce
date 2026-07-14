package agent

import (
	"encoding/json"
	"os"
	"testing"
)

func TestLoadDirectorySettings_absentIsNotAnError(t *testing.T) {
	cfg := Config{StateDir: t.TempDir()}

	_, configured, err := cfg.loadDirectorySettings()
	if err != nil {
		t.Fatalf("unexpected error: %v", err)
	}
	if configured {
		t.Fatal("expected not-configured when the file is absent")
	}
}

func TestLoadDirectorySettings_readsFileAndDefaultsThePort(t *testing.T) {
	cfg := Config{StateDir: t.TempDir()}
	body := `{"host":"dc1.acme.internal","security":"None","bindDn":"cn=svc,dc=acme,dc=com","bindPassword":"secret"}`
	if err := os.WriteFile(cfg.directorySettingsPath(), []byte(body), 0o600); err != nil {
		t.Fatal(err)
	}

	settings, configured, err := cfg.loadDirectorySettings()
	if err != nil || !configured {
		t.Fatalf("configured=%v err=%v", configured, err)
	}
	if settings.Host != "dc1.acme.internal" || settings.BindDN != "cn=svc,dc=acme,dc=com" {
		t.Fatalf("unexpected settings %+v", settings)
	}
	if settings.Port != 389 {
		t.Fatalf("expected the default plain-LDAP port 389, got %d", settings.Port)
	}
}

func TestLoadDirectorySettings_defaultsLdapsPort(t *testing.T) {
	cfg := Config{StateDir: t.TempDir()}
	if err := os.WriteFile(cfg.directorySettingsPath(), []byte(`{"host":"dc1","security":"Ldaps"}`), 0o600); err != nil {
		t.Fatal(err)
	}

	settings, _, err := cfg.loadDirectorySettings()
	if err != nil {
		t.Fatal(err)
	}
	if settings.Port != 636 {
		t.Fatalf("expected the default LDAPS port 636, got %d", settings.Port)
	}
}

func TestDirectorySyncPayload_parsesCamelCase(t *testing.T) {
	const raw = `{"connectionId":"c1","baseDn":"dc=acme,dc=com","userSearchFilter":"(objectClass=user)","emailAttribute":"mail","externalIdAttribute":"objectGUID"}`

	var payload DirectorySyncPayload
	if err := json.Unmarshal([]byte(raw), &payload); err != nil {
		t.Fatal(err)
	}
	if payload.BaseDN != "dc=acme,dc=com" || payload.UserSearchFilter != "(objectClass=user)" {
		t.Fatalf("unexpected payload %+v", payload)
	}
	if payload.EmailAttribute != "mail" || payload.ExternalIDAttribute != "objectGUID" {
		t.Fatalf("attribute map not parsed: %+v", payload)
	}
}
