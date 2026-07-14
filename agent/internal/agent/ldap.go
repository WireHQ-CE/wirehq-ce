package agent

import (
	"crypto/tls"
	"crypto/x509"
	"fmt"

	"github.com/go-ldap/ldap/v3"
)

// runDirectorySync binds to the local directory with the agent-local credentials and pulls the users matching
// the task's query spec, mapped to the provider-neutral shape. Plain LDAP / LDAPS / StartTLS per the local
// settings' Security. (docs/23-ldap-directory-sync.md §6)
func runDirectorySync(settings DirectorySettings, payload DirectorySyncPayload) ([]DirectoryUser, error) {
	conn, err := dialDirectory(settings)
	if err != nil {
		return nil, err
	}
	defer conn.Close()

	if err := conn.Bind(settings.BindDN, settings.BindPassword); err != nil {
		return nil, fmt.Errorf("bind: %w", err)
	}

	base := payload.UserSearchBase
	if base == "" {
		base = payload.BaseDN
	}

	attrs := dedupe([]string{
		payload.EmailAttribute, payload.DisplayNameAttribute, payload.UsernameAttribute,
		payload.MemberOfAttribute, payload.ExternalIDAttribute,
	})

	request := ldap.NewSearchRequest(
		base, ldap.ScopeWholeSubtree, ldap.NeverDerefAliases, 0, 0, false,
		payload.UserSearchFilter, attrs, nil)

	result, err := conn.SearchWithPaging(request, 500)
	if err != nil {
		return nil, fmt.Errorf("search: %w", err)
	}

	users := make([]DirectoryUser, 0, len(result.Entries))
	for _, entry := range result.Entries {
		if user, ok := mapEntry(entry, payload); ok {
			users = append(users, user)
		}
	}
	return users, nil
}

// mapEntry projects one LDAP entry onto a DirectoryUser via the payload's attribute map. An entry we can't key
// (no external id) or contact (no email) is skipped — not a user we can provision.
func mapEntry(entry *ldap.Entry, payload DirectorySyncPayload) (DirectoryUser, bool) {
	email := entry.GetAttributeValue(payload.EmailAttribute)
	externalID := externalID(entry, payload.ExternalIDAttribute)
	if email == "" || externalID == "" {
		return DirectoryUser{}, false
	}

	return DirectoryUser{
		ExternalID:  externalID,
		Email:       email,
		DisplayName: entry.GetAttributeValue(payload.DisplayNameAttribute),
		Username:    entry.GetAttributeValue(payload.UsernameAttribute),
		Groups:      entry.GetAttributeValues(payload.MemberOfAttribute),
	}, true
}

// externalID reads the stable directory id: AD's objectGUID is a 16-byte binary value → a canonical UUID
// string; a string id (LDAP entryUUID) passes through. The formatting only has to be STABLE across syncs (it's
// the idempotency key WireHQ stores), not match any particular vendor layout. (D-4)
func externalID(entry *ldap.Entry, attribute string) string {
	raw := entry.GetRawAttributeValue(attribute)
	if len(raw) == 16 {
		return formatGUID(raw)
	}
	return entry.GetAttributeValue(attribute)
}

func formatGUID(b []byte) string {
	return fmt.Sprintf("%x-%x-%x-%x-%x", b[0:4], b[4:6], b[6:8], b[8:10], b[10:16])
}

func dialDirectory(settings DirectorySettings) (*ldap.Conn, error) {
	address := fmt.Sprintf("%s:%d", settings.Host, settings.Port)
	switch settings.Security {
	case "Ldaps":
		tlsConfig, err := directoryTLS(settings)
		if err != nil {
			return nil, err
		}
		return ldap.DialURL("ldaps://"+address, ldap.DialWithTLSConfig(tlsConfig))
	case "StartTls":
		conn, err := ldap.DialURL("ldap://" + address)
		if err != nil {
			return nil, err
		}
		tlsConfig, err := directoryTLS(settings)
		if err != nil {
			conn.Close()
			return nil, err
		}
		if err := conn.StartTLS(tlsConfig); err != nil {
			conn.Close()
			return nil, fmt.Errorf("starttls: %w", err)
		}
		return conn, nil
	default: // None — plain LDAP (trusted internal network / dev)
		return ldap.DialURL("ldap://" + address)
	}
}

func directoryTLS(settings DirectorySettings) (*tls.Config, error) {
	config := &tls.Config{
		ServerName:         settings.Host,
		InsecureSkipVerify: settings.InsecureSkipVerify, //nolint:gosec // opt-in dev flag for a self-signed directory
	}
	if settings.CaCertPEM != "" {
		pool := x509.NewCertPool()
		if !pool.AppendCertsFromPEM([]byte(settings.CaCertPEM)) {
			return nil, fmt.Errorf("directory ca certificate is not valid PEM")
		}
		config.RootCAs = pool
	}
	return config, nil
}

func dedupe(values []string) []string {
	seen := make(map[string]struct{}, len(values))
	out := make([]string, 0, len(values))
	for _, v := range values {
		if v == "" {
			continue
		}
		if _, ok := seen[v]; ok {
			continue
		}
		seen[v] = struct{}{}
		out = append(out, v)
	}
	return out
}
