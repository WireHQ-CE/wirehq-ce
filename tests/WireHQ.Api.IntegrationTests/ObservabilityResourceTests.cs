using FluentAssertions;
using Microsoft.Extensions.Configuration;
using WireHQ.Api.Observability;
using Xunit;

namespace WireHQ.Api.IntegrationTests;

/// <summary>
/// Pure (host-free) unit tests for the data-residency region tagging (docs/15 §14/§15, Phase 7): every signal is
/// tagged with <c>deployment.region</c> when <c>Observability:Region</c> is configured, and the attribute is
/// omitted (never hardcoded) when it isn't.
/// </summary>
public sealed class ObservabilityResourceTests
{
    private static IConfiguration Config(params (string Key, string Value)[] values) =>
        new ConfigurationBuilder()
            .AddInMemoryCollection(values.Select(v => new KeyValuePair<string, string?>(v.Key, v.Value)))
            .Build();

    [Fact]
    public void Region_is_tagged_on_the_resource_when_configured()
    {
        var config = Config(("Observability:Region", "eu"));

        ObservabilityResource.Region(config).Should().Be("eu");
        ObservabilityResource.Attributes(config).Should().ContainKey(ObservabilityResource.RegionAttribute)
            .WhoseValue.Should().Be("eu");
    }

    [Fact]
    public void Region_is_omitted_when_unset_or_blank()
    {
        ObservabilityResource.Region(Config()).Should().BeNull();
        ObservabilityResource.Region(Config(("Observability:Region", "   "))).Should().BeNull();

        // Never hardcode a region: a single-region/self-host resource carries no region attribute.
        ObservabilityResource.Attributes(Config()).Should().NotContainKey(ObservabilityResource.RegionAttribute);
    }

    [Fact]
    public void Region_is_trimmed()
    {
        ObservabilityResource.Region(Config(("Observability:Region", " us "))).Should().Be("us");
    }
}
