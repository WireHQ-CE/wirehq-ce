using FluentAssertions;
using WireHQ.Application.Common.Email;
using Xunit;

namespace WireHQ.Application.UnitTests;

/// <summary>
/// The transactional-email templates' edition tagline (docs/17 §6): set once at startup from
/// <c>Branding:EditionTagline</c> (a self-hosted Community Edition sets "Community Edition"), it
/// renders under the wordmark in the HTML and as a trailing line in the text version. Unset — the
/// SaaS default — the output is identical to the un-editioned template.
/// </summary>
public sealed class EmailTemplatesTests
{
    [Fact]
    public void Edition_tagline_renders_in_html_and_text_when_set_and_is_absent_when_not()
    {
        var original = EmailTemplates.EditionTagline;
        try
        {
            EmailTemplates.EditionTagline = null;
            var plain = EmailTemplates.PasswordReset("user@wirehq.test", "https://wirehq.test/reset?token=t");
            plain.HtmlBody.Should().NotContain("Community Edition");
            plain.TextBody.Should().NotContain("Community Edition");

            EmailTemplates.EditionTagline = "Community Edition";
            var editioned = EmailTemplates.PasswordReset("user@wirehq.test", "https://wirehq.test/reset?token=t");
            editioned.HtmlBody.Should().Contain("Community Edition", because: "the tagline renders under the wordmark");
            editioned.TextBody.Should().Contain("WireHQ — Community Edition");
        }
        finally
        {
            EmailTemplates.EditionTagline = original;
        }
    }

    [Fact]
    public void Edition_tagline_is_html_encoded()
    {
        var original = EmailTemplates.EditionTagline;
        try
        {
            EmailTemplates.EditionTagline = "<script>alert(1)</script>";
            var message = EmailTemplates.Test("user@wirehq.test");
            message.HtmlBody.Should().NotContain("<script>", because: "the tagline is operator config, but encode-on-output is the rule");
            message.HtmlBody.Should().Contain("&lt;script&gt;");
        }
        finally
        {
            EmailTemplates.EditionTagline = original;
        }
    }
}
