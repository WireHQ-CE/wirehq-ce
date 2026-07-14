using FluentAssertions;
using WireHQ.Domain.ApiKeys;
using Xunit;

namespace WireHQ.Domain.UnitTests.ApiKeys;

public sealed class ApiKeyTests
{
    private static readonly Guid OrgId = Guid.CreateVersion7();

    private static ApiKey NewKey(IReadOnlyCollection<string>? scopes = null, DateTimeOffset? expiresAtUtc = null)
    {
        var gen = ApiKeyToken.Generate();
        return ApiKey.Create(OrgId, "CI", gen.DisplayPrefix, gen.Hash, scopes ?? ["identity.users.read"], Guid.CreateVersion7(), expiresAtUtc).Value;
    }

    [Fact]
    public void Create_requires_a_name_and_at_least_one_scope()
    {
        var gen = ApiKeyToken.Generate();
        ApiKey.Create(OrgId, " ", gen.DisplayPrefix, gen.Hash, ["a"], null, null).Error.Should().Be(ApiKeyErrors.InvalidName);
        ApiKey.Create(OrgId, "ok", gen.DisplayPrefix, gen.Hash, [], null, null).Error.Should().Be(ApiKeyErrors.NoScopes);
    }

    [Fact]
    public void Create_stores_deduplicated_scopes_and_starts_active()
    {
        var key = NewKey(["identity.users.read", "identity.users.read", "identity.teams.read"]);

        key.Status.Should().Be(ApiKeyStatus.Active);
        key.Scopes.Select(s => s.PermissionKey).Should().BeEquivalentTo(["identity.users.read", "identity.teams.read"]);
    }

    [Fact]
    public void IsUsable_reflects_revocation_and_expiry()
    {
        var now = DateTimeOffset.UtcNow;

        NewKey().IsUsable(now).Should().BeTrue();
        NewKey(expiresAtUtc: now.AddMinutes(-1)).IsUsable(now).Should().BeFalse("it has expired");

        var revoked = NewKey();
        revoked.Revoke();
        revoked.IsUsable(now).Should().BeFalse("it was revoked");
    }

    [Fact]
    public void The_token_hashes_deterministically_and_carries_the_prefix()
    {
        var gen = ApiKeyToken.Generate();

        gen.Plaintext.Should().StartWith("whq_");
        gen.DisplayPrefix.Should().Be(gen.Plaintext[..12]);
        ApiKeyToken.Hash(gen.Plaintext).Should().Be(gen.Hash);
        ApiKeyToken.LooksLikeApiKey(gen.Plaintext).Should().BeTrue();
        ApiKeyToken.LooksLikeApiKey("Bearer something").Should().BeFalse();
    }
}
