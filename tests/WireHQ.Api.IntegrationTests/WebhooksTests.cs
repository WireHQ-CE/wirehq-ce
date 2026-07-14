using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using FluentAssertions;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using WireHQ.Application.Abstractions;
using WireHQ.Application.Abstractions.Persistence;
using WireHQ.Application.Features.Webhooks;
using WireHQ.Domain.Organizations;
using WireHQ.Domain.Webhooks;
using Xunit;

namespace WireHQ.Api.IntegrationTests;

/// <summary>
/// Webhooks (docs/26-api-keys-webhooks.md §7-8, ADR-043). Exercises the real pipeline against Postgres + a local
/// stub HTTP sink: creating an endpoint returns the signing secret once; an audited action is captured into the
/// outbox by the interceptor, then the dispatch scheduler signs it (HMAC) and POSTs it and marks it delivered; a
/// failing endpoint is retried with backoff; and the feature is Enterprise-gated. Kept-core.
/// </summary>
[Collection(ApiCollection.Name)]
public sealed class WebhooksTests : IDisposable
{
    private const string Password = "Sup3rSecret-Hooks!1";

    private readonly WireHqApiFactory _factory;
    private readonly WebApplicationFactory<Program> _host;

    public WebhooksTests(WireHqApiFactory factory)
    {
        _factory = factory;
        _host = factory.WithWebHostBuilder(_ => { });
    }

    public void Dispose() => _host.Dispose();

    [Fact]
    public async Task Creating_an_endpoint_returns_the_secret_once_and_lists_without_it()
    {
        var (client, _) = await AuthenticateOwnerAsync();

        var created = await CreateEndpointAsync(client, "https://example.com/hook", ["wg.*"]);
        created.SigningSecret.Should().StartWith("whsec_");

        var list = (await client.GetFromJsonAsync<List<EndpointDto>>("/api/v1/webhooks"))!;
        var item = list.Single(e => e.Id == created.Id);
        item.Url.Should().Be("https://example.com/hook");
        item.Status.Should().Be("Active");
        item.EventTypes.Should().Contain("wg.*");
        // The list DTO has no secret field at all — the secret is never projected.
    }

    [Fact]
    public async Task An_audited_action_is_captured_signed_and_delivered()
    {
        var (client, _) = await AuthenticateOwnerAsync();
        using var sink = new WebhookSink(HttpStatusCode.OK);

        // Subscribe to everything, then make the sender aware of the new endpoint (the background loop is off in tests).
        var endpoint = await CreateEndpointAsync(client, sink.Url, ["*"]);
        await _host.Services.GetRequiredService<WebhookSubscriptionCache>().RefreshAsync(default);

        // A real audited action (creating an API key writes api.keys.created) → the outbox interceptor captures it.
        (await client.PostAsJsonAsync("/api/v1/api-keys",
            new { name = "trigger", scopes = new[] { "identity.users.read" }, expiresAtUtc = (DateTimeOffset?)null }))
            .StatusCode.Should().Be(HttpStatusCode.Created);

        var captured = await SingleDeliveryAsync(endpoint.Id);
        captured.Status.Should().Be(WebhookDeliveryStatus.Pending);
        captured.EventType.Should().Be("api.keys.created");

        // Drain the outbox: the scheduler signs + POSTs to the sink.
        await _host.Services.GetRequiredService<WebhookDispatchScheduler>().RunDueAsync(default);

        sink.Requests.Should().HaveCount(1);
        var request = sink.Requests[0];
        request.Event.Should().Be("api.keys.created");
        // The signature is HMAC-SHA256(secret, rawBody) — verify it with the secret we were shown once.
        request.Signature.Should().Be("sha256=" + HmacHex(endpoint.SigningSecret, request.Body));
        request.Body.Should().Contain("api.keys.created");

        var delivered = await SingleDeliveryAsync(endpoint.Id);
        delivered.Status.Should().Be(WebhookDeliveryStatus.Delivered);
        delivered.LastResponseCode.Should().Be(200);
    }

