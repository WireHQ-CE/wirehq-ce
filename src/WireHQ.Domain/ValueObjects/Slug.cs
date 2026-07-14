using System.Text.RegularExpressions;
using WireHQ.Domain.Common;
using WireHQ.Shared.Results;

namespace WireHQ.Domain.ValueObjects;

/// <summary>
/// A URL-safe identifier (e.g. <c>acme-corp</c>) used for organizations and teams. Lower-case,
/// alphanumeric and hyphens, no leading/trailing/double hyphens. Human-friendly and safe in
/// subdomains and URLs.
/// </summary>
public sealed partial class Slug : ValueObject
{
    public const int MinLength = 2;
    public const int MaxLength = 48;

    private Slug(string value) => Value = value;

    public string Value { get; }

    public static Result<Slug> Create(string? input)
    {
        if (string.IsNullOrWhiteSpace(input))
        {
            return SlugErrors.Empty;
        }

        var normalized = input.Trim().ToLowerInvariant();

        if (normalized.Length is < MinLength or > MaxLength)
        {
            return SlugErrors.InvalidLength;
        }

        if (!SlugRegex().IsMatch(normalized))
        {
            return SlugErrors.InvalidFormat;
        }

        return new Slug(normalized);
    }

    /// <summary>Best-effort conversion of an arbitrary name into a candidate slug.</summary>
    public static Result<Slug> FromName(string name)
    {
        var candidate = NonSlugChars().Replace(name.Trim().ToLowerInvariant(), "-");
        candidate = MultiHyphen().Replace(candidate, "-").Trim('-');
        return Create(candidate);
    }

    protected override IEnumerable<object?> GetEqualityComponents()
    {
        yield return Value;
    }

    public override string ToString() => Value;

    [GeneratedRegex("^[a-z0-9]+(?:-[a-z0-9]+)*$", RegexOptions.CultureInvariant)]
    private static partial Regex SlugRegex();

    [GeneratedRegex("[^a-z0-9]+", RegexOptions.CultureInvariant)]
    private static partial Regex NonSlugChars();

    [GeneratedRegex("-{2,}", RegexOptions.CultureInvariant)]
    private static partial Regex MultiHyphen();
}

public static class SlugErrors
{
    public static readonly Error Empty = Error.Validation("slug.empty", "Slug is required.");
    public static readonly Error InvalidLength = Error.Validation("slug.invalid_length", $"Slug must be between {Slug.MinLength} and {Slug.MaxLength} characters.");
    public static readonly Error InvalidFormat = Error.Validation("slug.invalid_format", "Slug may contain only lowercase letters, numbers and single hyphens.");
}
