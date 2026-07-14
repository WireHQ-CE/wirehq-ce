using System.Diagnostics;

namespace WireHQ.Application.Common.Observability;

/// <summary>
/// The Application layer's OpenTelemetry <see cref="ActivitySource"/> — a span per use case (docs/15 §6).
/// Registered on the tracer in the host (<c>AddObservability</c> calls <c>AddSource</c> with
/// <see cref="ActivitySourceName"/>), so MediatR spans nest under the ASP.NET request span and share its
/// trace id (= the correlation reference, ADR-030).
/// </summary>
public static class ApplicationTelemetry
{
    public const string ActivitySourceName = "WireHQ.Application";

    public static readonly ActivitySource Source = new(ActivitySourceName);
}
