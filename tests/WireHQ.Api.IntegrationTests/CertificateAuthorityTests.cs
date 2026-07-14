using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using WireHQ.Application.Abstractions;
using WireHQ.Application.Abstractions.Persistence;
using WireHQ.Modules.Orchestration.Certificates;
using WireHQ.Modules.Orchestration.Domain;
using Xunit;

namespace WireHQ.Api.IntegrationTests;

/// <summary>
/// Proves the per-org private CA (ADR-028, Slice A): a CA is minted lazily on first issuance and reused
/// thereafter; an agent's CSR yields a short-lived <c>clientAuth</c> leaf that chains to the org CA with a
/// WireHQ-assigned subject; and malformed or non-EC CSRs are rejected. Runs against the real Postgres as
/// the RLS <c>wirehq_app</c> role, so the CA tables are exercised under the live tenancy policy.
/// </summary>
[Collection(ApiCollection.Name)]
public sealed class CertificateAuthorityTests(WireHqApiFactory factory)
{
    private const string ClientAuthOid = "1.3.6.1.5.5.7.3.2";
    private readonly WireHqApiFactory _factory = factory;

    [Fact]
    public async Task Issuing_a_client_cert_lazily_creates_the_org_ca_and_returns_a_valid_clientauth_leaf()
    {
        var client = _factory.CreateClient();
        var orgId = await RegisterAsync(client, "CA Issue Org");
        var agentId = Guid.CreateVersion7();

        using var scope = _factory.Services.CreateScope();
        scope.ServiceProvider.GetRequiredService<ISettableTenantContext>().SetTenant(orgId);
        var ca = scope.ServiceProvider.GetRequiredService<ICertificateAuthority>();
        var db = scope.ServiceProvider.GetRequiredService<IApplicationDbContext>();

        var result = await ca.IssueClientCertificateAsync(orgId, agentId, BuildEcCsr(), CancellationToken.None);
        await db.SaveChangesAsync(CancellationToken.None);

        result.IsSuccess.Should().BeTrue(because: result.IsFailure ? result.Error.Description : null);
        var issued = result.Value;

        using var leaf = X509Certificate2.CreateFromPem(issued.CertificatePem);
        using var caCert = X509Certificate2.CreateFromPem(issued.CaCertificatePem);

        // Identity is assigned by WireHQ from the validated org + minted agent id — never the CSR's subject.
        leaf.Subject.Should().Contain(agentId.ToString()).And.Contain(orgId.ToString());

        // It is a client-auth leaf, short-lived (~45 days), and not a CA.
        leaf.Extensions.OfType<X509EnhancedKeyUsageExtension>().Single()
            .EnhancedKeyUsages.Cast<Oid>().Select(o => o.Value).Should().Contain(ClientAuthOid);
        leaf.Extensions.OfType<X509BasicConstraintsExtension>().Single().CertificateAuthority.Should().BeFalse();
        (leaf.NotAfter.ToUniversalTime() - DateTime.UtcNow).TotalDays.Should().BeInRange(44, 46);

        // The fingerprint the gateway will authenticate by is the SHA-256 of this exact cert.
        issued.Sha256Fingerprint.Should().Be(leaf.GetCertHashString(HashAlgorithmName.SHA256));

        // The leaf chains to (only) the org CA — proven with a custom trust anchor, no machine store.
        ChainsTo(leaf, caCert).Should().BeTrue();

        // The CA was persisted, sealed (the private key is ciphertext, not PEM).
        var caRow = await db.Set<OrgCertificateAuthority>().IgnoreQueryFilters()
            .SingleAsync(c => c.OrganizationId == orgId);
        caRow.PrivateKeyCiphertext.Should().NotBeNullOrEmpty().And.NotContain("PRIVATE KEY");
    }

