using System.Net;
using WireHQ.Application.Abstractions;

namespace WireHQ.Application.Common.Email;

/// <summary>
/// Builds the branded transactional emails (a dark shell + a gold call-to-action button, matching the
/// WireHQ brand). Pure — no I/O — so it is trivially unit-testable. (docs/01-brand-system.md)
/// </summary>
public static class EmailTemplates
{
    private const string Gold = "#F5B301";
    private const string Ink = "#0A0B0D";
    private const string Surface = "#12151B";

    /// <summary>
    /// An optional edition tagline rendered under the WireHQ wordmark in every email (e.g.
    /// "Community Edition"). Set ONCE at startup from <c>Branding:EditionTagline</c> (Program.cs) —
    /// a process-wide constant, so the templates stay pure and deterministic. Null (the SaaS
    /// default) renders the header exactly as before.
    /// </summary>
    public static string? EditionTagline { get; set; }

    public static EmailMessage VerifyEmail(string to, string name, string verifyUrl) =>
        Build(to,
            subject: "Confirm your WireHQ email",
            heading: $"Welcome to WireHQ, {Safe(name)}",
            intro: "Confirm your email address to finish setting up your account.",
            buttonText: "Confirm email",
            buttonUrl: verifyUrl,
            footer: "If you didn't create a WireHQ account, you can ignore this email.");

    public static EmailMessage PasswordReset(string to, string resetUrl) =>
        Build(to,
            subject: "Reset your WireHQ password",
            heading: "Reset your password",
            intro: "We received a request to reset your password. This link is valid for one hour.",
            buttonText: "Reset password",
            buttonUrl: resetUrl,
            footer: "If you didn't request this, you can safely ignore this email — your password won't change.");

    public static EmailMessage Invite(string to, string organizationName, string setPasswordUrl) =>
        Build(to,
            subject: $"You've been invited to {organizationName} on WireHQ",
            heading: $"Join {Safe(organizationName)} on WireHQ",
            intro: $"You've been invited to {Safe(organizationName)}. Set a password to activate your account and get started.",
            buttonText: "Accept invite & set password",
            buttonUrl: setPasswordUrl,
            footer: "If you weren't expecting this invitation, you can ignore this email.");

    public static EmailMessage AddedToOrganization(string to, string organizationName, string loginUrl) =>
        Build(to,
            subject: $"You've been added to {organizationName} on WireHQ",
            heading: $"You're now part of {Safe(organizationName)}",
            intro: $"Your existing WireHQ account has been added to {Safe(organizationName)}. Sign in to get started.",
            buttonText: "Sign in",
            buttonUrl: loginUrl,
            footer: "If you weren't expecting this, contact your organization's administrator.");

    /// <summary>A contact-form submission, delivered to the operator's inbox. Includes the sender's details.</summary>
    public static EmailMessage ContactForm(string to, string fromName, string fromEmail, string? subject, string message) =>
        Build(to,
            subject: string.IsNullOrWhiteSpace(subject) ? $"New contact message from {fromName}" : $"Contact: {subject}",
            heading: "New contact-form message",
            intro: $"From {fromName} ({fromEmail}):\n\n{message}",
            buttonText: null,
            buttonUrl: null,
            footer: $"Reply directly to {fromEmail}. Sent via the WireHQ website contact form.");

    public static EmailMessage Test(string to) =>
        Build(to,
            subject: "WireHQ SMTP test",
            heading: "Your SMTP settings work 🎉",
            intro: "This is a test message sent from WireHQ → Settings → Email. If you're reading it, transactional email is configured correctly.",
            buttonText: null,
            buttonUrl: null,
            footer: "Sent by WireHQ.");

    /// <summary>Marketplace licence fulfilment: the signed licence key, delivered to the buyer. The key
    /// carries no secrets (its power is the signature), but treat the mail like a receipt. (docs/19 §4)</summary>
    public static EmailMessage LicenceIssued(string to, string moduleSlug, string licenceKey, DateTimeOffset updateWindowEndUtc) =>
        Build(to,
            subject: $"Your WireHQ Marketplace licence — {moduleSlug}",
            heading: "Your module licence is ready",
            intro: $"""
                Thanks for your <strong>{Safe(moduleSlug)}</strong> licence. Enter this key in your
                WireHQ instance under <strong>Modules → Licence key</strong> to activate it:
                <div style="margin:16px 0;padding:14px 16px;border-radius:8px;background:#11141A;border:1px solid #2A2F3A;font-family:ui-monospace,SFMono-Regular,Menlo,monospace;font-size:12px;line-height:1.6;color:#E7C365;word-break:break-all;">{Safe(licenceKey)}</div>
                Your licence includes updates until <strong>{updateWindowEndUtc:d MMMM yyyy}</strong> —
                after that the module keeps working; it just stops updating until you renew.
                """,
            buttonText: null,
            buttonUrl: null,
            footer: "Keep this email safe — the key is shown only here and in your purchase history.");

    /// <summary>Buyer-portal magic link (docs/19 §4, D-5): a passwordless sign-in to "my purchases".</summary>
    public static EmailMessage MarketplacePortalLink(string to, string portalUrl) =>
        Build(to,
            subject: "Sign in to your WireHQ purchases",
            heading: "Your sign-in link",
            intro: "Use this link to view your WireHQ Marketplace purchases — your licence keys, receipts and installs. It's valid for 15 minutes and works once.",
            buttonText: "View my purchases",
            buttonUrl: portalUrl,
            footer: "If you didn't request this, you can safely ignore it — no one can see your purchases without this link.");

