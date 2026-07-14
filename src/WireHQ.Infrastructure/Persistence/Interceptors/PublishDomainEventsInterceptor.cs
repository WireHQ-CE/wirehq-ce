using MediatR;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using WireHQ.Application.Common.Messaging;
using WireHQ.Domain.Common;

namespace WireHQ.Infrastructure.Persistence.Interceptors;

/// <summary>
/// After a successful save, dispatches the domain events raised by tracked aggregates through
/// MediatR (wrapped as <see cref="DomainEventNotification{T}"/>). Dispatching post-commit means
/// reactions observe persisted state; reliable cross-module/external delivery is the outbox's job.
/// (docs/02-architecture.md)
/// </summary>
public sealed class PublishDomainEventsInterceptor(IPublisher publisher) : SaveChangesInterceptor
{
    public override async ValueTask<int> SavedChangesAsync(
        SaveChangesCompletedEventData eventData, int result, CancellationToken cancellationToken = default)
    {
        if (eventData.Context is not null)
        {
            await DispatchAsync(eventData.Context, cancellationToken);
        }

        return await base.SavedChangesAsync(eventData, result, cancellationToken);
    }

    private async Task DispatchAsync(DbContext context, CancellationToken cancellationToken)
    {
        var aggregates = context.ChangeTracker
            .Entries<AggregateRoot>()
            .Where(e => e.Entity.DomainEvents.Count > 0)
            .Select(e => e.Entity)
            .ToList();

        var events = aggregates.SelectMany(a => a.DomainEvents).ToList();

        // Clear first so a re-entrant save can't double-dispatch.
        aggregates.ForEach(a => a.ClearDomainEvents());

        foreach (var domainEvent in events)
        {
            var notificationType = typeof(DomainEventNotification<>).MakeGenericType(domainEvent.GetType());
            var notification = Activator.CreateInstance(notificationType, domainEvent)!;
            await publisher.Publish(notification, cancellationToken);
        }
    }
}
