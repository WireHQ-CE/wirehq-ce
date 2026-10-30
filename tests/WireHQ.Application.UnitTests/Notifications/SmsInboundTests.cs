using FluentAssertions;
using WireHQ.Application.Features.Notifications;
using Xunit;

namespace WireHQ.Application.UnitTests.Notifications;

/// <summary>The inbound SMS keyword classifier + the inbound-token hashing (docs/36 §4.3/§5, Slice D).</summary>
public sealed class SmsInboundTests
{
    [Theory]
    [InlineData("STOP", SmsInboundAction.Stop)]
    [InlineData("stop", SmsInboundAction.Stop)]
    [InlineData("StopAll", SmsInboundAction.Stop)]
    [InlineData("UNSUBSCRIBE", SmsInboundAction.Stop)]
    [InlineData("CANCEL", SmsInboundAction.Stop)]
    [InlineData("STOP please", SmsInboundAction.Stop)] // the first token is the keyword
    [InlineData("START", SmsInboundAction.Start)]
    [InlineData("yes", SmsInboundAction.Start)]
    [InlineData("UNSTOP", SmsInboundAction.Start)]
    [InlineData("HELP", SmsInboundAction.Help)]
    [InlineData("info", SmsInboundAction.Help)]
    [InlineData("hello there", SmsInboundAction.None)]
    [InlineData("", SmsInboundAction.None)]
    [InlineData("   ", SmsInboundAction.None)]
    [InlineData(null, SmsInboundAction.None)]
    public void Classify_maps_the_keyword_family(string? body, SmsInboundAction expected)
    {
        SmsInboundKeywords.Classify(body).Should().Be(expected);
    }

    [Fact]
    public void Generate_returns_a_token_and_its_matching_hash()
    {
        var (token, hash) = SmsInboundToken.Generate();

        token.Should().NotBeNullOrWhiteSpace();
        hash.Should().Be(SmsInboundToken.Hash(token), "the stored hash must be the SHA-256 of the plaintext token");
    }

    [Fact]
    public void Generated_tokens_are_unique()
    {
        var a = SmsInboundToken.Generate();
        var b = SmsInboundToken.Generate();

        a.Token.Should().NotBe(b.Token);
        a.Hash.Should().NotBe(b.Hash);
    }

    [Fact]
    public void Hash_is_deterministic_for_the_lookup()
    {
        SmsInboundToken.Hash("the-token").Should().Be(SmsInboundToken.Hash("the-token"));
        SmsInboundToken.Hash("the-token").Should().NotBe(SmsInboundToken.Hash("other-token"));
    }
}