    [Fact]
    public async Task A_failing_endpoint_is_retried_with_backoff()
    {
        var (client, _) = await AuthenticateOwnerAsync();
        using var sink = new WebhookSink(HttpStatusCode.InternalServerError);

        var endpoint = await CreateEndpointAsync(client, sink.Url, ["*"]);

        // Enqueue directly via the test endpoint (no cache/interceptor needed) → one Pending delivery.
        (await client.PostAsync($"/api/v1/webhooks/{endpoint.Id}/test", content: null))
            .StatusCode.Should().Be(HttpStatusCode.NoContent);

        await _host.Services.GetRequiredService<WebhookDispatchScheduler>().RunDueAsync(default);

        sink.Requests.Should().HaveCount(1); // it was attempted…
        var delivery = await SingleDeliveryAsync(endpoint.Id);
        delivery.Status.Should().Be(WebhookDeliveryStatus.Pending); // …and rescheduled, not failed
        delivery.Attempts.Should().Be(1);
        delivery.LastResponseCode.Should().Be(500);
        delivery.NextAttemptAtUtc.Should().NotBeNull();
    }

    [Fact]
    public async Task Webhooks_are_feature_gated_to_enterprise()
    {
        var client = _host.CreateClient();
        var email = $"hookfree+{Guid.NewGuid():N}@wirehq.test";
        (await client.PostAsJsonAsync("/api/v1/auth/register",
            new { email, password = Password, firstName = "Hook", lastName = "Owner", acceptTerms = true })).EnsureSuccessStatusCode();
        await _factory.VerifyEmailAsync(email);
        await Authenticate(client, email);

        var me = (await client.GetFromJsonAsync<MeDto>("/api/v1/auth/me"))!;
        await _factory.SetEditionAsync(me.ActiveOrganizationId!.Value, OrganizationEdition.Community);

        var response = await client.PostAsJsonAsync("/api/v1/webhooks",
            new { url = "https://example.com/hook", description = (string?)null, eventTypes = new[] { "*" } });
        response.StatusCode.Should().Be(HttpStatusCode.Forbidden); // plan.upgrade_required
    }

    [Fact]
    public async Task Updating_an_endpoint_that_retains_an_event_type_succeeds()
    {
        // Regression: ReplaceEventTypes used to clear+re-add the composite-keyed children, so retaining any pattern
        // threw an EF tracking conflict and every ordinary edit 500'd. The diff keeps it working.
        var (client, _) = await AuthenticateOwnerAsync();
        var endpoint = await CreateEndpointAsync(client, "https://example.com/hook", ["wg.*"]);

        var update = await client.PutAsJsonAsync($"/api/v1/webhooks/{endpoint.Id}",
            new { url = "https://example.com/hook2", description = "renamed", eventTypes = new[] { "wg.*", "identity.users.*" } });
        update.StatusCode.Should().Be(HttpStatusCode.NoContent);

        var item = (await client.GetFromJsonAsync<List<EndpointDto>>("/api/v1/webhooks"))!.Single(e => e.Id == endpoint.Id);
        item.Url.Should().Be("https://example.com/hook2");
        item.EventTypes.Should().BeEquivalentTo("wg.*", "identity.users.*");
    }

    [Fact]
    public async Task Deleting_an_endpoint_removes_its_deliveries()
    {
        var (client, _) = await AuthenticateOwnerAsync();
        var endpoint = await CreateEndpointAsync(client, "https://example.com/hook", ["*"]);

        // Enqueue a delivery, then delete the endpoint — its delivery history goes with it (soft ref, explicit cleanup).
        (await client.PostAsync($"/api/v1/webhooks/{endpoint.Id}/test", content: null)).EnsureSuccessStatusCode();
        (await CountDeliveriesAsync(endpoint.Id)).Should().Be(1);

        (await client.DeleteAsync($"/api/v1/webhooks/{endpoint.Id}")).StatusCode.Should().Be(HttpStatusCode.NoContent);

        (await CountDeliveriesAsync(endpoint.Id)).Should().Be(0);
    }

