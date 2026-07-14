namespace WireHQ.Application.Abstractions.Licensing;

/// <summary>
/// Signs marketplace licence artifacts (licence keys, activation tokens) into signed, tamper-evident
/// tokens using the active signing key. SaaS-only: it holds the Ed25519 private key, so it is wired
/// only in the hosted licensing service — never in a self-hosted Community Edition, which verifies but
/// never issues (docs/19-marketplace-licensing.md, ADR-036).
///
/// The claims object is serialized to the token's JSON payload verbatim; callers own its shape and set
/// any temporal claims (<c>iat</c>/<c>exp</c>/<c>nbf</c>) themselves — see
/// <see cref="LicenceKeyClaims"/> and <see cref="ActivationTokenClaims"/>.
/// </summary>
public interface ILicenceTokenSigner
{
    /// <summary>The active key id stamped into new tokens (for later verification + rotation).</summary>
    string ActiveKeyId { get; }

    /// <summary>Signs <paramref name="claims"/> into a token bound to the active key.</summary>
    string Sign<TClaims>(TClaims claims)
        where TClaims : notnull;
}