    [Fact]
    public async Task Issuing_twice_reuses_the_same_org_ca()
    {
        var client = _factory.CreateClient();
        var orgId = await RegisterAsync(client, "CA Reuse Org");

        using var scope = _factory.Services.CreateScope();
        scope.ServiceProvider.GetRequiredService<ISettableTenantContext>().SetTenant(orgId);
        var ca = scope.ServiceProvider.GetRequiredService<ICertificateAuthority>();
        var db = scope.ServiceProvider.GetRequiredService<IApplicationDbContext>();

        var first = await ca.IssueClientCertificateAsync(orgId, Guid.CreateVersion7(), BuildEcCsr(), CancellationToken.None);
        await db.SaveChangesAsync(CancellationToken.None);
        var second = await ca.IssueClientCertificateAsync(orgId, Guid.CreateVersion7(), BuildEcCsr(), CancellationToken.None);
        await db.SaveChangesAsync(CancellationToken.None);

        first.IsSuccess.Should().BeTrue();
        second.IsSuccess.Should().BeTrue();
        second.Value.CaCertificatePem.Should().Be(first.Value.CaCertificatePem);
        second.Value.Sha256Fingerprint.Should().NotBe(first.Value.Sha256Fingerprint);

        (await db.Set<OrgCertificateAuthority>().IgnoreQueryFilters().CountAsync(c => c.OrganizationId == orgId))
            .Should().Be(1, because: "the CA is created once and reused for every subsequent issuance");
    }

    [Fact]
    public async Task A_non_ec_csr_is_rejected_as_weak()
    {
        var client = _factory.CreateClient();
        var orgId = await RegisterAsync(client, "CA Weak Key Org");

        using var scope = _factory.Services.CreateScope();
        scope.ServiceProvider.GetRequiredService<ISettableTenantContext>().SetTenant(orgId);
        var ca = scope.ServiceProvider.GetRequiredService<ICertificateAuthority>();

        var result = await ca.IssueClientCertificateAsync(orgId, Guid.CreateVersion7(), BuildRsaCsr(), CancellationToken.None);

        result.IsFailure.Should().BeTrue();
        result.Error.Code.Should().Be("orch.agent.weak_key");
    }

    [Fact]
    public async Task A_malformed_csr_is_rejected()
    {
        var client = _factory.CreateClient();
        var orgId = await RegisterAsync(client, "CA Bad Csr Org");

        using var scope = _factory.Services.CreateScope();
        scope.ServiceProvider.GetRequiredService<ISettableTenantContext>().SetTenant(orgId);
        var ca = scope.ServiceProvider.GetRequiredService<ICertificateAuthority>();

        var result = await ca.IssueClientCertificateAsync(orgId, Guid.CreateVersion7(), "-----BEGIN CERTIFICATE REQUEST-----\nnot-a-csr\n-----END CERTIFICATE REQUEST-----", CancellationToken.None);

        result.IsFailure.Should().BeTrue();
        result.Error.Code.Should().Be("orch.agent.invalid_csr");
    }

    private static bool ChainsTo(X509Certificate2 leaf, X509Certificate2 root)
    {
        using var chain = new X509Chain();
        chain.ChainPolicy.TrustMode = X509ChainTrustMode.CustomRootTrust;
        chain.ChainPolicy.CustomTrustStore.Add(root);
        chain.ChainPolicy.RevocationMode = X509RevocationMode.NoCheck;
        chain.ChainPolicy.VerificationTime = DateTime.UtcNow;
        return chain.Build(leaf);
    }

    /// <summary>An EC P-256 CSR — the subject is deliberately bogus to prove the CA ignores it.</summary>
    private static string BuildEcCsr()
    {
        using var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        return new CertificateRequest("CN=attacker-chosen,O=not-my-org", key, HashAlgorithmName.SHA256)
            .CreateSigningRequestPem();
    }

    private static string BuildRsaCsr()
    {
        using var rsa = RSA.Create(2048);
        return new CertificateRequest("CN=rsa-agent", rsa, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1)
            .CreateSigningRequestPem();
    }

    private static async Task<Guid> RegisterAsync(HttpClient client, string name)
    {
        var email = $"{name.Replace(' ', '.').ToLower()}+{Guid.NewGuid():N}@wirehq.test";
        var response = await client.PostAsJsonAsync("/api/v1/auth/register",
            new { email, password = "Sup3rSecret!!", firstName = name, lastName = "Test", acceptTerms = true });
        var body = (await response.Content.ReadFromJsonAsync<RegisterResult>())!;
        return body.OrganizationId;
    }

    private sealed record RegisterResult(Guid UserId, Guid OrganizationId, string OrganizationSlug);
}
