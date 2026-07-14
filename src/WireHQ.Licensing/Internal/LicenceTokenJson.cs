using System.Text.Json;
using System.Text.Json.Serialization;

namespace WireHQ.Licensing.Internal;

/// <summary>
/// Shared JSON handling for licence tokens. Signature validity does <b>not</b> depend on canonical
/// JSON: the signer serializes the claims once and signs those exact bytes, and the verifier checks the
/// signature over the bytes as transmitted — deserialization to a claims type happens only after the
/// signature is confirmed. These options just keep serialization stable and compact.
/// </summary>
internal static class LicenceTokenJson
{
    internal static readonly JsonSerializerOptions Options = new(JsonSerializerDefaults.Web)
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };
}

/// <summary>The token footer: carries the signing key id (<c>kid</c>) so the verifier can select the
/// right public key. The footer is authenticated by the PASETO PAE but not encrypted (it is not secret).</summary>
internal sealed record LicenceTokenFooter(
    [property: JsonPropertyName("kid")] string Kid);
