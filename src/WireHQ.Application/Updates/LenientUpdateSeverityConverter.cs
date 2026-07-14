using System.Text.Json;
using System.Text.Json.Serialization;

namespace WireHQ.Application.Updates;

/// <summary>
/// Reads <see cref="UpdateSeverity"/> from its string name (case-insensitive) but falls back to
/// <see cref="UpdateSeverity.None"/> for any UNKNOWN value instead of throwing (docs/30 U-13a). This is a
/// forward-compatibility guard that CANNOT be retrofitted to already-deployed CE installs: if a future WireHQ
/// release publishes a manifest carrying a severity tier an older install doesn't recognise, a strict converter
/// would reject the ENTIRE signed manifest — blinding that install to a security release (a false all-clear).
/// Loudness is driven by <c>security</c>/<c>unsupported</c>, not severity, so an unknown tier degrades to a
/// still-correct advisory. Writes the enum's string name, so the API keeps emitting "High" etc. for the frontend.
/// </summary>
public sealed class LenientUpdateSeverityConverter : JsonConverter<UpdateSeverity>
{
    public override UpdateSeverity Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
        => reader.TokenType == JsonTokenType.String
            && Enum.TryParse<UpdateSeverity>(reader.GetString(), ignoreCase: true, out var severity)
            && Enum.IsDefined(severity)
                ? severity
                : UpdateSeverity.None;

    public override void Write(Utf8JsonWriter writer, UpdateSeverity value, JsonSerializerOptions options)
        => writer.WriteStringValue(value.ToString());
}
