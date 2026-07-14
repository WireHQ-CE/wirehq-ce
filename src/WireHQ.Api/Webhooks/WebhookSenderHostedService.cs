using WireHQ.Application.Features.Webhooks;

namespace WireHQ.Api.Webhooks;

/// <summary>
/// Drives the webhook outbox (docs/26-api-keys-webhooks.md §8). Every <c>Webhooks:Sender:IntervalSeconds</c>
/// (default 10, clamped 5–300) it refreshes the <see cref="WebhookSubscriptionCache"/> (so newly created/removed
/// endpoints take effect) and asks <see cref="WebhookDispatchScheduler"/> to send any due deliveries. Opt-out via
/// <c>Webhooks:Sender:Enabled=false</c> (set in the test factory so it can't race tests, which drive the scheduler
/// directly). <b>Kept-core</b> — unlike the SaaS-only directory scheduler this ships in every edition (webhooks are
/// entitlement-gated core, K-2); it is idle until an endpoint exists (empty cache, no due rows).
/// </summary>
public sealed class WebhookSenderHostedService(
    WebhookSubscriptionCache cache,
    WebhookDispatchScheduler scheduler,
    IConfiguration configuration,
    ILogger<WebhookSenderHostedService> logger)
    : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (!Enabled(configuration))
        {
            logger.LogInformation("Webhook sender is disabled (Webhooks:Sender:Enabled=false).");
            return;
        }

        var interval = ResolveInterval(configuration);
        logger.LogInformation("Webhook sender started (every {Seconds:0}s).", interval.TotalSeconds);

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await cache.RefreshAsync(stoppingToken);
                await scheduler.RunDueAsync(stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "Webhook sender sweep failed; backing off.");
            }

            try
            {
                await Task.Delay(interval, stoppingToken);
            }
            catch (OperationCanceledException)
            {
                break;
            }
        }

        logger.LogInformation("Webhook sender stopped.");
    }

    private static bool Enabled(IConfiguration configuration)
    {
        var raw = configuration["Webhooks:Sender:Enabled"];
        return string.IsNullOrWhiteSpace(raw) || (bool.TryParse(raw, out var value) && value);
    }

    private static TimeSpan ResolveInterval(IConfiguration configuration)
    {
        var raw = configuration["Webhooks:Sender:IntervalSeconds"];
        var seconds = int.TryParse(raw, out var value) ? value : 10;
        return TimeSpan.FromSeconds(Math.Clamp(seconds, 5, 300));
    }
}
