using FluentAssertions;
using WireHQ.Domain.Auditing;
using WireHQ.Infrastructure.Auditing;
using Xunit;

namespace WireHQ.Api.IntegrationTests;

/// <summary>
/// Pure unit coverage for the audit hash-chain primitives (ADR-031, docs/15 §5) — no database. Proves the
/// hashing is deterministic and chained, that the advisory-lock key is stable per tenant, and that the
/// verifier catches every flavour of tamper: a mutated row, a removed/reordered row, and an unchained row.
/// </summary>
public sealed class AuditChainUnitTests
{
    private static AuditLog Entry(Guid? org, string action, DateTimeOffset at) =>
        AuditLog.Record(action, AuditOutcome.Success, organizationId: org, occurredAtUtc: at);

    private static List<AuditLog> BuildChain(params AuditLog[] entries)
    {
        byte[]? prev = null;
        foreach (var entry in entries)
        {
            var hash = AuditChain.ComputeEntryHash(prev, entry);
            entry.ApplyChain(prev, hash);
            prev = hash;
        }

        return [.. entries];
    }

    [Fact]
    public void Entry_hash_is_deterministic_and_depends_on_the_predecessor()
    {
        var entry = Entry(Guid.NewGuid(), "wg.network.created", DateTimeOffset.UtcNow);
        var prevA = new byte[32];
        var prevB = Enumerable.Repeat((byte)1, 32).ToArray();

        AuditChain.ComputeEntryHash(prevA, entry).Should().Equal(AuditChain.ComputeEntryHash(prevA, entry));
        AuditChain.ComputeEntryHash(prevA, entry).Should().NotEqual(AuditChain.ComputeEntryHash(prevB, entry));
        AuditChain.ComputeEntryHash(null, entry).Should().HaveCount(32); // SHA-256
    }

    [Fact]
    public void Lock_key_is_stable_per_tenant_and_distinct_across_tenants_and_the_platform_chain()
    {
        var orgA = Guid.NewGuid();
        var orgB = Guid.NewGuid();

        AuditChain.LockKey(orgA).Should().Be(AuditChain.LockKey(orgA));
        AuditChain.LockKey(orgA).Should().NotBe(AuditChain.LockKey(orgB));
        AuditChain.LockKey(null).Should().NotBe(AuditChain.LockKey(orgA)); // the platform chain is its own
    }

    [Fact]
    public void A_correctly_built_chain_verifies_intact()
    {
        var org = Guid.NewGuid();
        var now = DateTimeOffset.UtcNow;
        var chain = BuildChain(
            Entry(org, "a", now),
            Entry(org, "b", now.AddSeconds(1)),
            Entry(org, "c", now.AddSeconds(2)));

        var result = AuditChainVerifier.Verify(chain);

        result.IsIntact.Should().BeTrue();
        result.VerifiedCount.Should().Be(3);
        chain[0].PrevHash.Should().BeNull("the genesis entry has no predecessor");
    }

    [Fact]
    public void An_unchained_row_is_reported_as_broken()
    {
        var org = Guid.NewGuid();
        var now = DateTimeOffset.UtcNow;
        var chain = BuildChain(Entry(org, "a", now), Entry(org, "b", now.AddSeconds(1)));
        var orphan = Entry(org, "c", now.AddSeconds(2)); // never chained → entry_hash null

        var result = AuditChainVerifier.Verify([.. chain, orphan]);

        result.IsIntact.Should().BeFalse();
        result.VerifiedCount.Should().Be(2);
        result.BrokenAtEntryId.Should().Be(orphan.Id);
    }

    [Fact]
    public void Reordering_rows_breaks_the_prev_hash_linkage()
    {
        var org = Guid.NewGuid();
        var now = DateTimeOffset.UtcNow;
        var chain = BuildChain(
            Entry(org, "a", now),
            Entry(org, "b", now.AddSeconds(1)),
            Entry(org, "c", now.AddSeconds(2)));

        // Swap the middle two — each still carries a valid self-hash, but the links no longer line up.
        var reordered = new List<AuditLog> { chain[0], chain[2], chain[1] };

        var result = AuditChainVerifier.Verify(reordered);

        result.IsIntact.Should().BeFalse();
        result.BrokenAtEntryId.Should().Be(chain[2].Id);
    }

    [Fact]
    public void A_retained_away_history_verifies_when_the_boundary_is_anchored()
    {
        var org = Guid.NewGuid();
        var now = DateTimeOffset.UtcNow;
        var chain = BuildChain(
            Entry(org, "a", now),
            Entry(org, "b", now.AddSeconds(1)),
            Entry(org, "c", now.AddSeconds(2)));

        // Simulate retention dropping the first entry: the surviving run now starts mid-chain. With the
        // dropped entry's hash recorded as a boundary anchor, the survivors still verify.
        var survivors = new List<AuditLog> { chain[1], chain[2] };
        var boundary = new HashSet<string> { Convert.ToHexString(chain[0].EntryHash!) };

        var result = AuditChainVerifier.Verify(survivors, boundary);

        result.IsIntact.Should().BeTrue();
        result.VerifiedCount.Should().Be(2);
    }

    [Fact]
    public void A_deletion_without_a_matching_anchor_is_detected_as_tampering()
    {
        var org = Guid.NewGuid();
        var now = DateTimeOffset.UtcNow;
        var chain = BuildChain(
            Entry(org, "a", now),
            Entry(org, "b", now.AddSeconds(1)),
            Entry(org, "c", now.AddSeconds(2)));

        // The first entry is gone but no anchor explains it — the survivors must read as tampered.
        var survivors = new List<AuditLog> { chain[1], chain[2] };

        var result = AuditChainVerifier.Verify(survivors, validBoundaryHashes: new HashSet<string>());

        result.IsIntact.Should().BeFalse();
        result.BrokenAtEntryId.Should().Be(chain[1].Id);
    }

    [Fact]
    public void A_mutated_row_whose_content_no_longer_matches_its_hash_is_detected()
    {
        var org = Guid.NewGuid();
        var now = DateTimeOffset.UtcNow;
        var first = Entry(org, "a", now);
        var second = Entry(org, "b", now.AddSeconds(1));
        BuildChain(first, second);

        // Keep the linkage (same prev_hash) but stamp a hash computed from different content — i.e. the row
        // was altered after it was hashed. The genesis prev is null.
        var forged = Entry(org, "a-altered", now);
        first.ApplyChain(null, AuditChain.ComputeEntryHash(null, forged));

        var result = AuditChainVerifier.Verify([first, second]);

        result.IsIntact.Should().BeFalse();
        result.BrokenAtEntryId.Should().Be(first.Id);
    }
}
