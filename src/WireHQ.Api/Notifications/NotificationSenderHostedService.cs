using WireHQ.Application.Features.Notifications;

namespace WireHQ.Api.Notifications;

/// <summary>
/// Drives the notification dispatch spine (docs/35-notifications.md §4.1). Every
/// <c>Notifications:Sender:IntervalSeconds</c> (default 10, clamped 5–300) it refreshes the
/// <see cref="NotificationRouteCache"/> (so newly created/changed rules take effect) and asks
/// <see cref="NotificationDispatchScheduler"/> to expand any pending jobs and send any due deliveries. Opt-out via
/// <c>Notifications:Sender:Enabled=false</c> (set in the test factory so it can't race tests, which drive the
/// scheduler directly). <b>Kept-core</b> — ships in every edition; idle until a rule exists (empty cache, no jobs).
/// </summary>
public sealed class NotificationSenderHostedService(
    NotificationRouteCache cache,
    NotificationDispatchScheduler scheduler,
    IConfiguration configuration,
    ILogger<NotificationSenderHostedService> logger)
    : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (!Enabled(configuration))
        {
            logger.LogInformation("Notification sender is disabled (Notifications:Sender:Enabled=false).");
            return;
        }

        var interval = ResolveInterval(configuration);
        logger.LogInformation("Notification sender started (every {Seconds:0}s).", interval.TotalSeconds);

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
                logger.LogError(ex, "Notification sender sweep failed; backing off.");
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

        logger.LogInformation("Notification sender stopped.");
    }

    private static bool Enabled(IConfiguration configuration)
    {
        var raw = configuration["Notifications:Sender:Enabled"];
        return string.IsNullOrWhiteSpace(raw) || (bool.TryParse(raw, out var value) && value);
    }

    private static TimeSpan ResolveInterval(IConfiguration configuration)
    {
        var raw = configuration["Notifications:Sender:IntervalSeconds"];
        var seconds = int.TryParse(raw, out var value) ? value : 10;
        return TimeSpan.FromSeconds(Math.Clamp(seconds, 5, 300));
    }
}