    [Fact]
    public async Task A_disabled_endpoints_pending_delivery_is_abandoned_by_the_sender()
    {
        // Regression: the sweep used to `continue` past a disabled/removed endpoint's deliveries with no state change,
        // so they stayed Pending forever at the head of every cross-tenant sweep. Now they're abandoned (terminal).
        var (client, _) = await AuthenticateOwnerAsync();
        var endpoint = await CreateEndpointAsync(client, "https://example.com/hook", ["*"]);

        (await client.PostAsync($"/api/v1/webhooks/{endpoint.Id}/test", content: null)).EnsureSuccessStatusCode();
        (await client.PostAsJsonAsync($"/api/v1/webhooks/{endpoint.Id}/status", new { enabled = false })).EnsureSuccessStatusCode();

        await _host.Services.GetRequiredService<WebhookDispatchScheduler>().RunDueAsync(default);

        var delivery = await SingleDeliveryAsync(endpoint.Id);
        delivery.Status.Should().Be(WebhookDeliveryStatus.Failed);
        delivery.NextAttemptAtUtc.Should().BeNull();
    }

    [Fact]
    public async Task A_deactivated_api_keys_entitlement_stops_queued_webhook_deliveries()
    {
        // MM-14 data-plane deactivation guard (docs/33 §5.4): the whole webhook control plane is api.keys-gated, but the
        // bypass drain used to deliver on endpoint status alone. An org that loses the entitlement — the api-extensions
        // module deactivated on self-host, or the plan downgraded on SaaS — must stop delivering already-queued events,
        // not keep POSTing them. Regression guard for the sell-loop enforcement leak.
        var (client, orgId) = await AuthenticateOwnerAsync(); // Enterprise → holds api.keys
        using var sink = new WebhookSink(HttpStatusCode.OK);
        var endpoint = await CreateEndpointAsync(client, sink.Url, ["*"]);

        // Queue a delivery while entitled…
        (await client.PostAsync($"/api/v1/webhooks/{endpoint.Id}/test", content: null)).EnsureSuccessStatusCode();

        // …then lose the entitlement (downgrade off Enterprise) before the sweep sends.
        await _factory.SetEditionAsync(orgId, OrganizationEdition.Community);

        await _host.Services.GetRequiredService<WebhookDispatchScheduler>().RunDueAsync(default);

        sink.Requests.Should().BeEmpty("a deactivated api.keys module must not deliver queued webhooks (MM-14)");
        var delivery = await SingleDeliveryAsync(endpoint.Id);
        delivery.Status.Should().Be(WebhookDeliveryStatus.Failed); // terminal — see WebhookDelivery.Cancel
        delivery.NextAttemptAtUtc.Should().BeNull();
        delivery.LastError.Should().Contain("api.keys");
    }

    private async Task<int> CountDeliveriesAsync(Guid endpointId)
    {
        using var scope = _host.Services.CreateScope();
        scope.ServiceProvider.GetRequiredService<ISettableTenantContext>().SetBypass();
        var db = scope.ServiceProvider.GetRequiredService<IApplicationDbContext>();
        return await db.WebhookDeliveries.IgnoreQueryFilters().CountAsync(d => d.EndpointId == endpointId);
    }

    private static string HmacHex(string secret, string body) =>
        Convert.ToHexStringLower(HMACSHA256.HashData(Encoding.UTF8.GetBytes(secret), Encoding.UTF8.GetBytes(body)));

