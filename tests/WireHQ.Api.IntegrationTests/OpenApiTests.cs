using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using FluentAssertions;
using Microsoft.AspNetCore.Hosting;
using Xunit;

namespace WireHQ.Api.IntegrationTests;

/// <summary>
/// The production OpenAPI document (docs/27-openapi-reference.md, ADR-044), served at
/// <c>/api/docs/openapi/{documentName}.json</c>. Auth-gated by default in EVERY environment (the suite's
/// Development host included — O-2: the environment name alone never weakens auth): a JWT session or an API key
/// both read it via the smart scheme. <c>OpenApi:Enabled</c> is the kill switch; anonymous needs Development
/// AND an explicit <c>OpenApi:AllowAnonymous</c> held in no appsettings file (which is exactly why
/// <c>UseSetting</c> is reliable for it here). Content checks pin what a consumer relies on: both security
/// schemes, versioned + module paths in, machine-only surfaces out, maintainer doc-references scrubbed.
/// </summary>
[Collection(ApiCollection.Name)]
public sealed class OpenApiTests
{
    private const string SpecUrl = "/api/docs/openapi/v1.json";
    private const string Password = "Sup3rSecret-Docs!1";

    private readonly WireHqApiFactory _factory;

    public OpenApiTests(WireHqApiFactory factory) => _factory = factory;

    [Fact]
    public async Task Anonymous_is_rejected_by_default()
    {
        (await _factory.CreateClient().GetAsync(SpecUrl)).StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }

    [Fact]
    public async Task A_session_reads_the_spec_and_it_documents_the_api_honestly()
    {
        var (client, _) = await AuthenticateOwnerAsync();

        var response = await client.GetAsync(SpecUrl);
        response.StatusCode.Should().Be(HttpStatusCode.OK);
        response.Headers.CacheControl!.Private.Should().BeTrue();

        var json = await response.Content.ReadAsStringAsync();
        using var spec = JsonDocument.Parse(json);
        var root = spec.RootElement;

        root.GetProperty("info").GetProperty("title").GetString().Should().Be("WireHQ API");

        // Both production auth schemes are advertised (O-5) — a key holder can see how to authenticate.
        var schemes = root.GetProperty("components").GetProperty("securitySchemes");
        schemes.TryGetProperty("Bearer", out _).Should().BeTrue();
        schemes.TryGetProperty("ApiKey", out var apiKey).Should().BeTrue();
        apiKey.GetProperty("name").GetString().Should().Be("X-Api-Key");
        apiKey.GetProperty("in").GetString().Should().Be("header");

        var paths = root.GetProperty("paths");

        // Versioned controllers land via the API explorer; null-group module minimal-APIs land via their
        // /api/v1/ path prefix (O-5's DocInclusionPredicate).
        paths.TryGetProperty("/api/v1/api-keys", out _).Should().BeTrue();
        paths.TryGetProperty("/api/v1/wireguard/fleet", out _).Should().BeTrue();

        // Machine-only surfaces stay out of the customer document: the agent mTLS data plane, the health
        // probes, and the docs endpoint itself (ExcludeFromDescription).
        foreach (var path in paths.EnumerateObject())
        {
            path.Name.Should().NotStartWith("/agent/");
            path.Name.Should().NotStartWith("/health");
            path.Name.Should().NotStartWith("/api/docs/");
        }

        // XML doc comments reach the document — the operation summary comes from the controller's ///.
        paths.GetProperty("/api/v1/api-keys/scopes").GetProperty("get")
            .GetProperty("summary").GetString().Should().Contain("grantable-scope catalog");

        // …and NO maintainer doc-reference (docs/… or ADR-…) survives anywhere in the customer text (O-5's
        // filter) — the only legal "docs" occurrences are the /api/docs path keys, which are excluded above.
        json.Should().NotContain("docs/");
        json.Should().NotContain("ADR-");
    }

    [Fact]
    public async Task A_wrong_case_document_name_404s_without_poisoning_the_real_document()
    {
        // Regression (diff review): the cache used to be case-insensitive over a case-sensitive generator, so
        // one V1.json request negatively-cached "V1" and then answered every v1.json with that 404. The name is
        // now allow-listed case-sensitively — the miss is clean and the real document is untouched.
        var (client, _) = await AuthenticateOwnerAsync();

        (await client.GetAsync("/api/docs/openapi/V1.json")).StatusCode.Should().Be(HttpStatusCode.NotFound);
        (await client.GetAsync(SpecUrl)).StatusCode.Should().Be(HttpStatusCode.OK);
    }

    [Fact]
    public async Task An_api_key_reads_the_spec_with_the_key_itself()
    {
        // The audience for the reference is key holders — the smart scheme lets the key fetch the spec (O-2).
        var (owner, _) = await AuthenticateOwnerAsync();
        var create = await owner.PostAsJsonAsync("/api/v1/api-keys",
            new { name = "docs reader", scopes = new[] { "identity.users.read" }, expiresAtUtc = (DateTimeOffset?)null });
        create.StatusCode.Should().Be(HttpStatusCode.Created);
        var key = (await create.Content.ReadFromJsonAsync<CreateDto>())!.Key;

        var keyed = _factory.CreateClient();
        keyed.DefaultRequestHeaders.Add("X-Api-Key", key);

        (await keyed.GetAsync(SpecUrl)).StatusCode.Should().Be(HttpStatusCode.OK);
    }

    [Fact]
    public async Task An_unknown_document_is_a_404_even_authenticated()
    {
        var (client, _) = await AuthenticateOwnerAsync();
        (await client.GetAsync("/api/docs/openapi/v99.json")).StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task The_endpoint_disappears_when_disabled()
    {
        using var disabled = _factory.WithWebHostBuilder(builder => builder.UseSetting("OpenApi:Enabled", "false"));
        (await disabled.CreateClient().GetAsync(SpecUrl)).StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task Anonymous_access_is_an_explicit_development_opt_in()
    {
        // The local-dev convenience (the commented-out compose line): Development env + the explicit flag.
        using var anonymous = _factory.WithWebHostBuilder(builder => builder.UseSetting("OpenApi:AllowAnonymous", "true"));
        (await anonymous.CreateClient().GetAsync(SpecUrl)).StatusCode.Should().Be(HttpStatusCode.OK);
    }

    private async Task<(HttpClient Client, Guid OrganizationId)> AuthenticateOwnerAsync()
    {
        var client = _factory.CreateClient();
        var email = $"docsowner+{Guid.NewGuid():N}@wirehq.test";
        (await client.PostAsJsonAsync("/api/v1/auth/register",
            new { email, password = Password, firstName = "Docs", lastName = "Owner", acceptTerms = true })).EnsureSuccessStatusCode();
        await _factory.VerifyEmailAsync(email);

        var login = await client.PostAsJsonAsync("/api/v1/auth/login", new { email, password = Password });
        login.EnsureSuccessStatusCode();
        var token = (await login.Content.ReadFromJsonAsync<LoginDto>())!.AccessToken;
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);

        var me = (await client.GetFromJsonAsync<MeDto>("/api/v1/auth/me"))!;
        return (client, me.ActiveOrganizationId!.Value);
    }

    private sealed record LoginDto(string AccessToken);
    private sealed record MeDto(Guid? ActiveOrganizationId);
    private sealed record CreateDto(Guid Id, string Key);
}
