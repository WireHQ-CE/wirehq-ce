using System.Text.Json;
using FluentAssertions;
using WireHQ.Application.Updates;
using Xunit;

namespace WireHQ.Application.UnitTests.Updates;

/// <summary>
/// The update manifest's severity parse must be forward-compatible (docs/30 U-13a): a future WireHQ release may
/// publish a severity tier an OLDER CE install doesn't recognise, and a strict parse would reject the ENTIRE
/// signed manifest — blinding that install to a security release (a false all-clear that CANNOT be retrofitted to
/// already-deployed installs). An unknown tier must degrade to <c>None</c> while the manifest — and its security
/// flag — survive; loudness is driven by <c>security</c>/<c>unsupported</c>, not severity.
/// </summary>
public sealed class UpdateManifestSeverityTests
{
    private static readonly JsonSerializerOptions Web = new(JsonSerializerDefaults.Web);

    [Theory]
    [InlineData("apocalyptic")] // an unknown future tier
    [InlineData("999")]         // a numeric string that isn't a defined value
    [InlineData("")]            // empty
    public void An_unknown_severity_degrades_to_none_and_keeps_the_manifest(string severity)
    {
        var json = $$"""{"latestVersion":"0.99.0","security":true,"severity":"{{severity}}","requiresMigration":true}""";

        var manifest = JsonSerializer.Deserialize<UpdateManifest>(json, Web);

        manifest.Should().NotBeNull();                   // NOT rejected — the whole manifest survives
        manifest!.LatestVersion.Should().Be("0.99.0");
        manifest.Security.Should().BeTrue();             // the loud signal survives an unknown tier
        manifest.RequiresMigration.Should().BeTrue();
        manifest.Severity.Should().Be(UpdateSeverity.None);
    }

    [Theory]
    [InlineData("high", UpdateSeverity.High)]
    [InlineData("Critical", UpdateSeverity.Critical)]
    [InlineData("low", UpdateSeverity.Low)]
    public void A_known_severity_still_parses_case_insensitively(string severity, UpdateSeverity expected)
    {
        var json = $$"""{"latestVersion":"0.99.0","severity":"{{severity}}"}""";

        var manifest = JsonSerializer.Deserialize<UpdateManifest>(json, Web);

        manifest!.Severity.Should().Be(expected);
    }

    [Fact]
    public void Severity_serializes_as_its_string_name_for_the_frontend()
    {
        var json = JsonSerializer.Serialize(
            new UpdateManifest { LatestVersion = "1.0.0", Severity = UpdateSeverity.High }, Web);

        json.Should().Contain("\"severity\":\"High\"");
    }
}
