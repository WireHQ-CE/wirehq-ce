namespace WireHQ.Api.Observability;

/// <summary>
/// The redaction policy for telemetry leaving the process (docs/15 §4/§6). A defence-in-depth net over the
/// app's "secrets are never logged" discipline: even if a secret reaches a log property or message, it is
/// scrubbed before it leaves the process. Applied to logs by <see cref="RedactionEnricher"/> (console + OTLP);
/// the Collector adds a second pass for span attributes.
/// </summary>
public interface IRedactionPolicy
{
    /// <summary>True if a structured-log property with this name should have its value fully redacted.</summary>
    bool IsSensitiveProperty(string name);

    /// <summary>Mask any secret-shaped substrings within free text; returns the input unchanged if clean.</summary>
    string Redact(string text);
}