    private async Task<WebhookDelivery> SingleDeliveryAsync(Guid endpointId)
    {
        using var scope = _host.Services.CreateScope();
        scope.ServiceProvider.GetRequiredService<ISettableTenantContext>().SetBypass();
        var db = scope.ServiceProvider.GetRequiredService<IApplicationDbContext>();
        return await db.WebhookDeliveries.IgnoreQueryFilters().AsNoTracking().SingleAsync(d => d.EndpointId == endpointId);
    }

    private static async Task<CreateEndpointDto> CreateEndpointAsync(HttpClient client, string url, string[] eventTypes)
    {
        var response = await client.PostAsJsonAsync("/api/v1/webhooks", new { url, description = (string?)null, eventTypes });
        response.StatusCode.Should().Be(HttpStatusCode.Created);
        return (await response.Content.ReadFromJsonAsync<CreateEndpointDto>())!;
    }

    private async Task<(HttpClient Client, Guid OrganizationId)> AuthenticateOwnerAsync()
    {
        var client = _host.CreateClient();
        var email = $"hookowner+{Guid.NewGuid():N}@wirehq.test";
        (await client.PostAsJsonAsync("/api/v1/auth/register",
            new { email, password = Password, firstName = "Hook", lastName = "Owner", acceptTerms = true })).EnsureSuccessStatusCode();
        await _factory.VerifyEmailAsync(email);
        await Authenticate(client, email);

        var me = (await client.GetFromJsonAsync<MeDto>("/api/v1/auth/me"))!;
        await _factory.SetEditionAsync(me.ActiveOrganizationId!.Value, OrganizationEdition.Enterprise);
        return (client, me.ActiveOrganizationId!.Value);
    }

    private static async Task Authenticate(HttpClient client, string email)
    {
        var login = await client.PostAsJsonAsync("/api/v1/auth/login", new { email, password = Password });
        login.EnsureSuccessStatusCode();
        var token = (await login.Content.ReadFromJsonAsync<LoginDto>())!.AccessToken;
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);
    }

    private sealed record LoginDto(string AccessToken);
    private sealed record MeDto(Guid? ActiveOrganizationId);
    private sealed record CreateEndpointDto(Guid Id, string SigningSecret);
    private sealed record EndpointDto(Guid Id, string Url, string Status, List<string> EventTypes);
}

/// <summary>A throwaway local HTTP endpoint that records the webhook deliveries it receives and returns a fixed
/// status, so the sender path (HMAC-signed POST + retry) can be exercised end-to-end against a real socket.</summary>
internal sealed class WebhookSink : IDisposable
{
    private readonly HttpListener _listener = new();
    private readonly List<ReceivedRequest> _requests = [];

    public WebhookSink(HttpStatusCode status)
    {
        var port = FreePort();
        Url = $"http://localhost:{port}/hook";
        _listener.Prefixes.Add($"http://localhost:{port}/");
        _listener.Start();
        _ = Task.Run(() => LoopAsync(status));
    }

    public string Url { get; }

    public IReadOnlyList<ReceivedRequest> Requests
    {
        get { lock (_requests) { return _requests.ToList(); } }
    }

    private async Task LoopAsync(HttpStatusCode status)
    {
        while (_listener.IsListening)
        {
            HttpListenerContext context;
            try
            {
                context = await _listener.GetContextAsync();
            }
            catch
            {
                break; // listener stopped
            }

            using (var reader = new StreamReader(context.Request.InputStream, Encoding.UTF8))
            {
                var body = await reader.ReadToEndAsync();
                lock (_requests)
                {
                    _requests.Add(new ReceivedRequest(
                        body,
                        context.Request.Headers["X-WireHQ-Signature"],
                        context.Request.Headers["X-WireHQ-Event"]));
                }
            }

            context.Response.StatusCode = (int)status;
            context.Response.Close();
        }
    }

    private static int FreePort()
    {
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        listener.Stop();
        return port;
    }

    public void Dispose()
    {
        try { _listener.Stop(); } catch { /* already stopped */ }
        _listener.Close();
    }

    internal sealed record ReceivedRequest(string Body, string? Signature, string? Event);
}
