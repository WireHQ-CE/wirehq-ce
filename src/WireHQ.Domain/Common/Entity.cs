namespace WireHQ.Domain.Common;

/// <summary>
/// Base class for all entities. Identity is by <see cref="Id"/> (a time-ordered UUIDv7),
/// not by reference or value. Equality is identity-based so EF change tracking and domain
/// logic agree on what "the same entity" means.
/// </summary>
public abstract class Entity : IEquatable<Entity>
{
    protected Entity(Guid id) => Id = id;

    // Parameterless ctor for EF Core materialization.
    protected Entity()
    {
    }

    public Guid Id { get; protected init; }

    public bool Equals(Entity? other) => other is not null && GetType() == other.GetType() && Id == other.Id;

    public override bool Equals(object? obj) => obj is Entity entity && Equals(entity);

    public override int GetHashCode() => HashCode.Combine(GetType(), Id);

    public static bool operator ==(Entity? left, Entity? right) => Equals(left, right);

    public static bool operator !=(Entity? left, Entity? right) => !Equals(left, right);
}
