using Serilog.Core;
using Serilog.Events;

namespace WireHQ.Api.Observability;

/// <summary>
/// A Serilog enricher that scrubs secrets from every log event before it reaches a sink — console AND OTLP
/// (docs/15 §4). Sensitive-named properties are fully masked; other string values are scanned for
/// secret-shaped substrings. Registered as an <see cref="ILogEventEnricher"/> so Serilog's
/// <c>ReadFrom.Services</c> applies it to every sink, and the redacted values flow into the rendered message too.
/// </summary>
public sealed class RedactionEnricher(IRedactionPolicy policy) : ILogEventEnricher
{
    public void Enrich(LogEvent logEvent, ILogEventPropertyFactory propertyFactory)
    {
        foreach (var property in logEvent.Properties.ToArray())
        {
            if (Scrub(property.Key, property.Value) is { } replacement)
            {
                logEvent.AddOrUpdateProperty(new LogEventProperty(property.Key, replacement));
            }
        }
    }

    private LogEventPropertyValue? Scrub(string name, LogEventPropertyValue value)
    {
        if (policy.IsSensitiveProperty(name))
        {
            return new ScalarValue(RedactionPolicy.Mask);
        }

        if (value is ScalarValue { Value: string text })
        {
            var redacted = policy.Redact(text);
            return redacted != text ? new ScalarValue(redacted) : null;
        }

        return null;
    }
}
