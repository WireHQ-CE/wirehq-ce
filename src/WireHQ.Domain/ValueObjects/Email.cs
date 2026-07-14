using System.Text.RegularExpressions;
using WireHQ.Domain.Common;
using WireHQ.Shared.Results;

namespace WireHQ.Domain.ValueObjects;

/// <summary>
/// A validated, normalized email address. Construction goes through <see cref="Create"/>,
/// which returns a <see cref="Result{T}"/> — an invalid email is an expected failure, not an
/// exception. Stored normalized (trimmed, lower-cased) so uniqueness is case-insensitive.
/// </summary>
public sealed partial class Email : ValueObject
{
    public const int MaxLength = 320; // RFC 5321 local(64) + @ + domain(255)

    private Email(string value) => Value = value;

    public string Value { get; }

    public static Result<Email> Create(string? input)
    {
        if (string.IsNullOrWhiteSpace(input))
        {
            return EmailErrors.Empty;
        }

        var normalized = input.Trim().ToLowerInvariant();

        if (normalized.Length > MaxLength)
        {
            return EmailErrors.TooLong;
        }

        if (!EmailRegex().IsMatch(normalized))
        {
            return EmailErrors.Invalid;
        }

        return new Email(normalized);
    }

    protected override IEnumerable<object?> GetEqualityComponents()
    {
        yield return Value;
    }

    public override string ToString() => Value;

    [GeneratedRegex(@"^[^@\s]+@[^@\s]+\.[^@\s]+$", RegexOptions.CultureInvariant)]
    private static partial Regex EmailRegex();
}

public static class EmailErrors
{
    public static readonly Error Empty = Error.Validation("email.empty", "Email is required.");
    public static readonly Error TooLong = Error.Validation("email.too_long", "Email is too long.");
    public static readonly Error Invalid = Error.Validation("email.invalid", "Email is not a valid address.");
}
