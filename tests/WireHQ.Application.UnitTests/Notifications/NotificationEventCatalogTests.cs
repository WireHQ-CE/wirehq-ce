using FluentAssertions;
using WireHQ.Application.Features.Notifications;
using WireHQ.Domain.Notifications;
using Xunit;

namespace WireHQ.Application.UnitTests.Notifications;

/// <summary>Guards that the rule-editor event catalog and the domain's critical-event set (the SMS validator's source
/// of truth) can never drift: every curated critical action must be offerable in the picker as <c>IsCritical</c>, and
/// nothing the picker flags critical may be a glob or absent from the domain set.</summary>
public sealed class NotificationEventCatalogTests
{
    private static IReadOnlyList<NotificationEventTypeDto> Catalog()
    {
        var result = new ListNotificationEventTypesQueryHandler()
            .Handle(new ListNotificationEventTypesQuery(), CancellationToken.None).GetAwaiter().GetResult();
        result.IsSuccess.Should().BeTrue();
        return result.Value;
    }

    [Fact]
    public void Every_domain_critical_action_is_offered_in_the_catalog_as_critical()
    {
        var critical = Catalog().Where(e => e.IsCritical).Select(e => e.Pattern).ToHashSet();

        critical.Should().BeEquivalentTo(NotificationCriticalEvents.Actions,
            "the SMS picker and the server validator must offer exactly the same critical set");
    }

    [Fact]
    public void No_critical_catalog_entry_is_a_glob()
    {
        // A glob flagged critical would let an SMS rule pick a wildcard from the picker yet be rejected by the server.
        Catalog().Where(e => e.IsCritical).Should().OnlyContain(e => !e.Pattern.Contains('*'));
    }

    [Fact]
    public void The_broad_globs_are_not_flagged_critical()
    {
        Catalog().Where(e => e.Pattern.Contains('*')).Should().OnlyContain(e => !e.IsCritical);
    }
}
