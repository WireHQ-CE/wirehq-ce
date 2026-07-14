package agent

import (
	"encoding/json"
	"strings"
	"testing"
)

// The correlation id (ADR-030) must survive the wire both ways: WireHQ stamps it on the job bundle as
// `correlationId`, and the agent echoes it back on the result under the same key. A tag typo on either
// struct would silently break the browser → API → job → agent → back trace, so lock the contract here.
func TestJobDecodesCorrelationId(t *testing.T) {
	const payload = `{"jobId":"j1","instanceId":"i1","interfaceName":"wg0","bundle":"[Interface]","signature":"sig","agentManaged":true,"correlationId":"0af7651916cd43dd8448eb211c80319c"}`

	var job Job
	if err := json.Unmarshal([]byte(payload), &job); err != nil {
		t.Fatalf("unmarshal job: %v", err)
	}
	if job.CorrelationID != "0af7651916cd43dd8448eb211c80319c" {
		t.Fatalf("correlationId not decoded: got %q", job.CorrelationID)
	}
}

func TestJobResultEncodesCorrelationId(t *testing.T) {
	encoded, err := json.Marshal(jobResult{Status: "Succeeded", CorrelationID: "0af7651916cd43dd8448eb211c80319c"})
	if err != nil {
		t.Fatalf("marshal result: %v", err)
	}
	if !strings.Contains(string(encoded), `"correlationId":"0af7651916cd43dd8448eb211c80319c"`) {
		t.Fatalf("correlationId not encoded on result: %s", encoded)
	}
}

// The diagnostics batch must serialise to the camelCase shape the gateway's AgentDiagnosticsRequest binds
// (docs/15 §9). A tag typo would make the gateway drop the edge telemetry silently, so lock the contract.
func TestDiagnosticsRequestEncoding(t *testing.T) {
	req := diagnosticsRequest{
		JobID:         "0000000a-000b-000c-000d-00000000000e",
		InstanceID:    "0000000a-000b-000c-000d-00000000000f",
		CorrelationID: "0af7651916cd43dd8448eb211c80319c",
		Events: []DiagEvent{
			{Name: "apply", DurationMs: 41.5, Level: "info", Outcome: "ok"},
		},
	}
	encoded, err := json.Marshal(req)
	if err != nil {
		t.Fatalf("marshal diagnostics: %v", err)
	}

	for _, want := range []string{`"jobId":"`, `"instanceId":"`, `"correlationId":"`, `"events":[`, `"name":"apply"`, `"outcome":"ok"`} {
		if !strings.Contains(string(encoded), want) {
			t.Fatalf("diagnostics missing %s: %s", want, encoded)
		}
	}

	// An empty jobId must be omitted so it binds to a nullable Guid server-side (not "" → bind error).
	empty, _ := json.Marshal(diagnosticsRequest{Events: []DiagEvent{{Name: "heartbeat"}}})
	if strings.Contains(string(empty), `"jobId"`) {
		t.Fatalf("empty jobId should be omitted: %s", empty)
	}
}
