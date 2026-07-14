using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using WireHQ.Application.Abstractions;
using WireHQ.Application.Abstractions.Persistence;
using WireHQ.Application.Abstractions.Security;
using WireHQ.Application.Features.Notifications;
using WireHQ.Domain.Notifications;
using WireHQ.Domain.Organizations;
using Xunit;

namespace WireHQ.Api.IntegrationTests;

/// <summary>
/// Notifications Wave 1 (docs/35-notifications.md). Exercises the real dispatch spine against Postgres: an audited
/// action matching a rule is captured into the outbox by the interceptor, the scheduler expands it to per-recipient
/// deliveries and sends them via the Email channel. The load-bearing guard is <b>tenant isolation</b> — an event in
/// one org must never produce a delivery addressed to another org's user (blocker B-2). Kept-core.
/// </summary>
[Collection(ApiCollection.Name)]
public sealed class NotificationsTests : IDisposable
{
    private const string Password = "Sup3rSecret-Notify!1";

    private readonly WireHqApiFactory _factory;
    private readonly Microsoft.AspNetCore.Mvc.Testing.WebApplicationFactory<Program> _host;

    public NotificationsTests(WireHqApiFactory factory)
    {
        _factory = factory;
        _host = factory.WithWebHostBuilder(_ => { });
    }

    public void Dispose() => _host.Dispose();

    [Fact]
    public async Task A_matching_event_is_captured_and_delivered_only_to_its_own_org()
    {
        // Two orgs, each with an owner. Only org A has a rule.
        var (clientA, orgA, emailA) = await RegisterOwnerAsync();
        var (_, _, emailB) = await RegisterOwnerAsync();

        await CreateRuleAsync(clientA, "api.keys.*", NotificationAudience.OptedInUsers);
        await _host.Services.GetRequiredService<NotificationRouteCache>().RefreshAsync(default);

        // A real audited action in org A (creating an API key writes api.keys.created) → the interceptor captures a job.
        (await clientA.PostAsJsonAsync("/api/v1/api-keys",
            new { name = "trigger", scopes = new[] { "identity.users.read" }, expiresAtUtc = (DateTimeOffset?)null }))
            .StatusCode.Should().Be(HttpStatusCode.Created);

        // Drain: expand the job to per-recipient deliveries and send them via the Email channel.
        await _host.Services.GetRequiredService<NotificationDispatchScheduler>().RunDueAsync(default);

        var deliveries = await DeliveriesForOrgAsync(orgA);
        deliveries.Should().NotBeEmpty("the matching event should have produced at least one delivery");
        deliveries.Should().OnlyContain(d => d.Recipient == emailA, "org A's event must only notify org A's users");
        deliveries.Should().NotContain(d => d.Recipient == emailB, "org B's user must never receive org A's event (tenant isolation)");
        deliveries.Should().OnlyContain(d => d.Status == NotificationDeliveryStatus.Delivered, "the Email channel stub succeeds");
    }

    [Fact]
    public async Task The_free_email_rule_quota_is_enforced()
    {
        var (client, _, _) = await RegisterOwnerAsync();

        for (var i = 0; i < CreateNotificationRuleCommand.FreeEmailRuleQuota; i++)
        {
            (await CreateRuleResponseAsync(client, "mfa.*", NotificationAudience.OptedInUsers))
                .StatusCode.Should().Be(HttpStatusCode.Created);
        }

        // One past the free quota is rejected.
        (await CreateRuleResponseAsync(client, "mfa.*", NotificationAudience.OptedInUsers))
            .StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }

    [Fact]
    public async Task A_chat_rule_is_delivered_to_the_configured_webhook()
    {
        var (client, orgId, _) = await RegisterOwnerAsync(); // Enterprise → holds notifications.chat
        using var sink = new WebhookSink(HttpStatusCode.OK);
        await ConfigureChatDestinationAsync(orgId, sink.Url);

        await CreateRuleAsync(client, "api.keys.*", NotificationAudience.OptedInUsers, ChannelKind.Chat);
        await _host.Services.GetRequiredService<NotificationRouteCache>().RefreshAsync(default);

        (await client.PostAsJsonAsync("/api/v1/api-keys",
            new { name = "trigger", scopes = new[] { "identity.users.read" }, expiresAtUtc = (DateTimeOffset?)null }))
            .StatusCode.Should().Be(HttpStatusCode.Created);

        await _host.Services.GetRequiredService<NotificationDispatchScheduler>().RunDueAsync(default);

        sink.Requests.Should().HaveCount(1, "the Chat rule posts the matched event to the configured webhook — once, not once per user");
        sink.Requests[0].Body.Should().Contain("api.keys.created");
        (await DeliveriesForOrgAsync(orgId)).Should().OnlyContain(d => d.Status == NotificationDeliveryStatus.Delivered);
    }

