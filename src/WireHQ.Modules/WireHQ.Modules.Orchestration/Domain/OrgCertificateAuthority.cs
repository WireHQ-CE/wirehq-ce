using WireHQ.Domain.Common;

namespace WireHQ.Modules.Orchestration.Domain;

/// <summary>
/// A tenant's private certificate authority — the root WireHQ uses to issue + sign that org's agent client
/// certificates (and to sign deploy bundles). Created lazily on the org's first enrolment. The CA <b>private
/// key</b> is held only as ciphertext (sealed via <c>ISecretProtector</c>, AES-256-GCM) and never returned;
/// the cert (public) is stored in PEM so the gateway can build the issuing chain. One per org. (ADR-019/028)
/// </summary>
public sealed class OrgCertificateAuthority : AggregateRoot, ITenantOwned, IAuditable
{
    private OrgCertificateAuthority()
    {
    }

    private OrgCertificateAuthority(Guid id, Guid organizationId, string certificatePem, string privateKeyCiphertext)
        : base(id)
    {
        OrganizationId = organizationId;
        CertificatePem = certificatePem;
        PrivateKeyCiphertext = privateKeyCiphertext;
    }

    public Guid OrganizationId { get; private set; }

    /// <summary>The CA certificate (PEM, public).</summary>
    public string CertificatePem { get; private set; } = null!;

    /// <summary>The CA private key, encrypted at rest. Never leaves the server in clear.</summary>
    public string PrivateKeyCiphertext { get; private set; } = null!;

    public DateTimeOffset CreatedAtUtc { get; private set; }
    public Guid? CreatedBy { get; private set; }
    public DateTimeOffset? UpdatedAtUtc { get; private set; }
    public Guid? UpdatedBy { get; private set; }

    public static OrgCertificateAuthority Create(Guid organizationId, string certificatePem, string privateKeyCiphertext) =>
        new(Guid.CreateVersion7(), organizationId, certificatePem, privateKeyCiphertext);
}
