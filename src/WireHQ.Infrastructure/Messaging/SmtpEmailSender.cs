using MailKit.Net.Smtp;
using MailKit.Security;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using MimeKit;
using WireHQ.Application.Abstractions;
using WireHQ.Application.Abstractions.Persistence;
using WireHQ.Application.Abstractions.Security;

namespace WireHQ.Infrastructure.Messaging;

/// <summary>
/// Sends transactional email over SMTP (MailKit), using the Super-Admin-configured settings on the
/// <c>PlatformSettings</c> singleton. When SMTP is disabled or unconfigured it falls back to logging the
/// message (so dev/demo and the test suite work with no mail server). A real send failure throws — the
/// transactional callers wrap-and-log; the "send test email" command surfaces it. (docs/04-security.md)
/// </summary>
public sealed class SmtpEmailSender(
    IApplicationDbContext dbContext,
    ISecretProtector secretProtector,
    ILogger<SmtpEmailSender> logger)
    : IEmailSender
{
    public async Task SendAsync(EmailMessage message, CancellationToken cancellationToken)
    {
        var settings = await dbContext.PlatformSettings.AsNoTracking().FirstOrDefaultAsync(cancellationToken);

        if (settings is not { SmtpEnabled: true, SmtpConfigured: true })
        {
            logger.LogInformation(
                "EMAIL (dev sink — SMTP not configured) → {To}\n  Subject: {Subject}\n  {Body}",
                message.To, message.Subject, message.TextBody ?? message.HtmlBody);
            return;
        }

        // SmtpConfigured implies these are set, but assert it explicitly so a misconfiguration fails loudly
        // here rather than deep inside MailKit — and so the non-null contract of MailboxAddress/ConnectAsync
        // is satisfied (newer MailKit annotates host/address as non-nullable).
        if (string.IsNullOrWhiteSpace(settings.SmtpHost) || string.IsNullOrWhiteSpace(settings.SmtpFromEmail))
        {
            throw new InvalidOperationException("SMTP is enabled but the host or from-address is not configured.");
        }

        var mime = new MimeMessage();
        mime.From.Add(new MailboxAddress(settings.SmtpFromName ?? "WireHQ", settings.SmtpFromEmail));
        mime.To.Add(MailboxAddress.Parse(message.To));
        mime.Subject = message.Subject;
        mime.Body = new BodyBuilder { HtmlBody = message.HtmlBody, TextBody = message.TextBody }.ToMessageBody();

        var security = settings.SmtpUseSsl ? SecureSocketOptions.SslOnConnect : SecureSocketOptions.StartTlsWhenAvailable;

        using var client = new SmtpClient();
        await client.ConnectAsync(settings.SmtpHost, settings.SmtpPort, security, cancellationToken);

        if (!string.IsNullOrWhiteSpace(settings.SmtpUsername))
        {
            var password = string.IsNullOrWhiteSpace(settings.SmtpPasswordCiphertext)
                ? string.Empty
                : secretProtector.Unprotect(settings.SmtpPasswordCiphertext);
            await client.AuthenticateAsync(settings.SmtpUsername, password, cancellationToken);
        }

        await client.SendAsync(mime, cancellationToken);
        await client.DisconnectAsync(quit: true, cancellationToken);

        logger.LogInformation("Sent transactional email to {To} ({Subject}).", message.To, message.Subject);
    }
}
