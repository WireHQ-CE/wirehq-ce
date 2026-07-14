package agent

import (
	"testing"

	"github.com/go-ldap/ldap/v3"
)

func TestFormatGUID(t *testing.T) {
	got := formatGUID([]byte{0, 1, 2, 3, 4, 5, 6, 7, 8, 9, 10, 11, 12, 13, 14, 15})
	want := "00010203-0405-0607-0809-0a0b0c0d0e0f"
	if got != want {
		t.Fatalf("formatGUID = %q, want %q", got, want)
	}
}

func TestMapEntry_projectsViaAttributeMap(t *testing.T) {
	entry := ldap.NewEntry("uid=alice,ou=people,dc=example,dc=org", map[string][]string{
		"mail":      {"alice@example.org"},
		"cn":        {"Alice Example"},
		"uid":       {"alice"},
		"memberOf":  {"cn=admins,dc=example,dc=org", "cn=vpn,dc=example,dc=org"},
		"entryUUID": {"11111111-2222-3333-4444-555555555555"},
	})
	payload := DirectorySyncPayload{
		EmailAttribute:       "mail",
		DisplayNameAttribute: "cn",
		UsernameAttribute:    "uid",
		MemberOfAttribute:    "memberOf",
		ExternalIDAttribute:  "entryUUID",
	}

	user, ok := mapEntry(entry, payload)
	if !ok {
		t.Fatal("expected the entry to map")
	}
	if user.Email != "alice@example.org" || user.Username != "alice" || user.DisplayName != "Alice Example" {
		t.Fatalf("unexpected user %+v", user)
	}
	if user.ExternalID != "11111111-2222-3333-4444-555555555555" {
		t.Fatalf("external id = %q", user.ExternalID)
	}
	if len(user.Groups) != 2 {
		t.Fatalf("groups = %v", user.Groups)
	}
}

func TestMapEntry_skipsEntryWithoutEmailOrExternalId(t *testing.T) {
	payload := DirectorySyncPayload{EmailAttribute: "mail", ExternalIDAttribute: "entryUUID"}

	noEmail := ldap.NewEntry("uid=x", map[string][]string{"entryUUID": {"id-1"}})
	if _, ok := mapEntry(noEmail, payload); ok {
		t.Fatal("expected skip when no email")
	}

	noId := ldap.NewEntry("uid=y", map[string][]string{"mail": {"y@example.org"}})
	if _, ok := mapEntry(noId, payload); ok {
		t.Fatal("expected skip when no external id")
	}
}

func TestDedupe(t *testing.T) {
	got := dedupe([]string{"a", "", "b", "a", "c", "b"})
	if len(got) != 3 || got[0] != "a" || got[1] != "b" || got[2] != "c" {
		t.Fatalf("dedupe = %v", got)
	}
}
