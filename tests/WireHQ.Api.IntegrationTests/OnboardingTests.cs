using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using WireHQ.Application.Abstractions.Persistence;
using Xunit;

namespace WireHQ.Api.IntegrationTests;

/// <summary>
/// Frictionless signup + the Welcome Wizard. Proves: signup needs no organization (a personal workspace
/// is auto-provisioned) and requires Terms acceptance; the wizard round-trips and flips
/// <c>onboardingPending</c>; completing it with a company name renames the workspace; and the soft
/// email-verify gate blocks a sensitive action until the email is confirmed.
/// </summary>
[Collection(ApiCollection.Name)]
public sealed class OnboardingTests(WireHqApiFactory factory)
{
    private readonly WireHqApiFactory _factory = factory;

    [Fact]
    public async Task Signup_needs_no_org_and_auto_provisions_a_personal_workspace()
    {
        var client = _factory.CreateClient();
        var email = Unique("ada");

        var register = await client.PostAsJsonAsync("/api/v1/auth/register", new
        {
            email,
            password = "Sup3rSecret!!",
            firstName = "Ada",
            lastName = "Lovelace",
            acceptTerms = true,
        });
        register.StatusCode.Should().Be(HttpStatusCode.Created);

        Authorize(client, await LoginAsync(client, email));
        var me = (await client.GetFromJsonAsync<MeBody>("/api/v1/auth/me"))!;
        me.FirstName.Should().Be("Ada");
        me.LastName.Should().Be("Lovelace");
        me.Organizations.Should().ContainSingle();
        me.Organizations[0].Name.Should().Be("Ada's Workspace");
        me.OnboardingPending.Should().BeTrue();
    }

    [Fact]
    public async Task Signup_requires_accepting_the_terms()
    {
        var client = _factory.CreateClient();
        var response = await client.PostAsJsonAsync("/api/v1/auth/register", new
        {
            email = Unique("noterms"),
            password = "Sup3rSecret!!",
            firstName = "No",
            lastName = "Terms",
            acceptTerms = false,
        });
        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }

    [Fact]
    public async Task Wizard_completes_renames_the_workspace_and_clears_pending()
    {
        var client = _factory.CreateClient();
        var email = Unique("wizard");
        await RegisterAsync(client, email, "Grace", "Hopper");
        Authorize(client, await LoginAsync(client, email));

        var before = (await client.GetFromJsonAsync<OnboardingBody>("/api/v1/onboarding"))!;
        before.Status.Should().Be("Pending");
        before.ShouldShow.Should().BeTrue();

        var save = await client.PutAsJsonAsync("/api/v1/onboarding", new
        {
            companyName = "Acme Corp",
            companyWebsite = "https://acme.example",
            industry = "Software",
            teamSize = "11-50",
            vpnUsers = "51-200",
            currentVpnSolution = "OpenVPN",
            useCase = "Msp",
        });
        save.StatusCode.Should().Be(HttpStatusCode.NoContent);

        var after = (await client.GetFromJsonAsync<OnboardingBody>("/api/v1/onboarding"))!;
        after.Status.Should().Be("Completed");
        after.ShouldShow.Should().BeFalse();
        after.UseCase.Should().Be("Msp");

        var me = (await client.GetFromJsonAsync<MeBody>("/api/v1/auth/me"))!;
        me.OnboardingPending.Should().BeFalse();
        me.Organizations[0].Name.Should().Be("Acme Corp", because: "a company name renames the auto workspace");
    }

    [Fact]
    public async Task Wizard_can_be_skipped()
    {
        var client = _factory.CreateClient();
        var email = Unique("skip");
        await RegisterAsync(client, email, "Skip", "Me");
        Authorize(client, await LoginAsync(client, email));

        (await client.PostAsync("/api/v1/onboarding/skip", null)).StatusCode.Should().Be(HttpStatusCode.NoContent);

        var after = (await client.GetFromJsonAsync<OnboardingBody>("/api/v1/onboarding"))!;
        after.Status.Should().Be("Skipped");
        after.ShouldShow.Should().BeFalse();

        var me = (await client.GetFromJsonAsync<MeBody>("/api/v1/auth/me"))!;
        me.OnboardingPending.Should().BeFalse();
    }

    [Fact]
    public async Task Soft_gate_blocks_a_sensitive_action_until_the_email_is_verified()
    {
        var client = _factory.CreateClient();
        var email = Unique("verify");
        await RegisterAsync(client, email, "Vera", "Verify");
        Authorize(client, await LoginAsync(client, email));

        // Unverified → creating a team (an IRequiresVerifiedEmail command) is blocked.
        var blocked = await client.PostAsJsonAsync("/api/v1/teams", new { name = "Network Team" });
        blocked.StatusCode.Should().Be(HttpStatusCode.Forbidden);
        (await blocked.Content.ReadFromJsonAsync<ProblemBody>())!.Code.Should().Be("auth.email_unverified");

        // Verify the email out-of-band (doesn't rotate the security stamp, so the token stays valid).
        await VerifyEmailAsync(email);

        var allowed = await client.PostAsJsonAsync("/api/v1/teams", new { name = "Network Team" });
        allowed.StatusCode.Should().Be(HttpStatusCode.Created);
    }

    // ---- helpers ----

    private static string Unique(string prefix) => $"{prefix}+{Guid.NewGuid():N}@wirehq.test";

    private static async Task RegisterAsync(HttpClient client, string email, string first, string last)
    {
        var response = await client.PostAsJsonAsync("/api/v1/auth/register", new
        {
            email,
            password = "Sup3rSecret!!",
            firstName = first,
            lastName = last,
            acceptTerms = true,
        });
        response.StatusCode.Should().Be(HttpStatusCode.Created);
    }

    private static async Task<string> LoginAsync(HttpClient client, string email)
    {
        var login = await client.PostAsJsonAsync("/api/v1/auth/login", new { email, password = "Sup3rSecret!!" });
        login.StatusCode.Should().Be(HttpStatusCode.OK);
        return (await login.Content.ReadFromJsonAsync<TokenBody>())!.AccessToken;
    }

    private async Task VerifyEmailAsync(string email)
    {
        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<IApplicationDbContext>();
        var user = await db.Users.IgnoreQueryFilters().FirstAsync(u => u.Email.Value == email);
        user.VerifyEmail();
        await db.SaveChangesAsync(CancellationToken.None);
    }

    private static void Authorize(HttpClient client, string token) =>
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);

    private sealed record TokenBody(string AccessToken, int ExpiresIn);
    private sealed record OrgBody(Guid OrganizationId, string Slug, string Name, string Status);
    private sealed record MeBody(string? FirstName, string? LastName, bool OnboardingPending, List<OrgBody> Organizations);
    private sealed record OnboardingBody(string Status, bool ShouldShow, string UseCase);
    private sealed record ProblemBody(string Code);
}
