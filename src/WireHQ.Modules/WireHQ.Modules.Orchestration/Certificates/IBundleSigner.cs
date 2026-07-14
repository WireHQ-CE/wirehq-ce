namespace WireHQ.Modules.Orchestration.Certificates;

/// <summary>
/// Signs deployment bundles with a per-org signing key so an agent can verify integrity + provenance
/// before applying — even if the transport is breached. The job-delivery path that uses this lands in
/// Slice B; the seam is declared here so the certificate/trust surface lives in one place. (ADR-028)
/// </summary>
public interface IBundleSigner
{
    /// <summary>Signs the bundle bytes for <paramref name="organizationId"/>, returning a detached signature (base64).</summary>
    Task<string> SignAsync(Guid organizationId, ReadOnlyMemory<byte> bundle, CancellationToken cancellationToken);
}
