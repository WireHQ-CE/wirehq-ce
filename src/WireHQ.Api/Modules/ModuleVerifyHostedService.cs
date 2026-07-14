using Microsoft.EntityFrameworkCore;
using WireHQ.Application.Abstractions;
using WireHQ.Application.Abstractions.Licensing;
using WireHQ.Application.Abstractions.Persistence;
using WireHQ.Application.Features.Modules;
using WireHQ.Domain.Modules;

namespace WireHQ.Api.Modules;

/// <summary>
/// The Community Edition's weekly licence call-home (docs/29-ce-marketplace-modules.md M-7). Each pass re-verifies
/// every active module licence with the hosted service: a healthy licence gets a fresh activation token + grace
/// window (kept well ahead of the 14-day next-verify boundary), a revoked one is disabled, and an unreachable
/// service is left alone — the licence stays in its offline grace window and only locks once grace lapses
/// (nag-don't-kill; the control plane never faults). CE-ONLY (overlay-added; registered by the CE
/// <c>AddActivatedModules</c> seam, so the SaaS build never runs it). Interval:
/// <c>Modules:VerifySweeper:IntervalSeconds</c> (default 604800 = weekly, clamped 3600–2592000).
/// </summary>
public sealed class ModuleVerifyHostedService(
    IServiceScopeFactory scopeFactory,
    IConfiguration configuration,
    ILogger<ModuleVerifyHostedService> logger)
    : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var interval = ResolveInterval(configuration);
        logger.LogInformation("Module licence verify loop started (every {Seconds:0}s).", interval.TotalSeconds);

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await VerifyOnceAsync(stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "Module licence verify pass failed; backing off.");
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

        logger.LogInformation("Module licence verify loop stopped.");
    }

    private async Task VerifyOnceAsync(CancellationToken cancellationToken)
    {
        using var scope = scopeFactory.CreateScope();
        // The modules tables are install-global (no RLS policy), but the runtime role still connects tenant-scoped;
        // bypass keeps the sweep consistent with the other background sweeps.
        scope.ServiceProvider.GetRequiredService<ISettableTenantContext>().SetBypass();

        var dbContext = scope.ServiceProvider.GetRequiredService<IApplicationDbContext>();
        var clock = scope.ServiceProvider.GetRequiredService<IDateTimeProvider>();
        var licensingClient = scope.ServiceProvider.GetRequiredService<ILicensingClient>();

        var licences = await dbContext.ModuleLicences
            .Where(l => l.Status == ModuleLicenceStatus.Active)
            .ToListAsync(cancellationToken);
        if (licences.Count == 0)
        {
            return;
        }

        var installIdentity = await dbContext.InstallIdentities.FirstOrDefaultAsync(cancellationToken);
        if (installIdentity is null)
        {
            return; // no identity ⇒ nothing was ever activated online
        }

        // The verifier's key ring is lazy and throws when unconfigured (M-18) — resolve it defensively; a bad ring
        // means we cannot validate refreshed tokens, so skip the pass (the licences stay in their current grace).
        ILicenceTokenVerifier verifier;
        try
        {
            verifier = scope.ServiceProvider.GetRequiredService<ILicenceTokenVerifier>();
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Module licence verify skipped — the licensing key ring is not configured.");
            return;
        }

        var fingerprint = installIdentity.Fingerprint;
        var now = clock.UtcNow;
        var changed = false;

        foreach (var licence in licences)
        {
            var verification = await licensingClient.VerifyAsync(licence.ActivationToken, fingerprint, cancellationToken);
            switch (verification.Outcome)
            {
                case LicensingVerifyOutcome.Revoked:
                    licence.Revoke();
                    changed = true;
                    logger.LogInformation("Module '{Slug}' licence was revoked by the licensing service.", licence.ModuleSlug);
                    break;

                case LicensingVerifyOutcome.Active
                    when ModuleTokenValidator.VerifiedGrace(verifier, verification.ActivationToken, fingerprint) is { } grace:
                    licence.RecordVerification(verification.ActivationToken!, grace, now);
                    changed = true;
                    break;

                case LicensingVerifyOutcome.Expired:
                    // The stored token has expired (its exp is the grace boundary), so verify is rejected — this is
                    // exactly a licence that lapsed while offline. Re-activate with the stored licence key
                    // (idempotent for this fingerprint) to obtain a fresh token: a healthy licence self-heals and
                    // re-grants; a revoked one is disabled; an unreachable service leaves it in grace (M-7).
                    var reactivation = await licensingClient.ActivateAsync(licence.LicenceKey, fingerprint, cancellationToken);
                    if (reactivation.Outcome == LicensingOutcome.Revoked)
                    {
                        licence.Revoke();
                        changed = true;
                        logger.LogInformation("Module '{Slug}' licence was revoked on re-activation.", licence.ModuleSlug);
                    }
                    else if (reactivation.Outcome == LicensingOutcome.Activated
                        && ModuleTokenValidator.VerifiedGrace(verifier, reactivation.ActivationToken, fingerprint) is { } freshGrace)
                    {
                        licence.RecordVerification(reactivation.ActivationToken!, freshGrace, now);
                        changed = true;
                        logger.LogInformation("Module '{Slug}' licence re-activated after expiry.", licence.ModuleSlug);
                    }

                    break;

                // Active-but-unverifiable-token, or Unavailable: leave the licence in its current grace window.
                default:
                    break;
            }
        }

        if (changed)
        {
            await dbContext.SaveChangesAsync(cancellationToken);
        }
    }

    // Indexer + int.TryParse (not the Binder's GetValue<T> extension — matches the other sweepers).
    private static TimeSpan ResolveInterval(IConfiguration configuration)
    {
        var raw = configuration["Modules:VerifySweeper:IntervalSeconds"];
        var seconds = int.TryParse(raw, out var value) ? value : 604800;
        return TimeSpan.FromSeconds(Math.Clamp(seconds, 3600, 2592000));
    }
}
