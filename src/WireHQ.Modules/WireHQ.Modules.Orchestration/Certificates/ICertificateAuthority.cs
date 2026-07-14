using WireHQ.Shared.Results;

namespace WireHQ.Modules.Orchestration.Certificates;

/// <summary>A freshly-issued agent client certificate plus the chain material the agent needs to keep it.</summary>
/// <param name="CertificatePem">The issued client certificate (PEM, public).</param>
/// <param name="CaCertificatePem">The issuing org CA certificate (PEM, public) — the chain anchor.</param>
/// <param name="Sha256Fingerprint">Uppercase hex SHA-256 of the cert (DER) — the gateway's auth key.</param>
/// <param name="NotAfterUtc">Expiry; the agent rotates before this via <c>/agent/v1/cert/rotate</c>.</param>
public sealed record IssuedClientCertificate(
    string CertificatePem,
    string CaCertificatePem,
    string Sha256Fingerprint,
    DateTimeOffset NotAfterUtc);

/// <summary>
/// The per-org private certificate authority: WireHQ lazily mints one self-signed CA per organization and
/// uses it to issue short-lived client certificates for that org's agents. The CA <b>private key</b> is
/// sealed at rest via <c>ISecretProtector</c> and never leaves the server in clear; issuance takes only the
/// <b>public key</b> from the agent's CSR and assigns the subject itself (the client never chooses its own
/// identity). Self-contained — built on <c>System.Security.Cryptography.X509Certificates</c>, no external CA.
/// Revocation is not a CRL: the gateway rejects a disabled/revoked agent by fingerprint. (ADR-028, docs/12 §5)
/// </summary>
public interface ICertificateAuthority
{
    /// <summary>
    /// Lazily creates + persists the organization's CA on first call (idempotent thereafter) and returns its
    /// public certificate (PEM). The caller must already have established the tenant scope for <paramref name="organizationId"/>.
    /// </summary>
    Task<Result<string>> EnsureCaAsync(Guid organizationId, CancellationToken cancellationToken);

    /// <summary>
    /// Validates an agent's CSR (proof-of-possession + key strength) and issues a short-lived client leaf
    /// signed by the org CA, with <c>clientAuth</c> EKU and a WireHQ-assigned subject carrying the org +
    /// agent id. Only the CSR's public key is honoured. Ensures the CA exists first.
    /// </summary>
    Task<Result<IssuedClientCertificate>> IssueClientCertificateAsync(
        Guid organizationId, Guid agentId, string csrPem, CancellationToken cancellationToken);
}
