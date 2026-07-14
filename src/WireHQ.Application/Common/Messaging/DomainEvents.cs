using MediatR;
using WireHQ.Domain.Common;

namespace WireHQ.Application.Common.Messaging;

/// <summary>
/// Adapts a framework-free <see cref="IDomainEvent"/> into a MediatR notification so the
/// existing in-process dispatcher can fan it out — without any messaging dependency leaking
/// into the Domain.
/// </summary>
public sealed record DomainEventNotification<TDomainEvent>(TDomainEvent DomainEvent) : INotification
    where TDomainEvent : IDomainEvent;

/// <summary>
/// Convenience base for reacting to a domain event. Implement this in any module to respond to
/// another aggregate's event (e.g. Audit reacting to <c>MembershipRevoked</c>) — the reaction
/// is decoupled, which is what keeps modules independently extractable.
/// </summary>
public abstract class DomainEventHandler<TDomainEvent> : INotificationHandler<DomainEventNotification<TDomainEvent>>
    where TDomainEvent : IDomainEvent
{
    public Task Handle(DomainEventNotification<TDomainEvent> notification, CancellationToken cancellationToken) =>
        HandleAsync(notification.DomainEvent, cancellationToken);

    protected abstract Task HandleAsync(TDomainEvent domainEvent, CancellationToken cancellationToken);
}
