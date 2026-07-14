namespace WireHQ.Domain.Common;

/// <summary>
/// Base class for value objects — types defined by their attributes, not an identity
/// (e.g. <c>Email</c>, <c>Slug</c>). Two value objects are equal when their components are
/// equal. Value objects are immutable and self-validating.
/// </summary>
public abstract class ValueObject : IEquatable<ValueObject>
{
    /// <summary>The components that define equality for this value object.</summary>
    protected abstract IEnumerable<object?> GetEqualityComponents();

    public bool Equals(ValueObject? other) =>
        other is not null &&
        GetType() == other.GetType() &&
        GetEqualityComponents().SequenceEqual(other.GetEqualityComponents());

    public override bool Equals(object? obj) => obj is ValueObject other && Equals(other);

    public override int GetHashCode() =>
        GetEqualityComponents().Aggregate(default(HashCode), (hash, component) =>
        {
            hash.Add(component);
            return hash;
        }).ToHashCode();

    public static bool operator ==(ValueObject? left, ValueObject? right) => Equals(left, right);

    public static bool operator !=(ValueObject? left, ValueObject? right) => !Equals(left, right);
}
