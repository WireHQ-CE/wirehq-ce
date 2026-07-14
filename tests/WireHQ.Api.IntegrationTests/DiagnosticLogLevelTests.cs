using FluentAssertions;
using Serilog.Events;
using Serilog.Parsing;
using WireHQ.Api.Observability;
using Xunit;

namespace WireHQ.Api.IntegrationTests;

/// <summary>
/// Per-tenant diagnostic verbosity (docs/15 §4): the in-memory store is time-boxed and lock-free, and the
/// Serilog filter keeps Information+ for everyone while letting Debug through only for tenants whose window is
/// open. Plain unit tests (no host).
/// </summary>
public sealed class DiagnosticLogLevelTests
{
    private static readonly MessageTemplate Template = new MessageTemplateParser().Parse("test");

    [Fact]
    public void Store_enables_disables_and_expires()
    {
        var store = new DiagnosticModeStore();
        var org = Guid.NewGuid();

        store.IsEnabled(org).Should().BeFalse();

        store.Enable(org, DateTimeOffset.UtcNow.AddMinutes(10));
        store.IsEnabled(org).Should().BeTrue();
        store.Active().Should().ContainSingle(w => w.Key == org);

        store.Disable(org);
        store.IsEnabled(org).Should().BeFalse();

        // An already-expired window never reads as enabled and is evicted from Active().
        store.Enable(org, DateTimeOffset.UtcNow.AddMinutes(-1));
        store.IsEnabled(org).Should().BeFalse();
        store.Active().Should().BeEmpty();
    }

    [Fact]
    public void Filter_keeps_information_always_and_debug_only_for_tenants_in_diagnostic_mode()
    {
        var store = new DiagnosticModeStore();
        var filter = new DiagnosticLogFilter(store);
        var org = Guid.NewGuid();
        var orgProperty = new LogEventProperty("OrgId", new ScalarValue(org));

        // Information+ is always emitted, with or without tenant context.
        filter.IsEnabled(Event(LogEventLevel.Information)).Should().BeTrue();
        filter.IsEnabled(Event(LogEventLevel.Warning, orgProperty)).Should().BeTrue();

        // Debug is dropped without diagnostic mode (and dropped outright with no tenant context).
        filter.IsEnabled(Event(LogEventLevel.Debug)).Should().BeFalse();
        filter.IsEnabled(Event(LogEventLevel.Debug, orgProperty)).Should().BeFalse();

        // Open the window → this tenant's Debug flows; closing it stops again.
        store.Enable(org, DateTimeOffset.UtcNow.AddMinutes(10));
        filter.IsEnabled(Event(LogEventLevel.Debug, orgProperty)).Should().BeTrue();
        // A different tenant is unaffected.
        filter.IsEnabled(Event(LogEventLevel.Debug, new LogEventProperty("OrgId", new ScalarValue(Guid.NewGuid()))))
            .Should().BeFalse();

        store.Disable(org);
        filter.IsEnabled(Event(LogEventLevel.Debug, orgProperty)).Should().BeFalse();
    }

    private static LogEvent Event(LogEventLevel level, params LogEventProperty[] properties) =>
        new(DateTimeOffset.UtcNow, level, exception: null, Template, properties);
}
