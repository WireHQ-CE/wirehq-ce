namespace WireHQ.Domain.Common;

/// <summary>
/// Marks a domain event. Events are raised by aggregates and dispatched after the owning
/// transaction commits (see the UnitOfWork pipeline behavior). They are how modules react to
/// each other without direct references — the key to keeping the monolith decomposable.
/// </summary>
/// <remarks>
/// Deliberately framework-free: the Domain has no idea <em>how</em> events are dispatched.
/// The Application layer adapts these to MediatR notifications when publishing, so no
/// messaging dependency leaks into the model.
/// </remarks>
public interface IDomainEvent
{
    Guid EventId => Guid.NewGuid();

    DateTimeOffset OccurredOnUtc => DateTimeOffset.UtcNow;
}
