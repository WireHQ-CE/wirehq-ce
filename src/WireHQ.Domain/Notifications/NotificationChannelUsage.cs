using WireHQ.Domain.Common;

namespace WireHQ.Domain.Notifications;

/// <summary>
/// The <b>durable</b> per-org, per-channel, per-day send counter (docs/35-notifications.md §4.5, blocker B-6).
/// The email rate-limit and the (Wave 4) SMS daily cap read and increment this row transactionally in the sender's
/// <c>SaveChanges</c>, so a cap holds across app replicas and restarts — an in-memory Singleton counter would
/// enforce <c>replicas × N</c> and reset every deploy, defeating a spend-safety control on a channel with hard
/// per-message cost. Tenant-owned in the reused <c>identity</c> schema; keyed by (org, channel, day).
/// </summary>
public sealed class NotificationChannelUsage : Entity, ITenantOwned
{
    // EF Core
    private NotificationChannelUsage()
    {
    }

    private NotificationChannelUsage(Guid id, Guid organizationId, ChannelKind channel, DateOnly dayUtc)
        : base(id)
    {
        OrganizationId = organizationId;
        ChannelKind = channel;
        DayUtc = dayUtc;
        Count = 0;
    }

    public Guid OrganizationId { get; private set; }

    public ChannelKind ChannelKind { get; private set; }

    public DateOnly DayUtc { get; private set; }

    public int Count { get; private set; }

    public static NotificationChannelUsage Start(Guid organizationId, ChannelKind channel, DateOnly dayUtc) =>
        new(Guid.CreateVersion7(), organizationId, channel, dayUtc);

    /// <summary>Record one send against the day's budget.</summary>
    public void Increment() => Count++;

    /// <summary>True when this day's count has reached the given cap (a cap of 0 or less means "no cap").</summary>
    public bool HasReached(int cap) => cap > 0 && Count >= cap;
}
