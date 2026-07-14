using System.Text;
using System.Text.Json;
using FluentAssertions;
using WireHQ.Api.Audit;
using WireHQ.Application.Features.Audit.ExportAuditLogs;
using Xunit;

namespace WireHQ.Api.IntegrationTests;

/// <summary>
/// Pure (host-free) unit tests for the Phase-7 audit→SIEM serialisation: the <see cref="SiemActionMap"/>
/// classification and the OCSF (NDJSON) + CEF output of <see cref="AuditSiemFormatter"/>. (docs/15 §11/§16)
/// </summary>
public sealed class AuditSiemFormatterTests
{
    private static AuditExportRow Row(
        string action,
        string outcome = "Success",
        string? actorEmail = "user@example.test",
        string? targetType = "Network",
        string? targetId = "abc-123",
        string? ip = "203.0.113.7",
        string? correlation = "0af7651916cd43dd8448eb211c80319c",
        string? changes = null) =>
        new(
            OccurredAtUtc: DateTimeOffset.Parse("2026-06-29T12:34:56Z"),
            Action: action,
            ActorEmail: actorEmail,
            ActorType: "User",
            Outcome: outcome,
            TargetType: targetType,
            TargetId: targetId,
            IpAddress: ip,
            CorrelationId: correlation,
            Changes: changes);

    [Theory]
    // Authentication / session → OCSF IAM (3) / Authentication [3002]
    [InlineData("auth.login", 3, 3002, 1)]
    [InlineData("auth.logout", 3, 3002, 2)]
    [InlineData("platform.impersonation.started", 3, 3002, 1)]
    [InlineData("account.session_revoked", 3, 3002, 2)]
    // Account / identity changes → OCSF IAM (3) / Account Change [3001]
    [InlineData("account.password_changed", 3, 3001, 3)]
    [InlineData("mfa.enabled", 3, 3001, 7)]
    [InlineData("mfa.disabled", 3, 3001, 8)]
    [InlineData("identity.users.invite", 3, 3001, 1)]
    [InlineData("platform.customer.user_removed", 3, 3001, 5)]
    // Resource activity → OCSF Application Activity (6) / API Activity [6003]
    [InlineData("wg.network.created", 6, 6003, 1)]
    [InlineData("wg.config.version_revealed", 6, 6003, 2)]
    [InlineData("orch.agent.cert_rotated", 6, 6003, 3)]
    [InlineData("wg.peer.deleted", 6, 6003, 4)]
    [InlineData("identity.teams.create", 6, 6003, 1)]
    [InlineData("platform.settings.updated", 6, 6003, 3)]
    // Unknown prefix / empty → the safe API Activity fallback (never dropped)
    [InlineData("brandnew.thing.frobnicated", 6, 6003, 99)]
    [InlineData("", 6, 6003, 0)]
    public void Map_classifies_actions_by_prefix_and_verb(string action, int categoryUid, int classUid, int activityId)
    {
        var e = SiemActionMap.Map(action);

        e.CategoryUid.Should().Be(categoryUid);
        e.ClassUid.Should().Be(classUid);
        e.ActivityId.Should().Be(activityId);
        e.TypeUid.Should().Be((classUid * 100) + activityId, "type_uid = class_uid × 100 + activity_id");
    }

    [Fact]
    public void Ocsf_is_ndjson_with_one_valid_event_per_row()
    {
        var bytes = AuditSiemFormatter.ToOcsf([Row("auth.login"), Row("wg.network.created")]);
        var lines = Encoding.UTF8.GetString(bytes).Split('\n', StringSplitOptions.RemoveEmptyEntries);

        lines.Should().HaveCount(2);
        foreach (var line in lines)
        {
            var act = () => JsonDocument.Parse(line);
            act.Should().NotThrow("each NDJSON line must be a standalone JSON object");
        }
    }

    [Fact]
    public void Ocsf_event_carries_the_ocsf_envelope_and_the_diff_under_unmapped()
    {
        var changes = "[{\"field\":\"name\",\"old\":\"a\",\"new\":\"b\"}]";
        var bytes = AuditSiemFormatter.ToOcsf([Row("wg.network.updated", changes: changes)]);
        using var doc = JsonDocument.Parse(Encoding.UTF8.GetString(bytes).Trim());
        var root = doc.RootElement;

        root.GetProperty("class_uid").GetInt32().Should().Be(6003);
        root.GetProperty("activity_id").GetInt32().Should().Be(3);
        root.GetProperty("type_uid").GetInt32().Should().Be(600303);
        root.GetProperty("status").GetString().Should().Be("Success");
        root.GetProperty("severity_id").GetInt32().Should().Be(1);
        root.GetProperty("time").GetInt64().Should().Be(DateTimeOffset.Parse("2026-06-29T12:34:56Z").ToUnixTimeMilliseconds());
        root.GetProperty("actor").GetProperty("user").GetProperty("email_addr").GetString().Should().Be("user@example.test");
        root.GetProperty("src_endpoint").GetProperty("ip").GetString().Should().Be("203.0.113.7");

        var unmapped = root.GetProperty("unmapped");
        unmapped.GetProperty("wirehq.action").GetString().Should().Be("wg.network.updated");
        unmapped.GetProperty("wirehq.changes").ValueKind.Should().Be(JsonValueKind.Array, "the EF diff embeds as real JSON");
    }

    [Fact]
    public void Ocsf_marks_a_failure_outcome()
    {
        var bytes = AuditSiemFormatter.ToOcsf([Row("auth.login", outcome: "Failure")]);
        using var doc = JsonDocument.Parse(Encoding.UTF8.GetString(bytes).Trim());

        doc.RootElement.GetProperty("status").GetString().Should().Be("Failure");
        doc.RootElement.GetProperty("status_id").GetInt32().Should().Be(2);
        doc.RootElement.GetProperty("severity_id").GetInt32().Should().Be(4);
    }

    [Fact]
    public void Cef_emits_a_header_and_extension_per_row()
    {
        var bytes = AuditSiemFormatter.ToCef([Row("wg.network.created")]);
        var line = Encoding.UTF8.GetString(bytes).Trim();

        line.Should().StartWith("CEF:0|WireHQ|WireHQ|");
        var header = line.Split('|');
        header[4].Should().Be("wg.network.created", "the signature id is the action key");
        header[5].Should().Be("API Activity: Create");
        header[6].Should().Be("3", "a successful event is low severity");

        line.Should().Contain("act=wg.network.created");
        line.Should().Contain("suser=user@example.test");
        line.Should().Contain("cs1Label=correlationRef");
        line.Should().Contain("cs1=0af7651916cd43dd8448eb211c80319c");
    }

    [Fact]
    public void Cef_raises_severity_for_a_failure()
    {
        var bytes = AuditSiemFormatter.ToCef([Row("auth.login", outcome: "Failure")]);
        var line = Encoding.UTF8.GetString(bytes).Trim();

        line.Split('|')[6].Should().Be("8");
        line.Should().Contain("outcome=Failure");
    }

    [Fact]
    public void Cef_escapes_pipes_in_the_header_and_equals_in_the_extension()
    {
        // A pathological action/target exercising the CEF escaping rules.
        var row = Row("weird|action", targetId: "id=with=equals", correlation: null, targetType: null);
        var bytes = AuditSiemFormatter.ToCef([row]);
        var line = Encoding.UTF8.GetString(bytes).Trim();

        line.Should().Contain("weird\\|action", "a pipe in a CEF header field is backslash-escaped");
        line.Should().Contain("cs3=id\\=with\\=equals", "an equals in a CEF extension value is backslash-escaped");
    }
}
