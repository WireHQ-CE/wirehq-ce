using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using WireHQ.Application.Abstractions;
using WireHQ.Application.Abstractions.Notifications;
using WireHQ.Application.Abstractions.Persistence;
using WireHQ.Application.Features.Notifications;
using WireHQ.Domain.Notifications;
using Xunit;

namespace WireHQ.Api.IntegrationTests;

/// <summary>
/// The dispatch loop must survive one channel throwing. The batch <c>SaveChangesAsync</c> sits AFTER the per-delivery
/// loop, so an exception that escapes the loop's catch discards the outcome of every delivery in the batch — including
/// ones already sent — and, because <c>Attempts</c> never advances, the poison row stays at the front of the next
/// ordered batch and re-sends everything ahead of it on every tick.
///
/// That is exactly what an <c>HttpClient</c> timeout used to do: it throws <see cref="TaskCanceledException"/>, which
/// IS an <see cref="OperationCanceledException"/>, so the old <c>when (ex is not OperationCanceledException)</c> filter
/// let it straight through even though the caller's token was never cancelled. Shipped Chat Alerts hit it whenever an
/// operator's Teams/Slack webhook hung for 10s.
///
/// The pair below pins BOTH halves: a timeout must be caught (T1), and a genuine shutdown must still propagate (T2).
/// </summary>
[Collection(ApiCollection.Name)]
public sealed class NotificationDispatchResilienceTests(WireHqApiFactory factory) : IDisposable
{
    private readonly List<IDisposable> _hosts = [];

    public void Dispose()
    {
        foreach (var h in _hosts) h.Dispose();
    }

    /// <summary>A stand-in Email channel: throws for one recipient, succeeds for the rest.</summary>
    private sealed class ThrowingEmailChannel(string poisonRecipient, Func<Exception> thrower) : INotificationChannel
    {
        public ChannelKind Kind => ChannelKind.Email;

        public Task<ChannelSendResult> SendAsync(ChannelSendRequest request, CancellationToken cancellationToken) =>
            request.Recipient == poisonRecipient
                ? throw thrower()
                : Task.FromResult(ChannelSendResult.Ok(202));
    }

    private (IServiceProvider Services, NotificationDispatchScheduler Scheduler) HostWith(INotificationChannel channel)
    {
        // RunDueAsync builds its registry with GetServices<INotificationChannel>().ToDictionary(c => c.Kind), which
        // throws on a duplicate key — so the real Email channel must be REMOVED, not merely joined.
        var host = factory.WithWebHostBuilder(b => b.ConfigureServices(services =>
        {
            services.RemoveAll<INotificationChannel>();
            services.AddSingleton(channel);
        }));
        _hosts.Add(host);
        return (host.Services, host.Services.GetRequiredService<NotificationDispatchScheduler>());
    }

    [Fact]
    public async Task A_provider_timeout_fails_only_its_own_delivery_and_the_batch_still_commits()
    {
        var poison = $"poison-{Guid.NewGuid():N}@example.test";
        var sibling = $"sibling-{Guid.NewGuid():N}@example.test";

        // The exact shape an HttpClient timeout produces.
        var (services, scheduler) = HostWith(new ThrowingEmailChannel(
            poison, () => new TaskCanceledException("timed out", new TimeoutException())));

        var (orgId, poisonId, siblingId) = await SeedTwoDeliveriesAsync(services, poison, sibling);

        // CancellationToken.None ⇒ IsCancellationRequested is false: precisely the defect condition.
        var act = async () => await scheduler.RunDueAsync(CancellationToken.None);
        await act.Should().NotThrowAsync("a channel timeout is a delivery failure, not a reason to abandon the batch");

        // Re-read from a FRESH context — asserting on tracked entities would pass even unfixed, since the bug is that
        // the batch save never runs.
        await using var scope = services.CreateAsyncScope();
        // RLS is fail-closed on notification_deliveries, so a scope with no tenant context cannot read or write it —
        // the drain does the same thing (RunDueAsync: SetBypass on its own scope).
        scope.ServiceProvider.GetRequiredService<ISettableTenantContext>().SetBypass();
        var db = scope.ServiceProvider.GetRequiredService<IApplicationDbContext>();

        var poisoned = await db.NotificationDeliveries.IgnoreQueryFilters()
            .SingleAsync(d => d.Id == poisonId);
        poisoned.Status.Should().Be(NotificationDeliveryStatus.Pending, "a timeout is retryable, not terminal");
        poisoned.Attempts.Should().Be(1, "the attempt must be recorded, or this row poisons every future batch");

        var ok = await db.NotificationDeliveries.IgnoreQueryFilters().SingleAsync(d => d.Id == siblingId);
        ok.Status.Should().Be(NotificationDeliveryStatus.Delivered,
            "the sibling was actually sent — losing its outcome means sending it again");

        _ = orgId;
    }