    [Fact]
    public async Task A_deactivated_chat_module_stops_queued_chat_deliveries()
    {
        var (client, orgId, _) = await RegisterOwnerAsync();
        using var sink = new WebhookSink(HttpStatusCode.OK);
        await ConfigureChatDestinationAsync(orgId, sink.Url);

        await CreateRuleAsync(client, "api.keys.*", NotificationAudience.OptedInUsers, ChannelKind.Chat);
        await _host.Services.GetRequiredService<NotificationRouteCache>().RefreshAsync(default);

        // Capture a Chat job while still entitled...
        (await client.PostAsJsonAsync("/api/v1/api-keys",
            new { name = "trigger", scopes = new[] { "identity.users.read" }, expiresAtUtc = (DateTimeOffset?)null }))
            .StatusCode.Should().Be(HttpStatusCode.Created);

        // ...then lose the entitlement (downgrade off Enterprise) before the sweep sends.
        await _factory.SetEditionAsync(orgId, OrganizationEdition.Community);

        await _host.Services.GetRequiredService<NotificationDispatchScheduler>().RunDueAsync(default);

        sink.Requests.Should().BeEmpty("a deactivated Chat module must not send queued deliveries (MM-14)");
        (await DeliveriesForOrgAsync(orgId)).Should().OnlyContain(d => d.Status == NotificationDeliveryStatus.Cancelled);
    }

    private async Task ConfigureChatDestinationAsync(Guid organizationId, string url)
    {
        using var scope = _host.Services.CreateScope();
        scope.ServiceProvider.GetRequiredService<ISettableTenantContext>().SetBypass();
        var db = scope.ServiceProvider.GetRequiredService<IApplicationDbContext>();
        var protector = scope.ServiceProvider.GetRequiredService<ISecretProtector>();
        var config = NotificationChannelConfig.Create(organizationId, ChannelKind.Chat);
        config.SetChatDestination(NotificationProviderKind.Slack, protector.Protect(url), fromValue: null);
        db.NotificationChannelConfigs.Add(config);
        await db.SaveChangesAsync(default);
    }

    private async Task<HttpResponseMessage> CreateRuleResponseAsync(
        HttpClient client, string pattern, NotificationAudience audience, ChannelKind channel = ChannelKind.Email) =>
        await client.PostAsJsonAsync("/api/v1/notifications/rules", new
        {
            name = "rule",
            eventPattern = pattern,
            channelKind = channel.ToString(),
            audience = audience.ToString(),
            audienceRef = (Guid?)null,
        });

    private async Task CreateRuleAsync(HttpClient client, string pattern, NotificationAudience audience, ChannelKind channel = ChannelKind.Email) =>
        (await CreateRuleResponseAsync(client, pattern, audience, channel)).StatusCode.Should().Be(HttpStatusCode.Created);

    // Scoped to one org — the collection shares a database, so an unscoped query would pick up other tests' rows.
    private async Task<List<DeliveryRow>> DeliveriesForOrgAsync(Guid organizationId)
    {
        using var scope = _host.Services.CreateScope();
        scope.ServiceProvider.GetRequiredService<ISettableTenantContext>().SetBypass();
        var db = scope.ServiceProvider.GetRequiredService<IApplicationDbContext>();
        return await db.NotificationDeliveries
            .IgnoreQueryFilters()
            .Where(d => d.OrganizationId == organizationId)
            .AsNoTracking()
            .Select(d => new DeliveryRow(d.Recipient, d.Status))
            .ToListAsync();
    }

    private async Task<(HttpClient Client, Guid OrganizationId, string Email)> RegisterOwnerAsync()
    {
        var client = _host.CreateClient();
        var email = $"notify+{Guid.NewGuid():N}@wirehq.test";
        (await client.PostAsJsonAsync("/api/v1/auth/register",
            new { email, password = Password, firstName = "Notify", lastName = "Owner", acceptTerms = true })).EnsureSuccessStatusCode();
        await _factory.VerifyEmailAsync(email);

        var login = await client.PostAsJsonAsync("/api/v1/auth/login", new { email, password = Password });
        login.EnsureSuccessStatusCode();
        var token = (await login.Content.ReadFromJsonAsync<LoginDto>())!.AccessToken;
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);

        var me = (await client.GetFromJsonAsync<MeDto>("/api/v1/auth/me"))!;
        await _factory.SetEditionAsync(me.ActiveOrganizationId!.Value, OrganizationEdition.Enterprise);
        return (client, me.ActiveOrganizationId!.Value, email);
    }

    private sealed record LoginDto(string AccessToken);
    private sealed record MeDto(Guid? ActiveOrganizationId);
    private sealed record DeliveryRow(string Recipient, NotificationDeliveryStatus Status);
}
