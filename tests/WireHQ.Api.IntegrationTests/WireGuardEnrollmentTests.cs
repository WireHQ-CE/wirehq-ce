using System.IO.Compression;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text;
using FluentAssertions;
using Xunit;

namespace WireHQ.Api.IntegrationTests;

/// <summary>
/// Exercises CSV bulk enrollment end-to-end against a real Postgres: the dry-run preview classifies
/// each row (Create/Skip/Error), execute creates exactly the valid, non-duplicate peers in one batch,
/// the batch is queryable, and the package endpoint streams a ZIP of the generated configs.
/// </summary>
[Collection(ApiCollection.Name)]
public sealed class WireGuardEnrollmentTests(WireHqApiFactory factory)
{
    private readonly WireHqApiFactory _factory = factory;

    // Two valid rows, one duplicate email (skip), one invalid email (error).
    private const string Csv =
        "Name,Email,Department,DeviceType\n" +
        "Ada Lovelace,ada@acme.test,Engineering,Laptop\n" +
        "Grace Hopper,grace@acme.test,Research,Mobile\n" +
        "Ada Again,ada@acme.test,Engineering,Laptop\n" +
        "Broken,not-an-email,Ops,Server\n";

    [Fact]
    public async Task Validate_then_execute_creates_valid_peers_and_packages_their_configs()
    {
        var client = _factory.CreateClient();
        await AuthenticateAsOwnerAsync(client);

        var networkId = await CreatedId(client.PostAsJsonAsync("/api/v1/wireguard/networks",
            new { name = "Enroll", cidr = "10.70.0.0/24", dns = new[] { "1.1.1.1" } }));
        var instanceId = await CreatedId(client.PostAsJsonAsync("/api/v1/wireguard/instances",
            new { networkId, name = "EnrollGW", listenPort = 51870, interfaceAddress = "10.70.0.1/24", endpointHost = "vpn.acme.test:51870" }));

        // ---- Preview (no writes) ----
        var preview = await PostCsv<PreviewDto>(client, $"/api/v1/wireguard/instances/{instanceId}/enrollments/validate");
        preview.TotalRows.Should().Be(4);
        preview.CreateRows.Should().Be(2);
        preview.SkipRows.Should().Be(1);   // duplicate email
        preview.ErrorRows.Should().Be(1);  // invalid email
        preview.Rows.Single(r => r.Email == "not-an-email").Outcome.Should().Be("Error");
        preview.Rows.Count(r => r.Outcome == "Create").Should().Be(2);

        // Nothing was created by the dry-run.
        (await GetPeers(client, instanceId)).Should().BeEmpty();

        // ---- Execute ----
        var result = await PostCsv<ResultDto>(client, $"/api/v1/wireguard/instances/{instanceId}/enrollments/execute");
        result.Created.Should().Be(2);
        result.Skipped.Should().Be(1);
        result.Failed.Should().Be(1);
        result.BatchId.Should().NotBeEmpty();

        var peers = await GetPeers(client, instanceId);
        peers.Should().HaveCount(2);
        peers.Select(p => p.AssignedAddress).Should().OnlyContain(a => a.StartsWith("10.70.0."));
        peers.Select(p => p.AssignedAddress).Distinct().Should().HaveCount(2);

        // ---- Batch is queryable ----
        var batch = await GetJson<BatchDto>(client, $"/api/v1/wireguard/enrollments/{result.BatchId}");
        batch.Status.Should().Be("Completed");
        batch.TotalRows.Should().Be(4);
        batch.ValidRows.Should().Be(2);

        // ---- Package: a real ZIP with a .conf per peer + a manifest ----
        var package = await client.GetAsync($"/api/v1/wireguard/enrollments/{result.BatchId}/package");
        package.StatusCode.Should().Be(HttpStatusCode.OK);
        package.Content.Headers.ContentType!.MediaType.Should().Be("application/zip");

        await using var zipStream = await package.Content.ReadAsStreamAsync();
        using var archive = new ZipArchive(zipStream, ZipArchiveMode.Read);
        archive.Entries.Count(e => e.FullName.EndsWith(".conf")).Should().Be(2);
        archive.Entries.Should().Contain(e => e.FullName == "manifest.csv");

        // A .conf in the package is a real wg-quick config.
        var conf = archive.Entries.First(e => e.FullName.EndsWith(".conf"));
        await using var entry = conf.Open();
        using var sr = new StreamReader(entry);
        (await sr.ReadToEndAsync()).Should().Contain("[Interface]").And.Contain("PrivateKey = ");
    }

    [Fact]
    public async Task Validate_rejects_a_csv_missing_required_columns()
    {
        var client = _factory.CreateClient();
        await AuthenticateAsOwnerAsync(client);

        var networkId = await CreatedId(client.PostAsJsonAsync("/api/v1/wireguard/networks",
            new { name = "BadCsv", cidr = "10.72.0.0/24" }));
        var instanceId = await CreatedId(client.PostAsJsonAsync("/api/v1/wireguard/instances",
            new { networkId, name = "BadCsvGW", listenPort = 51872, interfaceAddress = "10.72.0.1/24" }));

        var response = await PostCsvRaw(client, $"/api/v1/wireguard/instances/{instanceId}/enrollments/validate", "FullName,Mail\nx,y\n");
        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }

    private static async Task<T> PostCsv<T>(HttpClient client, string url)
    {
        var response = await PostCsvRaw(client, url, Csv);
        response.StatusCode.Should().Be(HttpStatusCode.OK);
        return (await response.Content.ReadFromJsonAsync<T>())!;
    }

    private static async Task<HttpResponseMessage> PostCsvRaw(HttpClient client, string url, string csv)
    {
        using var form = new MultipartFormDataContent();
        var file = new ByteArrayContent(Encoding.UTF8.GetBytes(csv));
        file.Headers.ContentType = new MediaTypeHeaderValue("text/csv");
        form.Add(file, "file", "enroll.csv");
        return await client.PostAsync(url, form);
    }

    private static async Task<List<PeerDto>> GetPeers(HttpClient client, Guid instanceId) =>
        (await client.GetFromJsonAsync<List<PeerDto>>($"/api/v1/wireguard/instances/{instanceId}/peers"))!;

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
            new { email, password, firstName = "WG Owner", lastName = "Test", acceptTerms = true });
        register.StatusCode.Should().Be(HttpStatusCode.Created);

        await _factory.VerifyEmailAsync(email);

        var login = await client.PostAsJsonAsync("/api/v1/auth/login", new { email, password });
        login.StatusCode.Should().Be(HttpStatusCode.OK);
        var token = (await login.Content.ReadFromJsonAsync<LoginResponse>())!.AccessToken;

        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);
    }

    private sealed record IdResponse(Guid Id);
    private sealed record LoginResponse(string AccessToken);
    private sealed record PeerDto(Guid Id, string Name, string? Email, string AssignedAddress);
    private sealed record PreviewRowDto(int RowNumber, string? Name, string? Email, string? AssignedAddress, string Outcome, string? Reason);
    private sealed record PreviewDto(int TotalRows, int CreateRows, int SkipRows, int ErrorRows, List<PreviewRowDto> Rows);
    private sealed record ResultDto(Guid BatchId, int TotalRows, int Created, int Skipped, int Failed);
    private sealed record BatchDto(Guid Id, string SourceFilename, string Status, int TotalRows, int ValidRows, int ErrorRows);
}
