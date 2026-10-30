using FluentAssertions;
using WireHQ.Domain.ValueObjects;
using Xunit;

namespace WireHQ.Domain.UnitTests.ValueObjects;

/// <summary>E.164 validation + normalization for the SMS phone value object (docs/36-notifications.md §8).</summary>
public sealed class PhoneNumberTests
{
    [Theory]
    [InlineData("+14155550123", "+14155550123")]
    [InlineData("+1 (415) 555-0123", "+14155550123")] // separators stripped
    [InlineData("  +44 20 7946 0958 ", "+442079460958")]
    [InlineData("0044 20 7946 0958", "+442079460958")] // 00 international prefix → +
    public void Create_normalizes_valid_e164(string input, string expected)
    {
        var result = PhoneNumber.Create(input);

        result.IsSuccess.Should().BeTrue();
        result.Value.Value.Should().Be(expected);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("4155550123")]          // no leading '+'
    [InlineData("+0155550123")]         // country code can't start with 0
    [InlineData("+1")]                  // too short (needs ≥ 2 digits)
    [InlineData("+1415555zzzz")]         // non-digits
    [InlineData("+1234567890123456")]   // 16 digits > E.164 max of 15
    public void Create_rejects_invalid_number(string input)
    {
        var result = PhoneNumber.Create(input);

        result.IsFailure.Should().BeTrue();
    }

    [Fact]
    public void Numbers_are_equal_by_canonical_value()
    {
        var a = PhoneNumber.Create("+1 (415) 555-0123").Value;
        var b = PhoneNumber.Create("+14155550123").Value;

        a.Should().Be(b);
    }

    /// <summary>
    /// The inbound SMS webhook canonicalises the provider's <c>From</c> through this type so the suppression ledger key
    /// and the send-path match key are the same by construction. Twilio sends E.164 with the <c>+</c>, so that works —
    /// but a **bare national/international number with no `+` is REJECTED**, not guessed at, and that is deliberate:
    /// <c>447700900123</c> cannot be resolved to a country without knowing the provider's own convention.
    ///
    /// This matters for Slice E: **Vonage sends `msisdn` without a leading `+`**, so the normalisation must happen in
    /// the Vonage adapter, which knows its own format — not here, and not by loosening this rule. Until then a Vonage
    /// STOP fails closed (dropped + audited) rather than being recorded under a key the send path could never match.
    /// Pinned as a test because the failure it prevents is a total STOP failure with a console reporting the opposite.
    /// </summary>
    [Theory]
    [InlineData("+447700900123", "+447700900123")]   // Twilio: already canonical
    [InlineData("00447700900123", "+447700900123")]  // the 00 international prefix normalises
    [InlineData("+44 7700 900123", "+447700900123")] // separators are stripped
    public void Provider_from_formats_that_canonicalise(string input, string expected) =>
        PhoneNumber.Create(input).Value.Value.Should().Be(expected);

    [Fact]
    public void A_bare_number_with_no_plus_is_rejected_not_guessed()
    {
        // Vonage's msisdn shape. Rejection is the correct answer HERE — the fix belongs in that adapter (docs/36 §12).
        PhoneNumber.Create("447700900123").IsFailure.Should().BeTrue();
    }
}
