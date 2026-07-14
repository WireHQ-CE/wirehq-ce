using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using WireHQ.Application.Abstractions.Persistence;
using WireHQ.Modules.Orchestration.Domain;
using Xunit;

namespace WireHQ.Api.IntegrationTests;

/// <summary>
/// The correlation spine reaches background work (ADR-030): the request that enqueues a deployment job
/// stamps its correlation id onto the job row, so the dispatcher's execution logs chain back to the
/// originating request (fixes the G-21 context loss in background services).
/// </summary>
[Collection(ApiCollection.Name)]
public sealed class JobCorrelationTests(WireHqApiFactory factory)
{
    private readonly WireHqApiFactory _factory = factory;

    [Fact]
    public async Task A_deployment_job_records_the_enqueuing_requests_correlation_id()
    {
        var client = _factory.CreateClient();
        await AuthenticateAsOwnerAsync(client);

        var networkId = await CreatedId(client.PostAsJsonAsync("/api/v1/wireguard/networks",
            new { name = "Corr Net", cidr = "10.40.0.0/24", dns = new[] { "1.1.1.1" } }));
        var instanceId = await CreatedId(client.PostAsJsonAsync("/api/v1/wireguard/instances",
            new { networkId, name = "GW", listenPort = 51860, interfaceAddress = "10.40.0.1/24", endpointHost = "vpn.example.com:51860" }));

        var deploy = await client.PostAsync($"/api/v1/wireguard/instances/{instanceId}/deploy", content: null);
        deploy.StatusCode.Should().Be(HttpStatusCode.Accepted);
        var correlationId = deploy.Headers.GetValues("X-Correlation-Id").Single();
        var jobId = (await deploy.Content.ReadFromJsonAsync<DeployResponse>())!.JobId;

        using var scope = _factory.CreateBypassScope();
        var db = scope.ServiceProvider.GetRequiredService<IApplicationDbContext>();
        // Bypass handles RLS (L2); IgnoreQueryFilters opts out of the EF tenant filter (L1), since this
        // scope has no active org. (ADR-024/ADR-027)
        var job = await db.Set<DeploymentJob>().IgnoreQueryFilters().FirstOrDefaultAsync(j => j.Id == jobId);

        job.Should().NotBeNull();
        job!.CorrelationId.Should().NotBeNullOrEmpty().And.Be(correlationId);
    }

    private static async Task<Guid> CreatedId(Task<HttpResponseMessage> send)
    {
        var response = await send;
        response.StatusCode.Should().Be(HttpStatusCode.Created);
        return (await response.Content.ReadFromJsonAsync<IdResponse>())!.Id;
    }

    private async Task AuthenticateAsOwnerAsync(HttpClient client)
    {
        var email = $"owner+{Guid.NewGuid():N}@wirehq.test";
        const string password = "Sup3rSecret!!";

        var register = await client.PostAsJsonAsync("/api/v1/auth/register",
            new { email, password, firstName = "Job", lastName = "Corr", acceptTerms = true });
        register.StatusCode.Should().Be(HttpStatusCode.Created);

        // Creating instances is gated by verified email (VerifiedEmailBehavior).
        await _factory.VerifyEmailAsync(email);

        var login = await client.PostAsJsonAsync("/api/v1/auth/login", new { email, password });
        var token = (await login.Content.ReadFromJsonAsync<LoginResponse>())!.AccessToken;
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);
    }

    private sealed record IdResponse(Guid Id);
    private sealed record DeployResponse(Guid JobId, string Status);
    private sealed record LoginResponse(string AccessToken);
}
