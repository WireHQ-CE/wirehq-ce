using System.Net.Http.Json;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using WireHQ.Application.Abstractions;
using WireHQ.Application.Abstractions.Persistence;
using Xunit;

namespace WireHQ.Api.IntegrationTests;

/// <summary>
/// Proves Layer-2 tenant isolation — Postgres Row-Level Security (ADR-027) — is enforced at runtime. The
/// API connects as the non-privileged <c>wirehq_app</c> role, so these assertions hold at the DATABASE,
/// independent of the EF query filter (Layer 1): every query below uses <c>IgnoreQueryFilters()</c>, so
/// the only thing that can scope the results is RLS via the per-connection <c>app.current_org</c> /
/// <c>app.bypass_rls</c> GUCs set by the TenantConnectionInterceptor. (docs/03-multi-tenancy.md)
/// </summary>
[Collection(ApiCollection.Name)]
public sealed class TenantRlsTests(WireHqApiFactory factory)
{
    private readonly WireHqApiFactory _factory = factory;

    [Fact]
    public async Task Rls_scopes_reads_to_the_current_org_blocks_others_and_is_fail_closed()
    {
        var client = _factory.CreateClient();
        var orgA = await RegisterAsync(client, "RLS Tenant A");
        var orgB = await RegisterAsync(client, "RLS Tenant B");

        // --- Scoped to org A: RLS exposes A's rows and hides B's, even with the EF filter disabled.
        using (var scope = _factory.Services.CreateScope())
        {
            scope.ServiceProvider.GetRequiredService<ISettableTenantContext>().SetTenant(orgA);
            var db = scope.ServiceProvider.GetRequiredService<IApplicationDbContext>();

            (await CountMembershipsAsync(db, orgA)).Should().BeGreaterThan(0,
                because: "a request scoped to org A can see org A's own rows");
            (await CountMembershipsAsync(db, orgB)).Should().Be(0,
                because: "RLS blocks org B's rows at the database even when the EF query filter is bypassed");
            (await CountOrganizationRowAsync(db, orgB)).Should().Be(0,
                because: "the organizations root table is scoped by id too");
        }

        // --- Bypass (platform/session-minting/background/seeders): every tenant's rows are visible.
        using (var scope = _factory.CreateBypassScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<IApplicationDbContext>();
            (await CountMembershipsAsync(db, orgA)).Should().BeGreaterThan(0);
            (await CountMembershipsAsync(db, orgB)).Should().BeGreaterThan(0,
                because: "app.bypass_rls opts trusted cross-tenant paths out of the policy");
        }

        // --- Fail-closed: no org and no bypass ⇒ no tenant rows at all.
        using (var scope = _factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<IApplicationDbContext>();
            (await CountMembershipsAsync(db, orgA)).Should().Be(0);
            (await CountMembershipsAsync(db, orgB)).Should().Be(0,
                because: "with neither app.current_org nor app.bypass_rls set, the policy matches no rows");
        }
    }

    private static Task<int> CountMembershipsAsync(IApplicationDbContext db, Guid organizationId) =>
        db.Memberships.IgnoreQueryFilters().CountAsync(m => m.OrganizationId == organizationId);

    private static Task<int> CountOrganizationRowAsync(IApplicationDbContext db, Guid organizationId) =>
        db.Organizations.IgnoreQueryFilters().CountAsync(o => o.Id == organizationId);

    private static async Task<Guid> RegisterAsync(HttpClient client, string name)
    {
        var email = $"{name.Replace(' ', '.').ToLower()}+{Guid.NewGuid():N}@wirehq.test";
        var response = await client.PostAsJsonAsync("/api/v1/auth/register",
            new { email, password = "Sup3rSecret!!", firstName = name, lastName = "Test", acceptTerms = true });
        var body = (await response.Content.ReadFromJsonAsync<RegisterResponse>())!;
        return body.OrganizationId;
    }

    private sealed record RegisterResponse(Guid UserId, Guid OrganizationId, string OrganizationSlug);
}
