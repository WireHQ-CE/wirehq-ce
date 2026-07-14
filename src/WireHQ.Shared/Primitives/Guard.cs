using System.Runtime.CompilerServices;

namespace WireHQ.Shared.Primitives;

/// <summary>
/// Lightweight argument guards for enforcing invariants at the edges (factories, constructors).
/// These protect against programmer error (a bug), so they throw — distinct from expected
/// business failures, which flow through <see cref="Results.Result"/>.
/// </summary>
public static class Guard
{
    public static T NotNull<T>(T? value, [CallerArgumentExpression(nameof(value))] string? name = null)
        where T : class =>
        value ?? throw new ArgumentNullException(name);

    public static string NotNullOrWhiteSpace(string? value, [CallerArgumentExpression(nameof(value))] string? name = null)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            throw new ArgumentException("Value cannot be null or whitespace.", name);
        }

        return value;
    }

    public static Guid NotEmpty(Guid value, [CallerArgumentExpression(nameof(value))] string? name = null)
    {
        if (value == Guid.Empty)
        {
            throw new ArgumentException("Value cannot be an empty GUID.", name);
        }

        return value;
    }

    public static string MaxLength(string value, int maxLength, [CallerArgumentExpression(nameof(value))] string? name = null)
    {
        if (value.Length > maxLength)
        {
            throw new ArgumentException($"Value exceeds the maximum length of {maxLength}.", name);
        }

        return value;
    }
}
