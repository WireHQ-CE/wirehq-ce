using System.Net;
using System.Net.Http.Json;
using FluentAssertions;
using Microsoft.AspNetCore.Hosting;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using WireHQ.Application.Abstractions.Security;
using WireHQ.Application.Organizations;
using WireHQ.Infrastructure.Persistence;
using WireHQ.Infrastructure.Persistence.Seeding;
using Xunit;

namespace WireHQ.Api.IntegrationTests;

/// <summary>
/// The self-host first-run path (docs/17-community-edition.md): a <c>Seed:OwnerEmail</c>/<c>OwnerPassword</c>-driven
/// <see cref="SelfHostOwnerSeeder"/> bootstraps the first organization Owner (so a Community Edition install is
/// usable without open signup), and <c>Auth:OpenRegistration=false</c> turns self-serve registration off — enforced
/// by the API (403) and advertised on the anonymous security-config so the auth pages hide signup.
/// </summary>
[Collection(ApiCollection.Name)]
public sealed class SelfHostFirstRunTests(WireHqApiFactory factory)
{
    private readonly WireHqApiFactory _factory = factory;

    [Fact]
    public async Task Owner_seeder_bootstraps_a_working_owner_and_is_idempotent()
    {
        var email = $"selfhost-owner+{Guid.NewGuid():N}@wirehq.test";
        const string password = "Sup3rSecret-Owner!!";
        var organizationName = $"Self-Host Org {Guid.NewGuid():N}"[..24];

        // Run the seeder exactly as boot does: real DB, RLS bypass (Program.cs sets the bypass on the
        // seeding scope), the real provisioner — only the configuration is test-supplied.
        await RunSeederAsync(email, password, organizationName);

        Guid ownerId;
        using (var scope = _factory.CreateBypassScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();

            var owner = await db.Users.IgnoreQueryFilters().SingleAsync(u => u.Email.Value == email);
            owner.EmailVerified.Should().BeTrue(because: "the seeded owner must not be nagged to verify");
            ownerId = owner.Id;

            var membership = await db.Memberships.IgnoreQueryFilters()
                .Include(m => m.Roles)
                .SingleAsync(m => m.UserId == owner.Id);
            var role = await db.Roles.IgnoreQueryFilters()
                .SingleAsync(r => r.Id == membership.Roles.Single().RoleId);
            role.Name.Should().Be("Owner", because: "the founder gets the full-catalog Owner system role");

            var organization = await db.Organizations.IgnoreQueryFilters()
                .SingleAsync(o => o.Id == membership.OrganizationId);
            organization.Name.Should().Be(organizationName);
        }

        // Idempotent: a second boot (same config) must not create a duplicate user or org.
        await RunSeederAsync(email, password, organizationName);
        using (var scope = _factory.CreateBypassScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
            (await db.Users.IgnoreQueryFilters().CountAsync(u => u.Email.Value == email)).Should().Be(1);
            (await db.Memberships.IgnoreQueryFilters().CountAsync(m => m.UserId == ownerId)).Should().Be(1);
        }

        // And the seeded credentials actually work end-to-end.
        var client = _factory.CreateClient();
        var login = await client.PostAsJsonAsync("/api/v1/auth/login", new { email, password });
        login.StatusCode.Should().Be(HttpStatusCode.OK);
    }

    [Fact]
    public async Task Registration_can_be_disabled_for_invite_only_installs()
    {
        // A derived host with Auth:OpenRegistration=false (same disposable Postgres; UseSetting flows
        // into the app configuration the same way the factory's UseEnvironment does).
        using var inviteOnly = _factory.WithWebHostBuilder(builder =>
            builder.UseSetting("Auth:OpenRegistration", "false"));
        var client = inviteOnly.CreateClient();

        var register = await client.PostAsJsonAsync("/api/v1/auth/register", new
        {
            email = $"blocked+{Guid.NewGuid():N}@wirehq.test",
            password = "Sup3rSecret!!",
            firstName = "Blocked",
            lastName = "Signup",
            acceptTerms = true,
        });
        register.StatusCode.Should().Be(HttpStatusCode.Forbidden);
        (await register.Content.ReadAsStringAsync()).Should().Contain("auth.registration_disabled");

        // The anonymous security-config advertises it so the auth pages hide signup.
        var config = await client.GetFromJsonAsync<SecurityConfigDto>("/api/v1/auth/security-config");
        config!.RegistrationEnabled.Should().BeFalse();
    }

    [Fact]
    public async Task Registration_is_open_by_default()
    {
        var client = _factory.CreateClient();

        var config = await client.GetFromJsonAsync<SecurityConfigDto>("/api/v1/auth/security-config");
        config!.RegistrationEnabled.Should().BeTrue(because: "open signup is the SaaS default posture");
        // AuthFlowTests covers that register itself succeeds on the default host.
    }

    /// <summary>Runs the real seeder in a fresh bypass scope with test-supplied configuration.</summary>
    private async Task RunSeederAsync(string email, string password, string organizationName)
    {
        using var scope = _factory.CreateBypassScope();
        var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["Seed:OwnerEmail"] = email,
            ["Seed:OwnerPassword"] = password,
            ["Seed:OwnerName"] = "Self-Host Owner",
            ["Seed:OrganizationName"] = organizationName,
        }).Build();

        var seeder = new SelfHostOwnerSeeder(
            db,
            scope.ServiceProvider.GetRequiredService<IPasswordHasher>(),
            scope.ServiceProvider.GetRequiredService<OrganizationProvisioner>(),
            configuration,
            NullLogger<SelfHostOwnerSeeder>.Instance);

        await seeder.SeedAsync(CancellationToken.None);
    }

    private sealed record SecurityConfigDto(bool TurnstileEnabled, string? TurnstileSiteKey, bool RegistrationEnabled);
}
