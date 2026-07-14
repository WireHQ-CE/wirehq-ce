namespace WireHQ.Domain.Common;

/// <summary>
/// An aggregate root: the only kind of entity loaded, saved, and referenced from outside its
/// aggregate, and the only place domain events are raised. Consistency boundaries and
/// invariants live here. The pipeline drains <see cref="DomainEvents"/> after a successful
/// commit and dispatches them.
/// </summary>
public abstract class AggregateRoot : Entity
{
    private readonly List<IDomainEvent> _domainEvents = [];

    protected AggregateRoot(Guid id)
        : base(id)
    {
    }

    protected AggregateRoot()
    {
    }

    public IReadOnlyCollection<IDomainEvent> DomainEvents => _domainEvents.AsReadOnly();

    protected void Raise(IDomainEvent domainEvent) => _domainEvents.Add(domainEvent);

    public void ClearDomainEvents() => _domainEvents.Clear();
}
