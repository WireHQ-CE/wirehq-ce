using System.Text.RegularExpressions;
using WireHQ.Domain.Common;
using WireHQ.Shared.Results;

namespace WireHQ.Domain.ValueObjects;

/// <summary>
/// A validated, normalized <b>E.164</b> phone number (docs/36-notifications.md §8, SMS Wave 4). Construction goes
/// through <see cref="Create"/>, which returns a <see cref="Result{T}"/> — an invalid number is an expected failure,
/// not an exception (mirrors <see cref="Email"/>). Stored in canonical E.164 form (a leading <c>+</c>, country code,
/// then national digits, no separators) so it is stable across providers and comparable for the "verified number is
/// unique across users" rule (docs/36 §2). The value object carries the format contract; ownership + consent are
/// tracked separately (a verified number ≠ consent to receive — docs/36 §4).
/// </summary>
public sealed partial class PhoneNumber : ValueObject
{
    /// <summary>Max stored length: <c>+</c> plus the E.164 maximum of 15 digits.</summary>
    public const int MaxLength = 16;

    private PhoneNumber(string value) => Value = value;

    /// <summary>The canonical E.164 string, e.g. <c>+15005550006</c>.</summary>
    public string Value { get; }

    public static Result<PhoneNumber> Create(string? input)
    {
        if (string.IsNullOrWhiteSpace(input))
        {
            return PhoneNumberErrors.Empty;
        }

        // Accept human-entered spacing/separators and normalise them away — the canonical form has none. A leading
        // "00" international prefix is normalised to "+" (a very common entry style).
        var normalized = SeparatorRegex().Replace(input.Trim(), string.Empty);
        if (normalized.StartsWith("00", StringComparison.Ordinal))
        {
            normalized = "+" + normalized[2..];
        }

        if (normalized.Length > MaxLength)
        {
            return PhoneNumberErrors.Invalid;
        }

        // E.164: a leading '+', a non-zero country-code digit, then up to 14 further digits (2..15 digits total).
        if (!E164Regex().IsMatch(normalized))
        {
            return PhoneNumberErrors.Invalid;
        }

        return new PhoneNumber(normalized);
    }

    protected override IEnumerable<object?> GetEqualityComponents()
    {
        yield return Value;
    }

    public override string ToString() => Value;

    [GeneratedRegex(@"[\s\-().]", RegexOptions.CultureInvariant)]
    private static partial Regex SeparatorRegex();

    [GeneratedRegex(@"^\+[1-9]\d{1,14}$", RegexOptions.CultureInvariant)]
    private static partial Regex E164Regex();
}

public static class PhoneNumberErrors
{
    public static readonly Error Empty = Error.Validation("phone.empty", "A phone number is required.");
    public static readonly Error Invalid = Error.Validation("phone.invalid", "Enter a valid phone number in international format, e.g. +14155550123.");
}
