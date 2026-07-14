using System.Text.Json;
using Microsoft.EntityFrameworkCore.ChangeTracking;
using Microsoft.EntityFrameworkCore.Storage.ValueConversion;

namespace WireHQ.Modules.WireGuard.Persistence;

/// <summary>jsonb value converters + comparers for the module's string collections/maps.</summary>
internal static class WireGuardJson
{
    private static readonly JsonSerializerOptions Options = new(JsonSerializerDefaults.Web);

    public static readonly ValueConverter<IReadOnlyCollection<string>, string> StringCollection =
        new(v => JsonSerializer.Serialize(v, Options),
            v => JsonSerializer.Deserialize<IReadOnlyCollection<string>>(v, Options) ?? new List<string>());

    public static readonly ValueComparer<IReadOnlyCollection<string>> StringCollectionComparer =
        new((a, b) => a!.SequenceEqual(b!),
            v => v.Count,
            v => v.ToList());

    public static readonly ValueConverter<IReadOnlyDictionary<string, string>, string> StringDictionary =
        new(v => JsonSerializer.Serialize(v, Options),
            v => JsonSerializer.Deserialize<IReadOnlyDictionary<string, string>>(v, Options) ?? new Dictionary<string, string>());

    public static readonly ValueComparer<IReadOnlyDictionary<string, string>> StringDictionaryComparer =
        new((a, b) => a!.Count == b!.Count && !a.Except(b).Any(),
            v => v.Count,
            v => v.ToDictionary(kv => kv.Key, kv => kv.Value));
}
