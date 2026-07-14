using System.Text.Json;
using WireHQ.Application.Abstractions;
using WireHQ.Application.Abstractions.Licensing;
using WireHQ.Licensing.Internal;
using WireHQ.Licensing.Paseto;

namespace WireHQ.Licensing;

/// <summary>
/// Verifies licence tokens against the pinned public keys and enforces any temporal claims. The order is
/// deliberate: read the footer's <c>kid</c>, select the public key, check the signature, then — only on a
/// valid signature — enforce <c>exp</c>/<c>nbf</c> and deserialize. Needs public keys only, so this is the
/// half a self-hosted install runs offline (docs/19-marketplace-licensing.md, ADR-036).
/// </summary>
public sealed class LicenceTokenVerifier(LicensingKeyRing keyRing, IDateTimeProvider clock) : ILicenceTokenVerifier
{
    public LicenceTokenVerification<TClaims> Verify<TClaims>(string token)
        where TClaims : notnull
    {
        if (string.IsNullOrEmpty(token) || !PasetoV4Public.TryReadFooter(token, out var footerBytes))
        {
            return LicenceTokenVerification<TClaims>.Invalid(LicenceTokenFailure.Malformed);
        }

        var keyId = TryReadKeyId(footerBytes);
        if (keyId is null)
        {
            return LicenceTokenVerification<TClaims>.Invalid(LicenceTokenFailure.Malformed);
        }

        if (!keyRing.TryGetPublicKey(keyId, out var publicKey))
        {
            return LicenceTokenVerification<TClaims>.Invalid(LicenceTokenFailure.UnknownKeyId, keyId);
        }

        if (!PasetoV4Public.TryVerify(token, publicKey, out var payload))
        {
            return LicenceTokenVerification<TClaims>.Invalid(LicenceTokenFailure.BadSignature, keyId);
        }

        // The signature is valid — now enforce registered temporal claims and read the claims shape.
        JsonDocument document;
        try
        {
            document = JsonDocument.Parse(payload);
        }
        catch (JsonException)
        {
            return LicenceTokenVerification<TClaims>.Invalid(LicenceTokenFailure.UnreadableClaims, keyId);
        }

        using (document)
        {
            var now = clock.UtcNow;
            if (TryReadTimestamp(document.RootElement, "nbf", out var notBefore) && now < notBefore)
            {
                return LicenceTokenVerification<TClaims>.Invalid(LicenceTokenFailure.NotYetValid, keyId);
            }

            if (TryReadTimestamp(document.RootElement, "exp", out var expiry) && now >= expiry)
            {
                return LicenceTokenVerification<TClaims>.Invalid(LicenceTokenFailure.Expired, keyId);
            }
        }

        TClaims? claims;
        try
        {
            claims = JsonSerializer.Deserialize<TClaims>(payload, LicenceTokenJson.Options);
        }
        catch (JsonException)
        {
            return LicenceTokenVerification<TClaims>.Invalid(LicenceTokenFailure.UnreadableClaims, keyId);
        }

        return claims is null
            ? LicenceTokenVerification<TClaims>.Invalid(LicenceTokenFailure.UnreadableClaims, keyId)
            : LicenceTokenVerification<TClaims>.Valid(claims, keyId);
    }

    private static string? TryReadKeyId(byte[] footerBytes)
    {
        if (footerBytes.Length == 0)
        {
            return null;
        }

        try
        {
            var footer = JsonSerializer.Deserialize<LicenceTokenFooter>(footerBytes, LicenceTokenJson.Options);
            return string.IsNullOrEmpty(footer?.Kid) ? null : footer.Kid;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static bool TryReadTimestamp(JsonElement root, string claim, out DateTimeOffset value)
    {
        value = default;
        return root.ValueKind == JsonValueKind.Object
            && root.TryGetProperty(claim, out var element)
            && element.ValueKind == JsonValueKind.String
            && element.TryGetDateTimeOffset(out value);
    }
}
