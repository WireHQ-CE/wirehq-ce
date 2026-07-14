using WireHQ.Application.Abstractions;
using WireHQ.Application.Abstractions.Notifications;
using WireHQ.Application.Features.Notifications;
using WireHQ.Domain.Notifications;

namespace WireHQ.Infrastructure.Messaging.Notifications;

/// <summary>
/// The free-core <b>Email</b> notification channel (docs/35-notifications.md §4.3). A thin adapter over the kept-core
/// <see cref="IEmailSender"/> — it uses the <b>operator's</b> SMTP sender (there is deliberately no per-org SMTP
/// override, an unguarded non-HTTP SSRF vector — blocker B-7). Renders the redacted plain summary into minimal HTML
/// for the medium; the plain text rides as the text alternative.
/// </summary>
public sealed class EmailChannel(IEmailSender emailSender) : INotificationChannel
{
    public ChannelKind Kind => ChannelKind.Email;

    public async Task<ChannelSendResult> SendAsync(ChannelSendRequest request, CancellationToken cancellationToken)
    {
        try
        {
            var html = NotificationSummary.ToHtml(request.Subject, request.Body);
            await emailSender.SendAsync(new EmailMessage(request.Recipient, request.Subject, html, request.Body), cancellationToken);
            return ChannelSendResult.Ok();
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // The SMTP sender throws on a real send failure; surface it as a retryable delivery failure (backoff).
            return ChannelSendResult.Failed(ex.Message);
        }
    }
}
