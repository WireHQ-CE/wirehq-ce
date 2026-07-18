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
        var (client, orgId, _) = await RegisterOwnerAsync();
        // The free quota only bites WITHOUT notifications.routing — a routing org promotes over-quota Email rules to
        // routed rules instead (Slice B). Use Community so the "quota is enforced" guarantee is exercised.
        await _factory.SetEditionAsync(orgId, OrganizationEdition.Community);

        for (var i = 0; i < CreateNotificationRuleCommand.FreeEmailRuleQuota; i++)
        {
            (await CreateRuleResponseAsync(client, "mfa.*", NotificationAudience.OptedInUsers))
                .StatusCode.Should().Be(HttpStatusCode.Created);
        }

        // One past the free quota is rejected (the org holds no notifications.routing to lift it).
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
        // With the entitlement lost before the sweep, the expand step skips the job (docs/35 §4.4) — no delivery is
        // materialised at all; either way nothing is sent, which is the guarantee.
        (await DeliveriesForOrgAsync(orgId)).Should().NotContain(d => d.Status == NotificationDeliveryStatus.Delivered,
            "no Chat message may escape once the module is deactivated (MM-14)");
    }

    [Fact]
    public async Task Creating_a_multi_pattern_rule_requires_the_routing_module()
    {
        var (client, orgId, _) = await RegisterOwnerAsync(); // Enterprise → holds notifications.routing
        await _factory.SetEditionAsync(orgId, OrganizationEdition.Community); // ...drop it

        // Without notifications.routing, a multi-pattern (advanced) rule is rejected (NotificationErrors.AdvancedRequired).
        (await CreateRuleResponseAsync(client, "mfa.*", NotificationAudience.OptedInUsers, additionalPatterns: ["identity.users.*"]))
            .StatusCode.Should().Be(HttpStatusCode.BadRequest, "a multi-pattern rule needs the Advanced Notifications module");

        // A single-pattern free-core Email rule is still allowed on Community.
        (await CreateRuleResponseAsync(client, "mfa.*", NotificationAudience.OptedInUsers))
            .StatusCode.Should().Be(HttpStatusCode.Created);

        // Restore routing → the same multi-pattern shape now succeeds.
        await _factory.SetEditionAsync(orgId, OrganizationEdition.Enterprise);
        (await CreateRuleResponseAsync(client, "policy.access.*", NotificationAudience.OptedInUsers, additionalPatterns: ["identity.users.*"]))
            .StatusCode.Should().Be(HttpStatusCode.Created, "with notifications.routing the advanced shape is allowed");
    }

    [Fact]
    public async Task Routed_email_rules_do_not_consume_the_free_quota()
    {
        var (client, _, _) = await RegisterOwnerAsync(); // Enterprise → holds notifications.routing

        // Five MULTI-PATTERN (routed) Email rules — each stores notifications.routing, so none is free-core.
        for (var i = 0; i < CreateNotificationRuleCommand.FreeEmailRuleQuota; i++)
        {
            (await CreateRuleResponseAsync(client, $"mfa.{i}.*", NotificationAudience.OptedInUsers, additionalPatterns: ["identity.users.*"]))
                .StatusCode.Should().Be(HttpStatusCode.Created);
        }

        // A 6th FREE single-pattern Email rule is still allowed — routed rules didn't consume the free allowance (finding #21).
        (await CreateRuleResponseAsync(client, "policy.access.*", NotificationAudience.OptedInUsers))
            .StatusCode.Should().Be(HttpStatusCode.Created, "only free-core Email rules count toward the free quota");
    }

    [Fact]
    public async Task Revoking_routing_cancels_a_queued_advanced_delivery_mid_flight()
    {
        var (_, orgId, email) = await RegisterOwnerAsync(); // Enterprise → holds notifications.routing

        // A delivery already queued (Pending) for an advanced rule — its RequiredFeatures carries notifications.routing.
        await QueueAdvancedEmailDeliveryAsync(orgId, email);

        // Lose notifications.routing (downgrade to Community) before the sweep sends.
        await _factory.SetEditionAsync(orgId, OrganizationEdition.Community);

        await _host.Services.GetRequiredService<NotificationDispatchScheduler>().RunDueAsync(default);

        (await DeliveriesForOrgAsync(orgId)).Should().OnlyContain(d => d.Status == NotificationDeliveryStatus.Cancelled,
            "the send-time set-valued MM-14 guard cancels a queued delivery when notifications.routing is revoked");
    }

    [Fact]
    public async Task An_advanced_multi_pattern_rule_delivers_only_to_its_own_org()
    {
        var (clientA, orgA, emailA) = await RegisterOwnerAsync(); // Enterprise → holds notifications.routing
        var (_, _, emailB) = await RegisterOwnerAsync();

        // A multi-pattern (advanced) Email rule in org A whose ADDITIONAL glob is api.keys.*.
        await CreateRuleAsync(clientA, "webhooks.*", NotificationAudience.OptedInUsers, additionalPatterns: ["api.keys.*"]);
        await _host.Services.GetRequiredService<NotificationRouteCache>().RefreshAsync(default);

        (await clientA.PostAsJsonAsync("/api/v1/api-keys",
            new { name = "trigger", scopes = new[] { "identity.users.read" }, expiresAtUtc = (DateTimeOffset?)null }))
            .StatusCode.Should().Be(HttpStatusCode.Created);

        await _host.Services.GetRequiredService<NotificationDispatchScheduler>().RunDueAsync(default);

        var deliveries = await DeliveriesForOrgAsync(orgA);
        deliveries.Should().NotBeEmpty("the additional glob api.keys.* matched api.keys.created (multi-pattern)");
        deliveries.Should().OnlyContain(d => d.Recipient == emailA, "org A's advanced rule must only notify org A's users");
        deliveries.Should().NotContain(d => d.Recipient == emailB, "tenant isolation for an advanced rule (B-2)");
        deliveries.Should().OnlyContain(d => d.Status == NotificationDeliveryStatus.Delivered, "routing is held, so the delivery sends");
    }

    [Fact]
    public async Task Email_rules_beyond_the_free_quota_persist_as_routed_for_a_routing_org()
    {
        var (client, orgId, _) = await RegisterOwnerAsync(); // Enterprise → holds notifications.routing

        // Fill the free quota with 5 plain single-pattern Email rules (free-core).
        for (var i = 0; i < CreateNotificationRuleCommand.FreeEmailRuleQuota; i++)
        {
            (await CreateRuleResponseAsync(client, $"mfa.{i}.*", NotificationAudience.OptedInUsers))
                .StatusCode.Should().Be(HttpStatusCode.Created);
        }

        // The 6th plain Email rule is NOT rejected — a routing org may go beyond the free quota; the rule is promoted
        // to a ROUTED rule ("email beyond the free quota ⇒ notifications.routing", docs/35 §4.4).
        (await CreateRuleResponseAsync(client, "policy.access.*", NotificationAudience.OptedInUsers))
            .StatusCode.Should().Be(HttpStatusCode.Created, "a routing org may exceed the free quota");

        var featureSets = await RuleFeatureSetsForOrgAsync(orgId);
        featureSets.Should().HaveCount(6);
        featureSets.Count(f => f.Length == 0).Should().Be(5, "the first five stay free-core (empty RequiredFeatures)");
        featureSets.Should().ContainSingle(f => f.Contains("notifications.routing"),
            "the 6th (over-quota) Email rule is persisted as routed");
    }

    [Fact]
    public async Task Email_rules_beyond_the_free_quota_are_capped_for_a_non_routing_org()
    {
        var (client, orgId, _) = await RegisterOwnerAsync();
        await _factory.SetEditionAsync(orgId, OrganizationEdition.Community); // no notifications.routing

        for (var i = 0; i < CreateNotificationRuleCommand.FreeEmailRuleQuota; i++)
        {
            (await CreateRuleResponseAsync(client, $"mfa.{i}.*", NotificationAudience.OptedInUsers))
                .StatusCode.Should().Be(HttpStatusCode.Created);
        }

        // Without notifications.routing, the 6th plain Email rule hits the free quota.
        (await CreateRuleResponseAsync(client, "policy.access.*", NotificationAudience.OptedInUsers))
            .StatusCode.Should().Be(HttpStatusCode.BadRequest, "a non-routing org is capped at the free quota");
    }

    [Fact]
    public async Task The_routed_email_daily_cap_is_higher_than_the_free_cap()
    {
        const int freeCap = 500; // == NotificationDispatchScheduler.EmailDailyCapPerOrg (private)

        // A routing org (Enterprise) whose email usage already hit the FREE cap still sends — its cap is raised (bounded).
        var (_, routingOrg, routingEmail) = await RegisterOwnerAsync(); // Enterprise → holds notifications.routing
        await SeedEmailUsageAsync(routingOrg, freeCap);
        await QueueFreeEmailDeliveryAsync(routingOrg, routingEmail);

        // A non-routing org (Community) at the same usage is capped at the free 500/day.
        var (_, freeOrg, freeEmail) = await RegisterOwnerAsync();
        await _factory.SetEditionAsync(freeOrg, OrganizationEdition.Community);
        await SeedEmailUsageAsync(freeOrg, freeCap);
        await QueueFreeEmailDeliveryAsync(freeOrg, freeEmail);

        await _host.Services.GetRequiredService<NotificationDispatchScheduler>().RunDueAsync(default);

        (await DeliveriesForOrgAsync(routingOrg)).Should().OnlyContain(d => d.Status == NotificationDeliveryStatus.Delivered,
            "a routing org's email cap is raised (bounded), so a delivery past the free cap still sends");
        (await DeliveriesForOrgAsync(freeOrg)).Should().OnlyContain(d => d.Status == NotificationDeliveryStatus.Cancelled,
            "a non-routing org is still capped at the free daily limit (500)");
    }

    [Fact]
    public async Task Creating_a_digest_rule_requires_the_routing_module()
    {
        var (client, orgId, _) = await RegisterOwnerAsync(); // Enterprise → holds notifications.routing
        await _factory.SetEditionAsync(orgId, OrganizationEdition.Community); // ...drop it

        // A Daily digest is an advanced shape → needs notifications.routing.
        (await CreateRuleResponseAsync(client, "mfa.*", NotificationAudience.OptedInUsers, digestCadence: DigestCadence.Daily))
            .StatusCode.Should().Be(HttpStatusCode.BadRequest, "a digest rule needs the Advanced Notifications module");

        // A single-pattern Immediate Email rule is still free-core.
        (await CreateRuleResponseAsync(client, "mfa.*", NotificationAudience.OptedInUsers))
            .StatusCode.Should().Be(HttpStatusCode.Created);

        // Restore routing → the digest shape now succeeds.
        await _factory.SetEditionAsync(orgId, OrganizationEdition.Enterprise);
        (await CreateRuleResponseAsync(client, "policy.access.*", NotificationAudience.OptedInUsers, digestCadence: DigestCadence.Daily))
            .StatusCode.Should().Be(HttpStatusCode.Created, "with notifications.routing a digest rule is allowed");
    }

    [Fact]
    public async Task Digest_jobs_are_not_expanded_immediately_and_do_not_block_immediate_jobs()
    {
        var (client, orgId, email) = await RegisterOwnerAsync(); // Enterprise → holds notifications.routing

        // A Daily DIGEST rule AND an IMMEDIATE rule, both matching api.keys.*.
        await CreateRuleAsync(client, "api.keys.*", NotificationAudience.OptedInUsers, digestCadence: DigestCadence.Daily);
        await CreateRuleAsync(client, "api.keys.*", NotificationAudience.OptedInUsers);
        await _host.Services.GetRequiredService<NotificationRouteCache>().RefreshAsync(default);

        (await client.PostAsJsonAsync("/api/v1/api-keys",
            new { name = "trigger", scopes = new[] { "identity.users.read" }, expiresAtUtc = (DateTimeOffset?)null }))
            .StatusCode.Should().Be(HttpStatusCode.Created);

        // One event matches BOTH rules → two jobs: one Immediate (expands + sends now), one Daily (waits for its cursor,
        // excluded from the immediate-expand batch at the SQL level so it can never head-of-line-block the immediate job).
        await _host.Services.GetRequiredService<NotificationDispatchScheduler>().RunDueAsync(default);

        var deliveries = await DeliveriesForOrgAsync(orgId);
        deliveries.Should().ContainSingle("only the immediate rule delivers now; the digest job waits and does not block it");
        deliveries.Should().OnlyContain(d => d.Status == NotificationDeliveryStatus.Delivered && d.Recipient == email);
    }

    [Fact]
    public async Task A_digest_rule_coalesces_events_into_one_delivery_and_advances_its_cursor()
    {
        var (client, orgId, email) = await RegisterOwnerAsync(); // Enterprise → holds notifications.routing

        var ruleId = await CreateRuleReturningIdAsync(client, "api.keys.*", NotificationAudience.OptedInUsers, DigestCadence.Daily);
        await _host.Services.GetRequiredService<NotificationRouteCache>().RefreshAsync(default);

        // A fresh Daily rule gets a concrete, future cursor (a null cursor would never fire — null <= now is UNKNOWN).
        var (cadence0, cursor0) = await DigestStateAsync(ruleId);
        cadence0.Should().Be("Daily");
        cursor0.Should().NotBeNull();
        cursor0.Should().BeAfter(DateTimeOffset.UtcNow);

        // Three matching events → three digest jobs (stamped Daily, coalesced — not one send each).
        for (var i = 0; i < 3; i++)
        {
            (await client.PostAsJsonAsync("/api/v1/api-keys",
                new { name = $"k{i}", scopes = new[] { "identity.users.read" }, expiresAtUtc = (DateTimeOffset?)null }))
                .StatusCode.Should().Be(HttpStatusCode.Created);
        }

        // A tick while the cursor is still in the future: nothing is expanded or flushed.
        await _host.Services.GetRequiredService<NotificationDispatchScheduler>().RunDueAsync(default);
        (await DeliveriesForOrgAsync(orgId)).Should().BeEmpty("digest jobs are not expanded immediately and the cursor isn't due yet");

        // Force the cursor due, then flush.
        await MakeDigestDueAsync(ruleId, DateTimeOffset.UtcNow.AddMinutes(-1));
        await _host.Services.GetRequiredService<NotificationDispatchScheduler>().RunDueAsync(default);

        var deliveries = await DeliveriesForOrgAsync(orgId);
        deliveries.Should().ContainSingle("the three events coalesce into ONE digest delivery per recipient");
        deliveries.Should().OnlyContain(d => d.Recipient == email && d.Status == NotificationDeliveryStatus.Delivered);

        // The cursor advanced to the next anchor boundary strictly after now (UTC midnight for Daily).
        var (_, cursor1) = await DigestStateAsync(ruleId);
        cursor1.Should().NotBeNull();
        cursor1.Should().BeAfter(DateTimeOffset.UtcNow);
        cursor1!.Value.TimeOfDay.Should().Be(TimeSpan.Zero, "the Daily cursor anchors to UTC midnight");
    }

    [Fact]
    public async Task A_digest_flush_advances_the_cursor_even_with_no_events()
    {
        var (client, orgId, _) = await RegisterOwnerAsync(); // Enterprise → holds notifications.routing

        var ruleId = await CreateRuleReturningIdAsync(client, "api.keys.*", NotificationAudience.OptedInUsers, DigestCadence.Weekly);
        await _host.Services.GetRequiredService<NotificationRouteCache>().RefreshAsync(default);

        // No events captured. Force the cursor due.
        await MakeDigestDueAsync(ruleId, DateTimeOffset.UtcNow.AddMinutes(-1));
        var (_, cursorBefore) = await DigestStateAsync(ruleId);

        await _host.Services.GetRequiredService<NotificationDispatchScheduler>().RunDueAsync(default);

        (await DeliveriesForOrgAsync(orgId)).Should().BeEmpty("no events → no digest delivery");
        var (_, cursorAfter) = await DigestStateAsync(ruleId);
        cursorAfter.Should().NotBeNull();
        cursorAfter.Should().BeAfter(DateTimeOffset.UtcNow, "a zero-job window still advances the cursor (never stuck <= now)");
        cursorAfter.Should().NotBe(cursorBefore);
    }

    [Fact]
    public async Task A_digest_flush_delivers_only_to_its_own_org()
    {
        var (clientA, orgA, emailA) = await RegisterOwnerAsync(); // Enterprise → holds notifications.routing
        var (_, _, emailB) = await RegisterOwnerAsync();

        var ruleId = await CreateRuleReturningIdAsync(clientA, "api.keys.*", NotificationAudience.OptedInUsers, DigestCadence.Daily);
        await _host.Services.GetRequiredService<NotificationRouteCache>().RefreshAsync(default);

        (await clientA.PostAsJsonAsync("/api/v1/api-keys",
            new { name = "trigger", scopes = new[] { "identity.users.read" }, expiresAtUtc = (DateTimeOffset?)null }))
            .StatusCode.Should().Be(HttpStatusCode.Created);

        await MakeDigestDueAsync(ruleId, DateTimeOffset.UtcNow.AddMinutes(-1));
        await _host.Services.GetRequiredService<NotificationDispatchScheduler>().RunDueAsync(default);

        var deliveries = await DeliveriesForOrgAsync(orgA);
        deliveries.Should().NotBeEmpty("the digest flush produced a delivery for org A");
        deliveries.Should().OnlyContain(d => d.Recipient == emailA, "a digest flush must only notify its own org's users (B-2)");
        deliveries.Should().NotContain(d => d.Recipient == emailB, "tenant isolation for a digest flush");
    }

    [Fact]
    public async Task Escalation_fires_the_next_step_when_due_and_acknowledge_halts_the_chain()
    {
        var (client, orgId, _) = await RegisterOwnerAsync(); // Enterprise → notifications.routing

        // A rule with a TWO-step escalation chain (5 min each). Trigger a matching event → capture a job.
        _ = await CreateEscalationRuleAsync(client, "api.keys.*", stepCount: 2);
        await _host.Services.GetRequiredService<NotificationRouteCache>().RefreshAsync(default);
        (await client.PostAsJsonAsync("/api/v1/api-keys",
            new { name = "trigger", scopes = new[] { "identity.users.read" }, expiresAtUtc = (DateTimeOffset?)null }))
            .StatusCode.Should().Be(HttpStatusCode.Created);

        // Drain: the primary (level 0) delivers now, and the job goes Escalating with a FUTURE cursor.
        await _host.Services.GetRequiredService<NotificationDispatchScheduler>().RunDueAsync(default);
        var job = await EscalationJobAsync(orgId);
        job.Status.Should().Be(NotificationJobStatus.Escalating);
        job.EscalationLevel.Should().Be(0);
        job.EscalationNextDueAtUtc.Should().NotBeNull().And.Subject.Should().BeAfter(DateTimeOffset.UtcNow);

        // Force the escalation cursor due, drain → EscalateAsync fires step 0 → a level-1 delivery, advance to level 1.
        await MakeEscalationDueAsync(job.Id, DateTimeOffset.UtcNow.AddMinutes(-1));
        await _host.Services.GetRequiredService<NotificationDispatchScheduler>().RunDueAsync(default);
        (await DeliveriesForOrgAsync(orgId)).Should().Contain(d => d.EscalationLevel == 1, "the first escalation step fired");
        var afterStep1 = await GetJobAsync(job.Id);
        afterStep1.EscalationLevel.Should().Be(1);
        afterStep1.Status.Should().Be(NotificationJobStatus.Escalating, "a two-step chain still has one step to go");

        // Acknowledge the alert → the chain stops (settled).
        (await client.PostAsync($"/api/v1/notifications/alerts/{job.Id}/acknowledge", null))
            .StatusCode.Should().Be(HttpStatusCode.NoContent);
        var acked = await GetJobAsync(job.Id);
        acked.Status.Should().Be(NotificationJobStatus.Expanded);
        acked.AcknowledgedAtUtc.Should().NotBeNull();

        // Even with a due cursor, an acknowledged job is no longer a candidate → step 2 never fires.
        await MakeEscalationDueAsync(job.Id, DateTimeOffset.UtcNow.AddMinutes(-1));
        await _host.Services.GetRequiredService<NotificationDispatchScheduler>().RunDueAsync(default);
        (await DeliveriesForOrgAsync(orgId)).Should().NotContain(d => d.EscalationLevel == 2, "acknowledge halted the chain before step 2");
    }

    [Fact]
    public async Task A_delivery_within_quiet_hours_is_deferred_at_send_time_not_sent()
    {
        var (_, orgId, email) = await RegisterOwnerAsync();

        // Queue a Pending delivery whose COPIED quiet window covers now (±2h in UTC) → the send must DEFER it.
        await QueueQuietHoursDeliveryAsync(orgId, email);
        await _host.Services.GetRequiredService<NotificationDispatchScheduler>().RunDueAsync(default);

        var delivery = (await DeliveriesForOrgAsync(orgId)).Should().ContainSingle().Subject;
        delivery.Status.Should().Be(NotificationDeliveryStatus.Pending, "a delivery in quiet hours is deferred, not sent");
        delivery.Attempts.Should().Be(0, "a defer is not a failed attempt");
        delivery.NextAttemptAtUtc.Should().NotBeNull().And.Subject.Should().BeAfter(DateTimeOffset.UtcNow, "deferred to the window's end");
    }

    [Fact]
    public async Task Acknowledging_another_orgs_alert_is_not_found_and_leaves_it_escalating()
    {
        var (clientA, orgA, _) = await RegisterOwnerAsync();
        var (clientB, _, _) = await RegisterOwnerAsync();

        _ = await CreateEscalationRuleAsync(clientA, "api.keys.*", stepCount: 1);
        await _host.Services.GetRequiredService<NotificationRouteCache>().RefreshAsync(default);
        (await clientA.PostAsJsonAsync("/api/v1/api-keys",
            new { name = "trigger", scopes = new[] { "identity.users.read" }, expiresAtUtc = (DateTimeOffset?)null }))
            .StatusCode.Should().Be(HttpStatusCode.Created);
        await _host.Services.GetRequiredService<NotificationDispatchScheduler>().RunDueAsync(default);
        var job = await EscalationJobAsync(orgA);

        // Org B's owner (holds notifications.acknowledge) acknowledges org A's job id → tenant-scoped NotFound, and A's
        // job is untouched. Cross-org protection rests on the ambient filter, NOT IgnoreQueryFilters (blocker B-2).
        (await clientB.PostAsync($"/api/v1/notifications/alerts/{job.Id}/acknowledge", null))
            .StatusCode.Should().Be(HttpStatusCode.NotFound);
        var stillEscalating = await GetJobAsync(job.Id);
        stillEscalating.Status.Should().Be(NotificationJobStatus.Escalating);
        stillEscalating.AcknowledgedAtUtc.Should().BeNull();
    }

    private async Task<Guid> CreateEscalationRuleAsync(HttpClient client, string pattern, int stepCount = 1)
    {
        var steps = Enumerable.Range(0, stepCount)
            .Select(_ => new { delayMinutes = 5, channelKind = "Email", audience = "OptedInUsers", audienceRef = (Guid?)null })
            .ToArray();
        var response = await client.PostAsJsonAsync("/api/v1/notifications/rules", new
        {
            name = "on-call", eventPattern = pattern, additionalPatterns = (string[]?)null,
            channelKind = "Email", audience = "OptedInUsers", audienceRef = (Guid?)null,
            digestCadence = "Immediate", escalationSteps = steps,
        });
        response.StatusCode.Should().Be(HttpStatusCode.Created);
        return await response.Content.ReadFromJsonAsync<Guid>();
    }

    // The (most recent) actively-escalating job for an org (bypass-scoped).
    private async Task<NotificationJob> EscalationJobAsync(Guid organizationId)
    {
        using var scope = _host.Services.CreateScope();
        scope.ServiceProvider.GetRequiredService<ISettableTenantContext>().SetBypass();
        var db = scope.ServiceProvider.GetRequiredService<IApplicationDbContext>();
        return await db.NotificationJobs.IgnoreQueryFilters().AsNoTracking()
            .Where(j => j.OrganizationId == organizationId && j.Status == NotificationJobStatus.Escalating)
            .OrderByDescending(j => j.Id).FirstAsync();
    }

    private async Task<NotificationJob> GetJobAsync(Guid jobId)
    {
        using var scope = _host.Services.CreateScope();
        scope.ServiceProvider.GetRequiredService<ISettableTenantContext>().SetBypass();
        var db = scope.ServiceProvider.GetRequiredService<IApplicationDbContext>();
        return await db.NotificationJobs.IgnoreQueryFilters().AsNoTracking().FirstAsync(j => j.Id == jobId);
    }

    // Force a job's escalation cursor into the past so EscalateAsync treats the next step as due this tick.
    private async Task MakeEscalationDueAsync(Guid jobId, DateTimeOffset when)
    {
        using var scope = _host.Services.CreateScope();
        scope.ServiceProvider.GetRequiredService<ISettableTenantContext>().SetBypass();
        var db = scope.ServiceProvider.GetRequiredService<IApplicationDbContext>();
        await db.NotificationJobs.IgnoreQueryFilters().Where(j => j.Id == jobId)
            .ExecuteUpdateAsync(s => s.SetProperty(j => j.EscalationNextDueAtUtc, when), default);
    }

    // Queue a Pending free-core Email delivery whose copied quiet-hours window covers now (±2h, UTC), so the send-time
    // defer can be exercised on an already-queued row.
    private async Task QueueQuietHoursDeliveryAsync(Guid organizationId, string recipient)
    {
        using var scope = _host.Services.CreateScope();
        scope.ServiceProvider.GetRequiredService<ISettableTenantContext>().SetBypass();
        var db = scope.ServiceProvider.GetRequiredService<IApplicationDbContext>();
        var now = scope.ServiceProvider.GetRequiredService<IDateTimeProvider>().UtcNow;
        var start = TimeOnly.FromDateTime(now.AddHours(-2).UtcDateTime);
        var end = TimeOnly.FromDateTime(now.AddHours(2).UtcDateTime);
        db.NotificationDeliveries.Add(NotificationDelivery.Create(
            organizationId, Guid.CreateVersion7(), Guid.CreateVersion7(), ChannelKind.Email, requiredFeatures: Array.Empty<string>(),
            recipient, "WireHQ: quiet", "a quiet-hours email", dedupValue: null, now,
            quietHoursStart: start, quietHoursEnd: end, quietHoursTimeZone: "Etc/UTC"));
        await db.SaveChangesAsync(default);
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
        HttpClient client, string pattern, NotificationAudience audience, ChannelKind channel = ChannelKind.Email,
        IReadOnlyCollection<string>? additionalPatterns = null, DigestCadence digestCadence = DigestCadence.Immediate) =>
        await client.PostAsJsonAsync("/api/v1/notifications/rules", new
        {
            name = "rule",
            eventPattern = pattern,
            additionalPatterns,
            channelKind = channel.ToString(),
            audience = audience.ToString(),
            audienceRef = (Guid?)null,
            digestCadence = digestCadence.ToString(),
        });

    private async Task CreateRuleAsync(
        HttpClient client, string pattern, NotificationAudience audience, ChannelKind channel = ChannelKind.Email,
        IReadOnlyCollection<string>? additionalPatterns = null, DigestCadence digestCadence = DigestCadence.Immediate) =>
        (await CreateRuleResponseAsync(client, pattern, audience, channel, additionalPatterns, digestCadence)).StatusCode.Should().Be(HttpStatusCode.Created);

    private async Task<Guid> CreateRuleReturningIdAsync(
        HttpClient client, string pattern, NotificationAudience audience, DigestCadence digestCadence = DigestCadence.Immediate)
    {
        var response = await CreateRuleResponseAsync(client, pattern, audience, ChannelKind.Email, additionalPatterns: null, digestCadence);
        response.StatusCode.Should().Be(HttpStatusCode.Created);
        return await response.Content.ReadFromJsonAsync<Guid>();
    }

    // A rule's persisted digest cadence + cursor (bypass-scoped) — to assert cursor init/advance.
    private async Task<(string Cadence, DateTimeOffset? Cursor)> DigestStateAsync(Guid ruleId)
    {
        using var scope = _host.Services.CreateScope();
        scope.ServiceProvider.GetRequiredService<ISettableTenantContext>().SetBypass();
        var db = scope.ServiceProvider.GetRequiredService<IApplicationDbContext>();
        var rule = await db.NotificationRules.IgnoreQueryFilters().AsNoTracking().FirstAsync(r => r.Id == ruleId);
        return (rule.DigestCadence.ToString(), rule.NextDigestAtUtc);
    }

    // Force a digest rule's cursor into the past so FlushDigestsAsync treats it as due this tick.
    private async Task MakeDigestDueAsync(Guid ruleId, DateTimeOffset when)
    {
        using var scope = _host.Services.CreateScope();
        scope.ServiceProvider.GetRequiredService<ISettableTenantContext>().SetBypass();
        var db = scope.ServiceProvider.GetRequiredService<IApplicationDbContext>();
        var rule = await db.NotificationRules.IgnoreQueryFilters().FirstAsync(r => r.Id == ruleId);
        rule.AdvanceDigestCursor(when);
        await db.SaveChangesAsync(default);
    }

    // Queue a Pending delivery for an advanced (routing-gated) rule directly, so the send-time set-valued MM-14 guard
    // can be exercised on an already-queued row (RunDueAsync expands+sends in one tick, so a real capture would send
    // before the entitlement can be revoked).
    private async Task QueueAdvancedEmailDeliveryAsync(Guid organizationId, string recipient)
    {
        using var scope = _host.Services.CreateScope();
        scope.ServiceProvider.GetRequiredService<ISettableTenantContext>().SetBypass();
        var db = scope.ServiceProvider.GetRequiredService<IApplicationDbContext>();
        var now = scope.ServiceProvider.GetRequiredService<IDateTimeProvider>().UtcNow;
        db.NotificationDeliveries.Add(NotificationDelivery.Create(
            organizationId, Guid.CreateVersion7(), Guid.CreateVersion7(), ChannelKind.Email,
            requiredFeatures: new[] { "notifications.routing" },
            recipient, "WireHQ: advanced", "an advanced routed event", dedupValue: null, now));
        await db.SaveChangesAsync(default);
    }

    // Queue a plain free-core Email delivery (empty RequiredFeatures) directly, so the per-org email cap can be
    // exercised without driving a real capture/expand.
    private async Task QueueFreeEmailDeliveryAsync(Guid organizationId, string recipient)
    {
        using var scope = _host.Services.CreateScope();
        scope.ServiceProvider.GetRequiredService<ISettableTenantContext>().SetBypass();
        var db = scope.ServiceProvider.GetRequiredService<IApplicationDbContext>();
        var now = scope.ServiceProvider.GetRequiredService<IDateTimeProvider>().UtcNow;
        db.NotificationDeliveries.Add(NotificationDelivery.Create(
            organizationId, Guid.CreateVersion7(), Guid.CreateVersion7(), ChannelKind.Email,
            requiredFeatures: Array.Empty<string>(),
            recipient, "WireHQ: quota", "a free-core email", dedupValue: null, now));
        await db.SaveChangesAsync(default);
    }

    // Seed today's durable email usage counter to a given count, so the send-time daily cap can be exercised.
    private async Task SeedEmailUsageAsync(Guid organizationId, int count)
    {
        using var scope = _host.Services.CreateScope();
        scope.ServiceProvider.GetRequiredService<ISettableTenantContext>().SetBypass();
        var db = scope.ServiceProvider.GetRequiredService<IApplicationDbContext>();
        var now = scope.ServiceProvider.GetRequiredService<IDateTimeProvider>().UtcNow;
        var usage = NotificationChannelUsage.Start(organizationId, ChannelKind.Email, DateOnly.FromDateTime(now.UtcDateTime));
        for (var i = 0; i < count; i++)
        {
            usage.Increment();
        }

        db.NotificationChannelUsage.Add(usage);
        await db.SaveChangesAsync(default);
    }

    // Each org's stored rule RequiredFeatures sets (bypass-scoped) — to assert an over-quota rule persisted as routed.
    private async Task<List<string[]>> RuleFeatureSetsForOrgAsync(Guid organizationId)
    {
        using var scope = _host.Services.CreateScope();
        scope.ServiceProvider.GetRequiredService<ISettableTenantContext>().SetBypass();
        var db = scope.ServiceProvider.GetRequiredService<IApplicationDbContext>();
        var sets = await db.NotificationRules
            .IgnoreQueryFilters()
            .Where(r => r.OrganizationId == organizationId)
            .AsNoTracking()
            .Select(r => r.RequiredFeatures)
            .ToListAsync();
        return sets.Select(s => s.ToArray()).ToList();
    }

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
            .Select(d => new DeliveryRow(d.Recipient, d.Status, d.EscalationLevel, d.Attempts, d.NextAttemptAtUtc))
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
    private sealed record DeliveryRow(
        string Recipient, NotificationDeliveryStatus Status, int EscalationLevel, int Attempts, DateTimeOffset? NextAttemptAtUtc);
}
