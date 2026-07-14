using System.Collections.Concurrent;
using WireHQ.Application.Abstractions;

namespace WireHQ.Api.Observability;

/// <summary>
/// In-memory <see cref="IDiagnosticModeStore"/> — a lock-free map of tenant → expiry. Read on every Debug log
/// event, so it stays allocation-free on the hot path; expired windows are evicted lazily on read. Transient
/// across restarts by design (docs/15 §4).
/// </summary>
public sealed class DiagnosticModeStore : IDiagnosticModeStore
{
    private readonly ConcurrentDictionary<Guid, DateTimeOffset> _until = new();

    public void Enable(Guid organizationId, DateTimeOffset until) => _until[organizationId] = until;

    public void Disable(Guid organizationId) => _until.TryRemove(organizationId, out _);

    public bool IsEnabled(Guid organizationId)
    {
        if (_until.TryGetValue(organizationId, out var until))
        {
            if (until > DateTimeOffset.UtcNow)
            {
                return true;
            }

            _until.TryRemove(organizationId, out _); // lazily evict the expired window
        }

        return false;
    }

    public IReadOnlyCollection<KeyValuePair<Guid, DateTimeOffset>> Active()
    {
        var now = DateTimeOffset.UtcNow;
        return _until.Where(window => window.Value > now).ToArray();
    }
}