    [Fact]
    public async Task A_genuine_shutdown_still_propagates_and_burns_no_attempt()
    {
        var poison = $"shutdown-{Guid.NewGuid():N}@example.test";
        var sibling = $"sibling-{Guid.NewGuid():N}@example.test";

        using var cts = new CancellationTokenSource();
        var (services, scheduler) = HostWith(new ThrowingEmailChannel(poison, () =>
        {
            cts.Cancel();                                   // the host is stopping…
            return new OperationCanceledException(cts.Token); // …and that is why we threw
        }));

        var (_, poisonId, _) = await SeedTwoDeliveriesAsync(services, poison, sibling);

        var act = async () => await scheduler.RunDueAsync(cts.Token);
        await act.Should().ThrowAsync<OperationCanceledException>(
            "a real shutdown must stop the drain, not be swallowed into a delivery failure");

        await using var scope = services.CreateAsyncScope();
        // RLS is fail-closed on notification_deliveries, so a scope with no tenant context cannot read or write it —
        // the drain does the same thing (RunDueAsync: SetBypass on its own scope).
        scope.ServiceProvider.GetRequiredService<ISettableTenantContext>().SetBypass();
        var db = scope.ServiceProvider.GetRequiredService<IApplicationDbContext>();
        var untouched = await db.NotificationDeliveries.IgnoreQueryFilters().SingleAsync(d => d.Id == poisonId);
        untouched.Attempts.Should().Be(0, "shutting down must not consume a retry");
        untouched.Status.Should().Be(NotificationDeliveryStatus.Pending);
    }

    private static async Task<(Guid OrgId, Guid PoisonId, Guid SiblingId)> SeedTwoDeliveriesAsync(
        IServiceProvider services, string poison, string sibling)
    {
        await using var scope = services.CreateAsyncScope();
        // RLS is fail-closed on notification_deliveries, so a scope with no tenant context cannot read or write it —
        // the drain does the same thing (RunDueAsync: SetBypass on its own scope).
        scope.ServiceProvider.GetRequiredService<ISettableTenantContext>().SetBypass();
        var db = scope.ServiceProvider.GetRequiredService<IApplicationDbContext>();

        var orgId = Guid.CreateVersion7();
        var ruleId = Guid.CreateVersion7();
        var jobId = Guid.CreateVersion7();
        var now = DateTimeOffset.UtcNow;

        // The poison delivery is created FIRST so it is not last in the ordered batch — the sibling must be reached
        // and must survive.
        var poisonDelivery = NotificationDelivery.Create(
            orgId, ruleId, jobId, ChannelKind.Email, [], poison, "subject", "body", null, now.AddMinutes(-2));
        var siblingDelivery = NotificationDelivery.Create(
            orgId, ruleId, jobId, ChannelKind.Email, [], sibling, "subject", "body", null, now.AddMinutes(-1));

        db.NotificationDeliveries.Add(poisonDelivery);
        db.NotificationDeliveries.Add(siblingDelivery);
        await db.SaveChangesAsync(CancellationToken.None);

        return (orgId, poisonDelivery.Id, siblingDelivery.Id);
    }
}
