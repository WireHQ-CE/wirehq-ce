using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using FluentAssertions;
using Xunit;

namespace WireHQ.Api.IntegrationTests;

/// <summary>
/// Profile enrichment: the extended profile fields round-trip through /me, usernames are unique, and the
/// avatar upload → public-serve → remove cycle works (bytes stored in the DB, served anonymously).
/// </summary>
[Collection(ApiCollection.Name)]
public sealed class ProfileTests(WireHqApiFactory factory)
{
    private readonly WireHqApiFactory _factory = factory;

    [Fact]
    public async Task Profile_fields_round_trip_through_me()
    {
        var client = _factory.CreateClient();
        var email = Unique("profile");
        await RegisterAsync(client, email, "Grace", "Hopper");
        Authorize(client, await LoginAsync(client, email));

        var patch = await client.PatchAsJsonAsync("/api/v1/account/profile", new
        {
            firstName = "Grace",
            lastName = "Hopper",
            username = $"grace_{Guid.NewGuid():N}".Substring(0, 16),
            jobTitle = "Rear Admiral",
            phone = "+1 555 0100",
            timezone = "America/New_York",
            language = "en",
        });
        patch.StatusCode.Should().Be(HttpStatusCode.NoContent);

        var me = (await client.GetFromJsonAsync<MeBody>("/api/v1/auth/me"))!;
        me.JobTitle.Should().Be("Rear Admiral");
        me.Phone.Should().Be("+1 555 0100");
        me.Timezone.Should().Be("America/New_York");
        me.Language.Should().Be("en");
        me.Username.Should().NotBeNullOrWhiteSpace();
    }

    [Fact]
    public async Task Username_must_be_unique()
    {
        var a = _factory.CreateClient();
        var emailA = Unique("user-a");
        await RegisterAsync(a, emailA, "Alice", "A");
        Authorize(a, await LoginAsync(a, emailA));

        var username = $"taken_{Guid.NewGuid():N}".Substring(0, 16);
        (await a.PatchAsJsonAsync("/api/v1/account/profile", new { firstName = "Alice", lastName = "A", username }))
            .StatusCode.Should().Be(HttpStatusCode.NoContent);

        var b = _factory.CreateClient();
        var emailB = Unique("user-b");
        await RegisterAsync(b, emailB, "Bob", "B");
        Authorize(b, await LoginAsync(b, emailB));

        var clash = await b.PatchAsJsonAsync("/api/v1/account/profile", new { firstName = "Bob", lastName = "B", username });
        clash.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        var problem = (await clash.Content.ReadFromJsonAsync<ProblemWithErrors>())!;
        problem.Errors.Should().ContainKey("username");
    }

    [Fact]
    public async Task Avatar_can_be_uploaded_served_publicly_and_removed()
    {
        var client = _factory.CreateClient();
        var email = Unique("avatar");
        await RegisterAsync(client, email, "Ada", "Lovelace");
        Authorize(client, await LoginAsync(client, email));

        var userId = (await client.GetFromJsonAsync<MeBody>("/api/v1/auth/me"))!.UserId;

        // No avatar yet → public endpoint 404s.
        var anon = _factory.CreateClient();
        (await anon.GetAsync($"/api/v1/avatars/{userId}")).StatusCode.Should().Be(HttpStatusCode.NotFound);

        // Upload a (tiny) PNG via multipart.
        var bytes = new byte[] { 0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A, 1, 2, 3, 4, 5 };
        using var form = new MultipartFormDataContent();
        var file = new ByteArrayContent(bytes);
        file.Headers.ContentType = new MediaTypeHeaderValue("image/png");
        form.Add(file, "file", "avatar.png");
        (await client.PostAsync("/api/v1/account/avatar", form)).StatusCode.Should().Be(HttpStatusCode.NoContent);

        // /me now exposes an avatar URL…
        (await client.GetFromJsonAsync<MeBody>("/api/v1/auth/me"))!.AvatarUrl.Should().NotBeNullOrWhiteSpace();

        // …and the public endpoint serves the bytes.
        var served = await anon.GetAsync($"/api/v1/avatars/{userId}");
        served.StatusCode.Should().Be(HttpStatusCode.OK);
        served.Content.Headers.ContentType!.MediaType.Should().Be("image/png");
        (await served.Content.ReadAsByteArrayAsync()).Should().Equal(bytes);

        // Remove → gone again.
        (await client.DeleteAsync("/api/v1/account/avatar")).StatusCode.Should().Be(HttpStatusCode.NoContent);
        (await client.GetFromJsonAsync<MeBody>("/api/v1/auth/me"))!.AvatarUrl.Should().BeNull();
        (await anon.GetAsync($"/api/v1/avatars/{userId}")).StatusCode.Should().Be(HttpStatusCode.NotFound);
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

    private static void Authorize(HttpClient client, string token) =>
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);

    private sealed record TokenBody(string AccessToken, int ExpiresIn);
    private sealed record MeBody(Guid UserId, string? Username, string? JobTitle, string? Phone, string? Timezone, string? Language, string? AvatarUrl);
    private sealed record ProblemWithErrors(Dictionary<string, string[]> Errors);
}