    private const string StatusAlertsFooter =
        "You're receiving this because you turned on status alerts. Manage them under your account notification settings.";

    /// <summary>A service-status incident was opened (docs/20 §6) — sent to users who opted into status alerts.
    /// (The <c>Service*</c> naming keeps these core/CE-safe templates clear of the SaaS-only CE strip markers.)</summary>
    public static EmailMessage ServiceIncidentOpened(string to, string title, string impact, string message, string statusUrl) =>
        Build(to,
            subject: $"WireHQ status: {title}",
            heading: "We're looking into an issue",
            intro: $"{title} — {impact}. {message}",
            buttonText: "View status page",
            buttonUrl: statusUrl,
            footer: StatusAlertsFooter);

    /// <summary>A status incident was resolved — sent to users who opted into status alerts.</summary>
    public static EmailMessage ServiceIncidentResolved(string to, string title, string statusUrl) =>
        Build(to,
            subject: $"WireHQ status: resolved — {title}",
            heading: "Incident resolved",
            intro: $"The incident \"{title}\" has been resolved. Thanks for your patience.",
            buttonText: "View status page",
            buttonUrl: statusUrl,
            footer: StatusAlertsFooter);

    /// <summary>Maintenance was scheduled — sent to users who opted into status alerts.</summary>
    public static EmailMessage ServiceMaintenanceScheduled(string to, string title, DateTimeOffset startsAtUtc, DateTimeOffset endsAtUtc, string statusUrl) =>
        Build(to,
            subject: $"WireHQ status: scheduled maintenance — {title}",
            heading: "Scheduled maintenance",
            intro: $"Planned maintenance: {title}. Window: {startsAtUtc:d MMM yyyy HH:mm} – {endsAtUtc:HH:mm} UTC.",
            buttonText: "View status page",
            buttonUrl: statusUrl,
            footer: StatusAlertsFooter);

    private static EmailMessage Build(
        string to, string subject, string heading, string intro, string? buttonText, string? buttonUrl, string footer)
    {
        var button = buttonText is not null && buttonUrl is not null
            ? $"""
              <tr><td style="padding:8px 0 24px;">
                <a href="{Attr(buttonUrl)}" style="display:inline-block;background:{Gold};color:{Ink};text-decoration:none;font-weight:600;padding:12px 22px;border-radius:8px;">{Safe(buttonText)}</a>
              </td></tr>
              <tr><td style="padding:0 0 8px;font-size:12px;color:#9AA1AD;">Or paste this link into your browser:<br><a href="{Attr(buttonUrl)}" style="color:{Gold};word-break:break-all;">{Safe(buttonUrl)}</a></td></tr>
              """
            : string.Empty;

        var html = $"""
            <!doctype html><html><body style="margin:0;background:{Ink};font-family:-apple-system,Segoe UI,Roboto,Helvetica,Arial,sans-serif;">
              <table role="presentation" width="100%" cellpadding="0" cellspacing="0" style="background:{Ink};padding:32px 16px;">
                <tr><td align="center">
                  <table role="presentation" width="480" cellpadding="0" cellspacing="0" style="max-width:480px;background:{Surface};border-radius:14px;overflow:hidden;border:1px solid #222730;">
                    <tr><td style="padding:24px 28px;border-bottom:1px solid #222730;">
                      <span style="font-size:20px;font-weight:700;color:#FFFFFF;">Wire<span style="color:{Gold};">HQ</span></span>{(EditionTagline is { Length: > 0 } tagline ? $"""<br><span style="font-size:10px;font-weight:600;letter-spacing:0.14em;text-transform:uppercase;color:#9AA1AD;">{Safe(tagline)}</span>""" : string.Empty)}
                    </td></tr>
                    <tr><td style="padding:28px;">
                      <table role="presentation" width="100%" cellpadding="0" cellspacing="0">
                        <tr><td style="font-size:20px;font-weight:700;color:#FFFFFF;padding:0 0 10px;">{Safe(heading)}</td></tr>
                        <tr><td style="font-size:14px;line-height:22px;color:#C7CCD4;padding:0 0 22px;">{Safe(intro)}</td></tr>
                        {button}
                        <tr><td style="font-size:12px;line-height:18px;color:#6B7280;padding:18px 0 0;border-top:1px solid #222730;">{Safe(footer)}</td></tr>
                      </table>
                    </td></tr>
                  </table>
                </td></tr>
              </table>
            </body></html>
            """;

        var text = buttonUrl is not null
            ? $"{heading}\n\n{intro}\n\n{buttonText}: {buttonUrl}\n\n{footer}"
            : $"{heading}\n\n{intro}\n\n{footer}";
        if (EditionTagline is { Length: > 0 } editionText)
        {
            text += $"\n\nWireHQ — {editionText}";
        }

        return new EmailMessage(to, subject, html, text);
    }

    private static string Safe(string value) => WebUtility.HtmlEncode(value);

    private static string Attr(string value) => WebUtility.HtmlEncode(value);
}
