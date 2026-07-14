using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using Microsoft.EntityFrameworkCore;
using WireHQ.Application.Abstractions;
using WireHQ.Application.Abstractions.Persistence;
using WireHQ.Application.Abstractions.Security;
using WireHQ.Modules.Orchestration.Domain;
using WireHQ.Shared.Results;

namespace WireHQ.Modules.Orchestration.Certificates;

/// <summary>
/// The per-org private CA. Mints a self-signed ECDSA P-384 CA per organization on first use, sealing its
/// private key via <see cref="ISecretProtector"/>; issues short-lived (45-day) ECDSA P-256 client leaves
/// signed by that CA from an agent's CSR. Only the CSR's <b>public key</b> is honoured — the subject,
/// validity, serial, and extensions are all assigned by WireHQ, so a client can never request a different
/// identity or capabilities than we grant. Persistence flows through <see cref="IApplicationDbContext"/>;
/// the surrounding unit-of-work behaviour commits (handlers Add, the pipeline saves). (ADR-028, docs/12 §5)
/// </summary>
public sealed class CaService(
    IApplicationDbContext dbContext,
    ISecretProtector secretProtector,
    IDateTimeProvider clock)
    : ICertificateAuthority, IBundleSigner
{
    private const int CaValidityYears = 10;
    private const int LeafValidityDays = 45;
    private static readonly Oid ClientAuthEku = new("1.3.6.1.5.5.7.3.2");

    /// <summary>
    /// Signs a deployment bundle with the org's CA key (ECDSA-P384/SHA-384, detached base64 signature). The
    /// agent already holds the CA certificate from enrolment, so it verifies the signature with the CA public
    /// key — no extra key distribution. The CA is the org's root of trust; a dedicated signing key can split
    /// out later without changing this seam. (ADR-028)
    /// </summary>
    public async Task<string> SignAsync(Guid organizationId, ReadOnlyMemory<byte> bundle, CancellationToken cancellationToken)
    {
        var ca = await LoadOrCreateCaAsync(organizationId, cancellationToken);
        if (ca.IsFailure)
        {
            throw new InvalidOperationException(ca.Error.Description);
        }

        using var signingCertificate = ca.Value.ToSigningCertificate();
        using var key = signingCertificate.GetECDsaPrivateKey()
            ?? throw new InvalidOperationException("The organization CA has no usable signing key.");
        return Convert.ToBase64String(key.SignData(bundle.Span, HashAlgorithmName.SHA384));
    }

    public async Task<Result<string>> EnsureCaAsync(Guid organizationId, CancellationToken cancellationToken)
    {
        var material = await LoadOrCreateCaAsync(organizationId, cancellationToken);
        return material.IsFailure ? material.Error : material.Value.CertificatePem;
    }

    public async Task<Result<IssuedClientCertificate>> IssueClientCertificateAsync(
        Guid organizationId, Guid agentId, string csrPem, CancellationToken cancellationToken)
    {
        var caResult = await LoadOrCreateCaAsync(organizationId, cancellationToken);
        if (caResult.IsFailure)
        {
            return caResult.Error;
        }

        // Read + verify the CSR's signature (proof the requester holds the matching private key). Default
        // load options validate the signature and ignore any extensions/attributes the CSR carries.
        CertificateRequest csr;
        try
        {
            csr = CertificateRequest.LoadSigningRequestPem(csrPem, HashAlgorithmName.SHA256);
        }
        catch (Exception ex) when (ex is CryptographicException or ArgumentException)
        {
            return OrchestrationErrors.Agent.InvalidCsr;
        }

        // Honour only EC P-256/P-384 keys — matches what the agent generates and keeps the issuing path tight.
        using (var presentedKey = csr.PublicKey.GetECDsaPublicKey())
        {
            if (presentedKey is null || presentedKey.KeySize is not (256 or 384))
            {
                return OrchestrationErrors.Agent.WeakKey;
            }
        }

        var now = clock.UtcNow;
        using var caCert = caResult.Value.ToSigningCertificate();

        // Build a fresh request from the CSR's public key with our own subject + extensions — never the CSR's.
        var subject = new X500DistinguishedName($"CN={agentId},O={organizationId}");
        var leafRequest = new CertificateRequest(subject, csr.PublicKey, HashAlgorithmName.SHA256);
        leafRequest.CertificateExtensions.Add(new X509BasicConstraintsExtension(certificateAuthority: false, hasPathLengthConstraint: false, pathLengthConstraint: 0, critical: true));
        leafRequest.CertificateExtensions.Add(new X509KeyUsageExtension(X509KeyUsageFlags.DigitalSignature, critical: true));
        leafRequest.CertificateExtensions.Add(new X509EnhancedKeyUsageExtension([ClientAuthEku], critical: false));
        leafRequest.CertificateExtensions.Add(new X509SubjectKeyIdentifierExtension(leafRequest.PublicKey, critical: false));
        leafRequest.CertificateExtensions.Add(X509AuthorityKeyIdentifierExtension.CreateFromCertificate(caCert, includeKeyIdentifier: true, includeIssuerAndSerial: false));

        var notBefore = now.AddMinutes(-5); // small backdate to tolerate agent/server clock skew
        var notAfter = now.AddDays(LeafValidityDays);
        using var leaf = leafRequest.Create(caCert, notBefore, notAfter, NewSerial());

        return new IssuedClientCertificate(
            leaf.ExportCertificatePem(),
            caResult.Value.CertificatePem,
            leaf.GetCertHashString(HashAlgorithmName.SHA256),
            notAfter);
    }

    /// <summary>Loads the org's CA material, creating + queuing it for persistence on first use.</summary>
    private async Task<Result<CaMaterial>> LoadOrCreateCaAsync(Guid organizationId, CancellationToken cancellationToken)
    {
        var existing = await dbContext.Set<OrgCertificateAuthority>()
            .FirstOrDefaultAsync(c => c.OrganizationId == organizationId, cancellationToken);
        if (existing is not null)
        {
            return new CaMaterial(existing.CertificatePem, secretProtector.Unprotect(existing.PrivateKeyCiphertext));
        }

        try
        {
            var material = GenerateCa(organizationId);
            var ca = OrgCertificateAuthority.Create(
                organizationId, material.CertificatePem, secretProtector.Protect(material.PrivateKeyBase64));
            dbContext.Set<OrgCertificateAuthority>().Add(ca); // the unit-of-work behaviour commits
            return material;
        }
        catch (CryptographicException)
        {
            return OrchestrationErrors.Agent.CaUnavailable;
        }
    }

    /// <summary>Generates a fresh self-signed ECDSA P-384 CA for one organization.</summary>
    private CaMaterial GenerateCa(Guid organizationId)
    {
        using var caKey = ECDsa.Create(ECCurve.NamedCurves.nistP384);
        var request = new CertificateRequest(
            new X500DistinguishedName($"CN=WireHQ Agent CA,O={organizationId}"), caKey, HashAlgorithmName.SHA384);
        request.CertificateExtensions.Add(new X509BasicConstraintsExtension(certificateAuthority: true, hasPathLengthConstraint: true, pathLengthConstraint: 0, critical: true));
        request.CertificateExtensions.Add(new X509KeyUsageExtension(X509KeyUsageFlags.KeyCertSign | X509KeyUsageFlags.CrlSign, critical: true));
        request.CertificateExtensions.Add(new X509SubjectKeyIdentifierExtension(request.PublicKey, critical: false));

        var now = clock.UtcNow;
        using var caCert = request.CreateSelfSigned(now.AddMinutes(-5), now.AddYears(CaValidityYears));
        return new CaMaterial(caCert.ExportCertificatePem(), Convert.ToBase64String(caKey.ExportPkcs8PrivateKey()));
    }

    private static byte[] NewSerial()
    {
        var serial = RandomNumberGenerator.GetBytes(16);
        serial[0] &= 0x7F; // keep it a positive integer
        return serial;
    }

    /// <summary>A loaded CA: its public cert (PEM) + its private key (PKCS#8, base64). In-memory only.</summary>
    private sealed record CaMaterial(string CertificatePem, string PrivateKeyBase64)
    {
        /// <summary>Rehydrates a signing certificate (cert + private key) for issuing leaves. Caller disposes.</summary>
        public X509Certificate2 ToSigningCertificate()
        {
            using var key = ECDsa.Create();
            key.ImportPkcs8PrivateKey(Convert.FromBase64String(PrivateKeyBase64), out _);
            using var publicOnly = X509Certificate2.CreateFromPem(CertificatePem);
            return publicOnly.CopyWithPrivateKey(key);
        }
    }
}
