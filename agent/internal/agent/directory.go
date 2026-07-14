package agent

import (
	"context"
	"encoding/json"
	"fmt"
	"log"
	"os"
	"path/filepath"
)

// DirectoryTaskKind is the task discriminator for an LDAP/AD directory sync (matches the SaaS
// DirectorySyncTaskProvider). (docs/23-ldap-directory-sync.md §6/§7)
const DirectoryTaskKind = "directory.sync"

// DirectorySettings is the agent-LOCAL directory configuration — the bind host + credentials the agent uses to
// reach the customer's directory. It lives on the agent host (<state>/directory.json) and is NEVER sent by
// WireHQ: agent-local credential custody (D-3) means WireHQ never holds the bind secret. The per-sync query
// spec (base DN, filter, attribute map) arrives on the task from WireHQ.
type DirectorySettings struct {
	Host               string `json:"host"`
	Port               int    `json:"port"`
	Security           string `json:"security"` // Ldaps | StartTls | None
	BindDN             string `json:"bindDn"`
	BindPassword       string `json:"bindPassword"`
	CaCertPEM          string `json:"caCertPem,omitempty"`
	InsecureSkipVerify bool   `json:"insecureSkipVerify,omitempty"`
}

// DirectorySyncPayload is the query spec WireHQ sends on a directory-sync task (no host, no credentials — D-3).
type DirectorySyncPayload struct {
	ConnectionID         string `json:"connectionId"`
	BaseDN               string `json:"baseDn"`
	UserSearchFilter     string `json:"userSearchFilter"`
	UserSearchBase       string `json:"userSearchBase"`
	EmailAttribute       string `json:"emailAttribute"`
	DisplayNameAttribute string `json:"displayNameAttribute"`
	UsernameAttribute    string `json:"usernameAttribute"`
	MemberOfAttribute    string `json:"memberOfAttribute"`
	ExternalIDAttribute  string `json:"externalIdAttribute"`
}

// DirectoryUser is one pulled directory user, mapped to the provider-neutral shape WireHQ reconciles.
type DirectoryUser struct {
	ExternalID  string   `json:"externalId"`
	Email       string   `json:"email"`
	DisplayName string   `json:"displayName"`
	Username    string   `json:"username"`
	Groups      []string `json:"groups"`
}

// directorySnapshot is the result body posted back to the gateway.
type directorySnapshot struct {
	Users []DirectoryUser `json:"users"`
}

func (c Config) directorySettingsPath() string { return filepath.Join(c.StateDir, "directory.json") }

// loadDirectorySettings reads the agent-local directory config. The bool is false (no error) when the file is
// absent — the agent then simply skips directory-sync tasks (this host isn't a directory gateway).
func (c Config) loadDirectorySettings() (DirectorySettings, bool, error) {
	data, err := os.ReadFile(c.directorySettingsPath())
	if os.IsNotExist(err) {
		return DirectorySettings{}, false, nil
	}
	if err != nil {
		return DirectorySettings{}, false, err
	}
	var s DirectorySettings
	if err := json.Unmarshal(data, &s); err != nil {
		return DirectorySettings{}, false, fmt.Errorf("parse %s: %w", c.directorySettingsPath(), err)
	}
	if s.Port == 0 {
		s.Port = defaultDirectoryPort(s.Security)
	}
	return s, true, nil
}

func defaultDirectoryPort(security string) int {
	if security == "Ldaps" {
		return 636
	}
	return 389
}

// processTasks drains the generic agent-task channel and runs each directory-sync task locally.
func (l *Loop) processTasks(ctx context.Context) {
	tasks, err := l.client.Tasks(ctx)
	if err != nil {
		log.Printf("poll tasks failed: %v", err)
		return
	}
	if len(tasks) == 0 {
		return
	}

	settings, configured, err := l.cfg.loadDirectorySettings()
	if err != nil {
		log.Printf("read directory settings failed: %v", err)
		return
	}

	for _, task := range tasks {
		if task.Kind != DirectoryTaskKind {
			continue // an unknown task kind — a newer server feature this agent doesn't implement yet
		}
		if !configured {
			log.Printf("directory-sync task %s skipped: no local directory config (%s)", task.TaskID, l.cfg.directorySettingsPath())
			continue
		}
		l.processDirectoryTask(ctx, task, settings)
	}
}

// processDirectoryTask runs one directory sync: parse the query spec, bind + pull locally, post the snapshot.
// On a directory error the agent does NOT post (the task stays pending and retries next poll) — an unreachable
// directory must never be reported as "zero users" (which could deactivate everyone).
func (l *Loop) processDirectoryTask(ctx context.Context, task Task, settings DirectorySettings) {
	var payload DirectorySyncPayload
	if err := json.Unmarshal([]byte(task.PayloadJSON), &payload); err != nil {
		log.Printf("directory-sync task %s: bad payload: %v", task.TaskID, err)
		return
	}

	users, err := runDirectorySync(settings, payload)
	if err != nil {
		log.Printf("directory-sync task %s: pull failed (will retry): %v", task.TaskID, err)
		return
	}

	if err := l.client.ReportTaskResult(ctx, task.TaskID, directorySnapshot{Users: users}); err != nil {
		log.Printf("directory-sync task %s: report failed: %v", task.TaskID, err)
		return
	}
	log.Printf("directory-sync task %s: pulled %d users", task.TaskID, len(users))
}
