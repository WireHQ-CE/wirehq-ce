using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using WireHQ.Application.Abstractions;
using WireHQ.Application.Abstractions.Persistence;
using WireHQ.Domain.Notifications;
using WireHQ.Domain.Webhooks;

namespace WireHQ.Application.Features.Notifications;

/// <summary>
/// An in-memory snapshot of every org's <b>enabled</b> notification rules, so the outbox interceptor can match an
/// audit action to rules on the save path <b>without a DB query</b> (docs/35-notifications.md §4.1) — mirroring the
/// webhook subscription cache. Loaded once on first use and refreshed on a cadence by the Api host's notification
/// sender service. Singleton.
/// <para>
/// Enforces the <b>self-loop denylist</b> (docs/35 §4.6): the subsystem's own <c>notifications.*</c> audit actions
/// are never matchable, so a rule can never be triggered by the act of managing rules — a hard invariant, not merely
/// "absent from the curated list".
/// </para>
/// </summary>
public sealed class NotificationRouteCache(IServiceScopeFactory scopeFactory)
{
    /// <summary>Audit actions the dispatch subsystem itself emits — never routable, to prevent a feedback loop.</summary>
    private const string SelfActionPrefix = "notifications.";

    private readonly SemaphoreSlim _lock = new(1, 1);
    private volatile IReadOnlyDictionary<Guid, IReadOnlyList<CachedRule>> _byOrg =
        new Dictionary<Guid, IReadOnlyList<CachedRule>>();
    private volatile bool _loaded;

    /// <summary>A rule matched to an audit action: its id and the digest cadence to stamp on the captured job (so the
    /// job can be routed to the immediate-expand or the digest-flush drain phase — docs/35 §4.5).</summary>
    public readonly record struct MatchedRule(Guid RuleId, DigestCadence Cadence);

    public async Task EnsureLoadedAsync(CancellationToken cancellationToken)
    {
        if (_loaded)
        {
            return;
        }

        await _lock.WaitAsync(cancellationToken);
        try
        {
            if (!_loaded)
            {
                await LoadAsync(cancellationToken);
            }
        }
        finally
        {
            _lock.Release();
        }
    }

    public async Task RefreshAsync(CancellationToken cancellationToken)
    {
        await _lock.WaitAsync(cancellationToken);
        try
        {
            await LoadAsync(cancellationToken);
        }
        finally
        {
            _lock.Release();
        }
    }

    /// <summary>Rules in the org whose pattern matches the audit action (with the cadence to stamp). Pure in-memory —
    /// no I/O. Returns nothing for a denylisted (self-emitted) action, so dispatch can never self-trigger.</summary>
    public IReadOnlyList<MatchedRule> MatchingRules(Guid organizationId, string action)
    {
        if (action.StartsWith(SelfActionPrefix, StringComparison.Ordinal))
        {
            return [];
        }

        if (!_byOrg.TryGetValue(organizationId, out var rules))
        {
            return [];
        }

        // A multi-pattern (advanced) rule appears once per pattern in the cache, so an action matching >1 of its globs
        // must still yield exactly ONE job for that rule — dedup rule ids with a seen-set (no duplicate jobs per event).
        HashSet<Guid>? seen = null;
        List<MatchedRule>? matches = null;
        foreach (var rule in rules)
        {
            if (WebhookEventMatcher.Matches(rule.Pattern, action) && (seen ??= []).Add(rule.RuleId))
            {
                (matches ??= []).Add(new MatchedRule(rule.RuleId, rule.Cadence));
            }
        }

        return matches ?? (IReadOnlyList<MatchedRule>)[];
    }

    private async Task LoadAsync(CancellationToken cancellationToken)
    {
        using var scope = scopeFactory.CreateScope();
        scope.ServiceProvider.GetRequiredService<ISettableTenantContext>().SetBypass();
        var dbContext = scope.ServiceProvider.GetRequiredService<IApplicationDbContext>();

        // Load each rule's primary pattern PLUS any advanced additional patterns, so the cache holds one CachedRule per
        // (rule, pattern). MatchingRules dedups rule ids, so a rule matching an action on several of its patterns still
        // produces exactly one job (docs/35 Wave 3 multi-pattern).
        var rules = await dbContext.NotificationRules
            .IgnoreQueryFilters()
            .Where(r => r.Enabled)
            .Select(r => new { r.Id, r.OrganizationId, r.EventPattern, r.DigestCadence, Additional = r.AdditionalPatterns.Select(p => p.Pattern).ToList() })
            .ToListAsync(cancellationToken);

        _byOrg = rules
            .GroupBy(r => r.OrganizationId)
            .ToDictionary(
                g => g.Key,
                g => (IReadOnlyList<CachedRule>)g
                    .SelectMany(r => r.Additional.Prepend(r.EventPattern).Select(pattern => new CachedRule(r.Id, pattern, r.DigestCadence)))
                    .ToList());
        _loaded = true;
    }

    public sealed record CachedRule(Guid RuleId, string Pattern, DigestCadence Cadence);
}
