using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using WireHQ.Application.Abstractions;
using WireHQ.Application.Abstractions.Persistence;
using WireHQ.Domain.Webhooks;

namespace WireHQ.Application.Features.Webhooks;

/// <summary>
/// An in-memory snapshot of every org's <b>active</b> webhook subscriptions, so the outbox interceptor can match an
/// audit action to endpoints on the save path <b>without a DB query</b> (docs/26-api-keys-webhooks.md §8). Loaded
/// once on first use and refreshed on a cadence by the Api host's <c>WebhookSenderHostedService</c> — so a newly-created or
/// -deleted endpoint takes effect within the refresh interval (eventually consistent, which is fine for webhooks;
/// the "send test" path enqueues directly and doesn't depend on the cache). Singleton.
/// </summary>
public sealed class WebhookSubscriptionCache(IServiceScopeFactory scopeFactory)
{
    private readonly SemaphoreSlim _lock = new(1, 1);
    private volatile IReadOnlyDictionary<Guid, IReadOnlyList<CachedEndpoint>> _byOrg =
        new Dictionary<Guid, IReadOnlyList<CachedEndpoint>>();
    private volatile bool _loaded;

    /// <summary>Load the snapshot the first time it's needed (the process may start with endpoints already present),
    /// so no event is missed between boot and the sender's first refresh.</summary>
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

    /// <summary>Reload the snapshot (the sender loop calls this each tick so endpoint changes propagate).</summary>
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

    /// <summary>Endpoint ids in the org whose subscriptions match the audit action. Pure in-memory — no I/O.</summary>
    public IReadOnlyList<Guid> MatchingEndpoints(Guid organizationId, string action)
    {
        if (!_byOrg.TryGetValue(organizationId, out var endpoints))
        {
            return [];
        }

        List<Guid>? matches = null;
        foreach (var endpoint in endpoints)
        {
            if (endpoint.Patterns.Any(pattern => WebhookEventMatcher.Matches(pattern, action)))
            {
                (matches ??= []).Add(endpoint.EndpointId);
            }
        }

        return matches ?? (IReadOnlyList<Guid>)[];
    }

    private async Task LoadAsync(CancellationToken cancellationToken)
    {
        using var scope = scopeFactory.CreateScope();
        scope.ServiceProvider.GetRequiredService<ISettableTenantContext>().SetBypass();
        var dbContext = scope.ServiceProvider.GetRequiredService<IApplicationDbContext>();

        var endpoints = await dbContext.WebhookEndpoints
            .IgnoreQueryFilters()
            .Where(e => e.Status == WebhookEndpointStatus.Active)
            .Select(e => new { e.Id, e.OrganizationId, Patterns = e.EventTypes.Select(s => s.Pattern).ToArray() })
            .ToListAsync(cancellationToken);

        _byOrg = endpoints
            .GroupBy(e => e.OrganizationId)
            .ToDictionary(
                g => g.Key,
                g => (IReadOnlyList<CachedEndpoint>)g.Select(e => new CachedEndpoint(e.Id, e.Patterns)).ToList());
        _loaded = true;
    }

    public sealed record CachedEndpoint(Guid EndpointId, IReadOnlyList<string> Patterns);
}
