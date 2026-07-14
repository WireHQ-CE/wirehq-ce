using System.Diagnostics;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using FluentAssertions;
using Xunit;

namespace WireHQ.Api.IntegrationTests;

/// <summary>
/// Exercises the Phase-0 deployment-job engine end-to-end against a real Postgres + the running
/// background dispatcher: requesting a deployment enqueues a job, the hosted dispatcher claims and
/// drives it through its lifecycle, and for the config-only Local provider it reaches Succeeded as a
/// no-op — with a recorded timeline. (docs/12-remote-orchestration.md §4)
/// </summary>
[Collection(ApiCollection.Name)]
public sealed class OrchestrationDeploymentTests(WireHqApiFactory factory)
{
    private readonly WireHqApiFactory _factory = factory;

    [Fact]
    public async Task Requesting_a_deployment_enqueues_a_job_the_dispatcher_drives_to_succeeded()
    {
        var client = _factory.CreateClient();
        await AuthenticateAsOwnerAsync(client);

        var networkId = await CreatedId(client.PostAsJsonAsync("/api/v1/wireguard/networks",
            new { name = "Deploy", cidr = "10.80.0.0/24" }));
        var instanceId = await CreatedId(client.PostAsJsonAsync("/api/v1/wireguard/instances",
            new { networkId, name = "DeployGW", listenPort = 51880, interfaceAddress = "10.80.0.1/24" }));

        // Enqueue a deployment — accepted (async), starts Pending.
        var queued = await client.PostAsync($"/api/v1/wireguard/instances/{instanceId}/deploy", content: null);
        queued.StatusCode.Should().Be(HttpStatusCode.Accepted);
        var job = (await queued.Content.ReadFromJsonAsync<RequestDeploymentResponse>())!;
        job.JobId.Should().NotBeEmpty();
        job.Status.Should().Be("Pending");

        // The background dispatcher claims and completes it (config-only Local → no-op success).
        var detail = await PollUntilTerminalAsync(client, job.JobId);
        detail.Status.Should().Be("Succeeded");
        detail.InstanceId.Should().Be(instanceId);
        detail.Attempts.Should().Be(1);
        detail.Error.Should().BeNull();
        detail.CompletedAtUtc.Should().NotBeNull();

        // The full timeline was recorded.
        detail.Events.Select(e => e.Phase).Should().ContainInOrder("queued", "dispatched", "applying", "succeeded");
        detail.Events.Single(e => e.Phase == "succeeded").Detail.Should().Contain("Config-only");

        // It shows in the instance's deployment history.
        var history = await GetJson<List<DeploymentSummary>>(client, $"/api/v1/wireguard/instances/{instanceId}/deployments");
        history.Should().ContainSingle(d => d.Id == job.JobId).Which.Status.Should().Be("Succeeded");
    }

    [Fact]
    public async Task Deploying_a_missing_instance_is_not_found()
    {
        var client = _factory.CreateClient();
        await AuthenticateAsOwnerAsync(client);

        var response = await client.PostAsync($"/api/v1/wireguard/instances/{Guid.NewGuid()}/deploy", content: null);
        response.StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    private static async Task<DeploymentDetail> PollUntilTerminalAsync(HttpClient client, Guid jobId)
    {
        var sw = Stopwatch.StartNew();
        while (sw.Elapsed < TimeSpan.FromSeconds(30))
        {
            var detail = await client.GetFromJsonAsync<DeploymentDetail>($"/api/v1/wireguard/deployments/{jobId}");
            if (detail is not null && detail.Status is "Succeeded" or "Failed" or "RolledBack")
            {
                return detail;
            }

            await Task.Delay(500);
        }

        throw new TimeoutException($"Deployment {jobId} did not reach a terminal state in time.");
    }

    private static async Task<T> GetJson<T>(HttpClient client, string url)
    {
        var value = await client.GetFromJsonAsync<T>(url);
        value.Should().NotBeNull();
        return value!;
    }

    private static async Task<Guid> CreatedId(Task<HttpResponseMessage> request)
    {
        var response = await request;
        response.StatusCode.Should().Be(HttpStatusCode.Created);
        return (await response.Content.ReadFromJsonAsync<IdResponse>())!.Id;
    }

    private async Task AuthenticateAsOwnerAsync(HttpClient client)
    {
        var unique = Guid.NewGuid().ToString("N");
        var email = $"owner+{unique}@wirehq.test";
        const string password = "Sup3rSecret!!";

        var register = await client.PostAsJsonAsync("/api/v1/auth/register",
            new { email, password, firstName = "Orch Owner", lastName = "Test", acceptTerms = true });
        register.StatusCode.Should().Be(HttpStatusCode.Created);

        await _factory.VerifyEmailAsync(email);

        var login = await client.PostAsJsonAsync("/api/v1/auth/login", new { email, password });
        login.StatusCode.Should().Be(HttpStatusCode.OK);
        var token = (await login.Content.ReadFromJsonAsync<LoginResponse>())!.AccessToken;

        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);
    }

    private sealed record IdResponse(Guid Id);
    private sealed record LoginResponse(string AccessToken);
    private sealed record RequestDeploymentResponse(Guid JobId, string Status);
    private sealed record DeploymentEventItem(string Phase, string? Detail, DateTimeOffset AtUtc);
    private sealed record DeploymentSummary(Guid Id, string Type, string Status, int Attempts, DateTimeOffset CreatedAtUtc, DateTimeOffset? CompletedAtUtc, string? Error);
    private sealed record DeploymentDetail(
        Guid Id, Guid InstanceId, string Type, string Status, int Attempts, int? DesiredConfigVersion,
        string? Error, DateTimeOffset CreatedAtUtc, DateTimeOffset? DispatchedAtUtc, DateTimeOffset? CompletedAtUtc,
        List<DeploymentEventItem> Events);
}
