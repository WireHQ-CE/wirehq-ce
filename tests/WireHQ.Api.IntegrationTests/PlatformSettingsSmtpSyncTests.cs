using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using WireHQ.Application.Abstractions.Security;
using WireHQ.Infrastructure.Persistence;
using WireHQ.Infrastructure.Persistence.Seeding;
using Xunit;

namespace WireHQ.Api.IntegrationTests;

/// <summary>
/// The self-host SMTP posture (docs/17 §7): with <c>Smtp:SyncFromConfig=true</c> the seeder re-applies
/// the <c>Smtp:*</c> configuration to the EXISTING platform-settings row on every boot — the Community
/// Edition's <c>.env</c> is its only SMTP interface. Without the flag (the SaaS default) an existing
/// row is never touched, so operator edits made in Settings → Email stay authoritative.
/// </summary>
[Collection(ApiCollection.Name)]
public sealed class PlatformSettingsSmtpSyncTests(WireHqApiFactory factory)
{
    private readonly WireHqApiFactory _factory = factory;

    [Fact]
    public async Task Sync_flag_reapplies_smtp_config_to_the_existing_row_and_default_does_not()
    {
        // The suite's boot already created the singleton row — this exercises the EXISTING-row path.
        var host1 = $"smtp-{Guid.NewGuid():N}.sync.test";
        await RunSeederAsync(syncFromConfig: true, smtpHost: host1);
        (await ReadSmtpHostAsync()).Should().Be(host1, because: "the sync flag makes config authoritative on every boot");

        // A subsequent boot with CHANGED config re-applies (edit .env → up -d → done).
        var host2 = $"smtp-{Guid.NewGuid():N}.sync.test";
        await RunSeederAsync(syncFromConfig: true, smtpHost: host2);
        (await ReadSmtpHostAsync()).Should().Be(host2);

        // The SaaS default (no flag): an existing row is never overwritten by config.
        await RunSeederAsync(syncFromConfig: false, smtpHost: "should-not-apply.sync.test");
        (await ReadSmtpHostAsync()).Should().Be(host2, because: "without the flag, operator edits stay authoritative");

        // A sync boot WITHOUT Smtp:Host never clears an existing setup.
        await RunSeederAsync(syncFromConfig: true, smtpHost: null);
        (await ReadSmtpHostAsync()).Should().Be(host2, because: "an empty Smtp:Host is a no-op, not a wipe");
    }

    private async Task RunSeederAsync(bool syncFromConfig, string? smtpHost)
    {
        using var scope = _factory.CreateBypassScope();
        var values = new Dictionary<string, string?> { ["Smtp:SyncFromConfig"] = syncFromConfig ? "true" : null };
        if (smtpHost is not null)
        {
            values["Smtp:Host"] = smtpHost;
            values["Smtp:Enabled"] = "true";
            values["Smtp:FromEmail"] = "no-reply@sync.test";
        }

        var seeder = new PlatformSettingsSeeder(
            scope.ServiceProvider.GetRequiredService<ApplicationDbContext>(),
            scope.ServiceProvider.GetRequiredService<ISecretProtector>(),
            new ConfigurationBuilder().AddInMemoryCollection(values).Build());
        await seeder.SeedAsync(CancellationToken.None);
    }

    private async Task<string?> ReadSmtpHostAsync()
    {
        using var scope = _factory.CreateBypassScope();
        var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
        return (await db.PlatformSettings.AsNoTracking().SingleAsync()).SmtpHost;
    }
}
