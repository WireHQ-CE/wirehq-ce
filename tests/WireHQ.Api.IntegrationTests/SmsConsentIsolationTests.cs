using System.Net.Http.Headers;
using System.Net.Http.Json;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using WireHQ.Application.Abstractions;
using WireHQ.Application.Abstractions.Persistence;
using WireHQ.Application.Features.Notifications;
using WireHQ.Domain.Memberships;
using WireHQ.Domain.Notifications;
using WireHQ.Domain.Organizations;
using WireHQ.Domain.ValueObjects;
using Xunit;

namespace WireHQ.Api.IntegrationTests;

/// <summary>
/// D-3 (docs/36 §4.1): SMS consent is <b>per-(org, user)</b>. This proves the load-bearing property against a real
/// Postgres via the dispatch spine — a user who belongs to two orgs and consented in <b>org A only</b>, with a verified
/// phone, is resolved into org A's SMS audience but <b>never</b> org B's, even though they're a verified member of both.
/// The fix is the explicit <c>OrganizationId</c> predicate on the consent read in <c>ResolveSmsAudienceAsync</c>.
/// (SMS stays dark, so the org-A delivery is expanded then Cancelled by the send-gate — we assert on the expanded row.)
/// </summary>
[Collection(ApiCollection.Name)]
public sealed class SmsConsentIsolationTests : IDisposable
{
    private const string Password = "Sup3rSecret-Consent!1";
    private const string Phone = "+447700900321";

    private readonly WireHqApiFactory _factory;
    private readonly Microsoft.AspNetCore.Mvc.Testing.WebApplicationFactory<Program> _host;

    public SmsConsentIsolationTests(WireHqApiFactory factory)
    {
        _factory = factory;
        _host = factory.WithWebHostBuilder(_ => { });
    }

    public void Dispose() => _host.Dispose();

    [Fact]
    public async Task Consent_in_one_org_does_not_resolve_the_user_into_another_orgs_sms_audience()
    {
        // U owns org A; V owns org B (V is just there so org B exists). Both Enterprise (so both hold notifications.sms).
        var (userId, orgA) = await RegisterOwnerAsync();
        var (_, orgB) = await RegisterOwnerAsync();

        using (var scope = BypassScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<IApplicationDbContext>();
            var now = scope.ServiceProvider.GetRequiredService<IDateTimeProvider>().UtcNow;

            // U is also an active member of org B, and has a verified phone (the ownership half of the audience).
            db.Set<Membership>().Add(Membership.CreateActive(orgB, userId, Array.Empty<Guid>()));
            var user = await db.Users.IgnoreQueryFilters().FirstAsync(u => u.Id == userId);
            user.SetVerifiedPhone(PhoneNumber.Create(Phone).Value, now);

            // Consent for org A ONLY — no row for org B (default-OFF).
            db.SmsConsents.Add(SmsConsent.Create(orgA, userId, consented: true, SmsNotificationDisclosure.DisclosureVersion, now));

            // A critical-only SMS rule in BOTH orgs, and a captured job for each (as if `mfa.disabled` fired in both).
            foreach (var org in new[] { orgA, orgB })
            {
                var rule = NotificationRule.Create(
                    org, "MFA disabled", "mfa.disabled", ChannelKind.Sms, NotificationAudience.OptedInUsers, null,
                    requiredFeatures: new[] { "notifications.sms" }).Value;
                db.NotificationRules.Add(rule);
                db.NotificationJobs.Add(NotificationJob.Create(
                    org, rule.Id, Guid.CreateVersion7(), "mfa.disabled", "MFA disabled for a user", DigestCadence.Immediate, now));
            }

            await db.SaveChangesAsync(default);
        }

        // Drain: expand the jobs to per-recipient SMS deliveries (the audience resolver runs here).
        await _host.Services.GetRequiredService<NotificationDispatchScheduler>().RunDueAsync(default);

        var orgASms = await SmsRecipientsForOrgAsync(orgA);
        var orgBSms = await SmsRecipientsForOrgAsync(orgB);

        orgASms.Should().ContainSingle().Which.Should().Be(Phone, "U consented in org A and has a verified phone");
        orgBSms.Should().BeEmpty("U never consented in org B — consent from org A must not leak (D-3)");
    }

    // ---- helpers ----

    private IServiceScope BypassScope()
    {
        var scope = _host.Services.CreateScope();
        scope.ServiceProvider.GetRequiredService<ISettableTenantContext>().SetBypass();
        return scope;
    }

    private async Task<List<string>> SmsRecipientsForOrgAsync(Guid organizationId)
    {
        using var scope = BypassScope();
        var db = scope.ServiceProvider.GetRequiredService<IApplicationDbContext>();
        return await db.NotificationDeliveries
            .IgnoreQueryFilters()
            .Where(d => d.OrganizationId == organizationId && d.ChannelKind == ChannelKind.Sms)
            .AsNoTracking()
            .Select(d => d.Recipient)
            .ToListAsync();
    }

    private async Task<(Guid UserId, Guid OrganizationId)> RegisterOwnerAsync()
    {
        var client = _host.CreateClient();
        var email = $"consent+{Guid.NewGuid():N}@wirehq.test";
        (await client.PostAsJsonAsync("/api/v1/auth/register",
            new { email, password = Password, firstName = "Consent", lastName = "Owner", acceptTerms = true })).EnsureSuccessStatusCode();
        await _factory.VerifyEmailAsync(email);

        var login = await client.PostAsJsonAsync("/api/v1/auth/login", new { email, password = Password });
        login.EnsureSuccessStatusCode();
        var token = (await login.Content.ReadFromJsonAsync<LoginDto>())!.AccessToken;
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);

        var me = (await client.GetFromJsonAsync<MeDto>("/api/v1/auth/me"))!;
        var orgId = me.ActiveOrganizationId!.Value;
        await _factory.SetEditionAsync(orgId, OrganizationEdition.Enterprise);

        using var scope = BypassScope();
        var db = scope.ServiceProvider.GetRequiredService<IApplicationDbContext>();
        var userId = (await db.Users.IgnoreQueryFilters().AsNoTracking().FirstAsync(u => u.Email.Value == email)).Id;
        return (userId, orgId);
    }

    private sealed record LoginDto(string AccessToken);
    private sealed record MeDto(Guid? ActiveOrganizationId);
}
