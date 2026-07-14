using System.Text.Json;
using WireHQ.Application.Abstractions.Licensing;
using WireHQ.Licensing.Internal;
using WireHQ.Licensing.Paseto;

namespace WireHQ.Licensing;

/// <summary>
/// Signs licence artifacts with the key ring's active key, stamping its <c>kid</c> into the token footer
/// so a verifier (this service during call-home, or a self-hosted install offline) can select the right
/// public key later. SaaS-only — it needs the private key (docs/19-marketplace-licensing.md, ADR-036).
/// </summary>
public sealed class LicenceTokenSigner(LicensingKeyRing keyRing) : ILicenceTokenSigner
{
    public string ActiveKeyId => keyRing.ActiveKeyId;

    public string Sign<TClaims>(TClaims claims)
        where TClaims : notnull
    {
        ArgumentNullException.ThrowIfNull(claims);

        var payload = JsonSerializer.SerializeToUtf8Bytes(claims, LicenceTokenJson.Options);
        var footer = JsonSerializer.SerializeToUtf8Bytes(new LicenceTokenFooter(keyRing.ActiveKeyId), LicenceTokenJson.Options);

        return PasetoV4Public.Sign(payload, footer, keyRing.ActiveSigningKey);
    }
}
