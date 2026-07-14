using System.Reflection;

namespace WireHQ.Api.Observability;

/// <summary>
/// The single source of the OpenTelemetry resource identity (docs/15 §14), so the trace/metric SDK and the
/// Serilog OTLP logs sink tag telemetry identically — service name, the build that produced it, and the
/// environment. Without one source they could drift and break the log↔trace join in the backend.
/// </summary>
public static class ObservabilityResource
{
    public const string ServiceName = "wirehq-api";

    /// <summary>
    /// The data-residency region this deployment serves (docs/15 §14/§15). Tags every signal so a multi-region
    /// SaaS Collector can route/retain by region; never hardcoded — driven by <c>Observability:Region</c> and
    /// simply omitted (single-region / self-host) when unset. Uses the same <c>deployment.*</c> family as
    /// <c>deployment.environment</c>.
    /// </summary>
    public const string RegionAttribute = "deployment.region";

    /// <summary>The build version (the released SemVer), stripped of any +git-sha build metadata.</summary>
    public static string Version { get; } =
        (Assembly.GetEntryAssembly()?.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion
         ?? "unknown").Split('+')[0];

    public static string Environment(IConfiguration configuration) =>
        configuration["ASPNETCORE_ENVIRONMENT"] ?? "Production";

    /// <summary>The configured residency region, or <c>null</c> when unset (single-region / self-host).</summary>
    public static string? Region(IConfiguration configuration)
    {
        var region = configuration["Observability:Region"];
        return string.IsNullOrWhiteSpace(region) ? null : region.Trim();
    }

    /// <summary>Resource attributes for the Serilog OTLP sink (the trace/metric SDK uses ConfigureResource).</summary>
    public static IDictionary<string, object> Attributes(IConfiguration configuration)
    {
        var attributes = new Dictionary<string, object>
        {
            ["service.name"] = ServiceName,
            ["service.version"] = Version,
            ["deployment.environment"] = Environment(configuration),
        };

        if (Region(configuration) is { } region)
        {
            attributes[RegionAttribute] = region;
        }

        return attributes;
    }
}
