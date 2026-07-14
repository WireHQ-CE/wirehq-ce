namespace WireHQ.Application.Abstractions;

/// <summary>
/// Sends transactional email (invites, verification, password reset, MFA notices). Abstracted so
/// the provider (SMTP, SES, SendGrid…) is an Infrastructure detail. The foundation ships a
/// dev implementation that logs the message, so flows are testable without a mail server.
/// </summary>
public interface IEmailSender
{
    /// <summary>Send a message. The SMTP implementation throws on a real send failure; the transactional
    /// callers wrap-and-log so a failed email never breaks the user-facing operation.</summary>
    Task SendAsync(EmailMessage message, CancellationToken cancellationToken);
}

/// <summary>An outgoing transactional email. <see cref="TextBody"/> is an optional plain-text alternative.</summary>
public sealed record EmailMessage(string To, string Subject, string HtmlBody, string? TextBody = null);
