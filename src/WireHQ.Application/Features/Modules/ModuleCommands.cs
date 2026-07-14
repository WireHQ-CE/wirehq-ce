using FluentValidation;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using WireHQ.Application.Abstractions;
using WireHQ.Application.Abstractions.Licensing;
using WireHQ.Application.Abstractions.Persistence;
using WireHQ.Application.Authorization;
using WireHQ.Application.Common.Messaging;
using WireHQ.Application.Common.Observability;
using WireHQ.Application.Entitlements;
using WireHQ.Domain.Auditing;
using WireHQ.Domain.Modules;
using WireHQ.Shared.Results;

namespace WireHQ.Application.Features.Modules;

// Activate / deactivate a CE Marketplace module licence on this install (docs/29-ce-marketplace-modules.md
// M-8/M-9). Gated on marketplace.modules.manage; deliberately NOT feature-gated — this IS the mechanism that
// unlocks features, so gating it on one would be circular. CE-only (overlay-added).

public static class ModuleErrors
{
    public static readonly Error ActivationUnavailable = Error.Conflict(
        "modules.activation_unavailable",
        "Module activation is unavailable on this install — the licensing keys are not configured. Contact your operator.");

    public static readonly Error ServiceUnreachable = Error.Conflict(
        "modules.licensing_unreachable",
        "Could not reach the licensing service to activate the module. Check connectivity and try again.");

    public static readonly Error SlotTaken = Error.Conflict(
        "modules.slot_taken",
        "This licence is already active on another install. Deactivate it there before activating it here.");

    public static readonly Error LicenceRevoked = Error.Conflict(
        "modules.licence_revoked", "This licence has been revoked and can no longer be activated.");

    public static readonly Error DeactivateRefused = Error.Conflict(
        "modules.deactivate_refused",
        "Could not free this licence's activation — its move limit may be reached. Contact your operator.");

    public static readonly Error DeactivateUnavailable = Error.Conflict(
        "modules.deactivate_unavailable",
        "Could not reach the licensing service to free this licence's activation. The module stays active here — try again once connectivity is restored.");

    public static readonly Error InvalidLicenceKey = Error.Validation(
        "modules.invalid_licence_key", "That licence key is not valid.");

    public static Error UnknownModule(string slug) =>
        Error.NotFound("modules.unknown_module", $"The licence is for a module this edition does not recognise ('{slug}').");

    public static Error NotAvailableOnCommunityEdition(string slug) =>
        Error.Conflict("modules.not_available_on_ce", $"The '{slug}' module is not yet available on the Community Edition.");

    public static Error AlreadyActivated(string slug) =>
        Error.Conflict("modules.already_activated", $"The '{slug}' module is already activated on this install.");

    public static Error NotActivated(string slug) =>
        Error.NotFound("modules.not_activated", $"The '{slug}' module is not activated on this install.");
}

// --- Activate ---

public sealed record ActivateModuleCommand(string LicenceKey)
    : ICommand<ActivateModuleResponse>, IAuthorizedRequest
{
    public IReadOnlyCollection<string> RequiredPermissions => [Permissions.Modules.Manage];
}

/// <summary>The module a licence key unlocked, echoed so the console can confirm + refresh entitlements.</summary>
public sealed record ActivateModuleResponse(string ModuleSlug);

public sealed class ActivateModuleCommandValidator : AbstractValidator<ActivateModuleCommand>
{
    public ActivateModuleCommandValidator()
    {
        RuleFor(x => x.LicenceKey).NotEmpty().MaximumLength(2048);
    }
}

public sealed class ActivateModuleCommandHandler(
    IApplicationDbContext dbContext,
    IServiceProvider serviceProvider,
    ILicensingClient licensingClient,
    IDateTimeProvider clock,
    IAuditWriter audit)
    : ICommandHandler<ActivateModuleCommand, ActivateModuleResponse>
{
    public async Task<Result<ActivateModuleResponse>> Handle(ActivateModuleCommand command, CancellationToken cancellationToken)
    {
        var licenceKey = command.LicenceKey.Trim();

        // Verify the licence key locally against the pinned public keys (M-6). Resolve the verifier defensively:
        // on an install whose licensing key ring is unconfigured/invalid the lazy ring throws on first resolve
        // (M-18) — degrade to "activation unavailable" rather than 500 the request.
        ILicenceTokenVerifier verifier;
        LicenceTokenVerification<LicenceKeyClaims> verification;
        try
        {
            verifier = serviceProvider.GetRequiredService<ILicenceTokenVerifier>();
            verification = verifier.Verify<LicenceKeyClaims>(licenceKey);
        }
        catch (Exception)
        {
            return ModuleErrors.ActivationUnavailable;
        }

        if (!verification.IsValid || verification.Claims is not { } claims)
        {
            return await RejectAsync("unknown", verification.Failure?.ToString() ?? "invalid",
                ModuleErrors.InvalidLicenceKey, cancellationToken);
        }

        var module = ModuleCatalog.Find(claims.ModuleSlug);
        if (module is null)
        {
            return await RejectAsync(claims.ModuleSlug, "unknown_module",
                ModuleErrors.UnknownModule(claims.ModuleSlug), cancellationToken);
        }

        // M-8 fail-safe: a code-delivered module's capability code is stripped from the CE, so unlocking its
        // entitlement would light a dead feature (no controller, no handler, no nav). Refuse — the catalogue
        // shows these "coming soon" until the phase-4 modules/ runtime ships their code.
        if (module.Delivery != ModuleDelivery.GateOnly)
        {
            return await RejectAsync(module.Slug, "code_delivered",
                ModuleErrors.NotAvailableOnCommunityEdition(module.Slug), cancellationToken);
        }

        // An existing ACTIVE (or in-grace) row means it is already activated here. A REVOKED row is retained (the
        // evaluator needs it present to keep the feature locked), but it must not block re-licensing — a
        // replacement key updates that row in place rather than inserting a second one against the unique slug index.
        var existing = await dbContext.ModuleLicences.FirstOrDefaultAsync(l => l.ModuleSlug == module.Slug, cancellationToken);
        if (existing is { Status: not ModuleLicenceStatus.Revoked })
        {
            return ModuleErrors.AlreadyActivated(module.Slug);
        }

        // This install's stable identity — minted on first activation (M-5). The fingerprint is read from the
        // in-memory entity so it is available for the call-home; a freshly-minted identity is harmless if the
        // activation later fails (a random, PII-free value reused on the next attempt).
        var installIdentity = await dbContext.InstallIdentities.FirstOrDefaultAsync(cancellationToken);
        if (installIdentity is null)
        {
            installIdentity = InstallIdentity.Create(clock.UtcNow);
            dbContext.InstallIdentities.Add(installIdentity);
        }

        // Call home to bind the licence to this install and receive its activation token (M-7).
        var activation = await licensingClient.ActivateAsync(licenceKey, installIdentity.Fingerprint, cancellationToken);
        switch (activation.Outcome)
        {
            case LicensingOutcome.SlotTaken:
                return await RejectAsync(module.Slug, "slot_taken", ModuleErrors.SlotTaken, cancellationToken);
            case LicensingOutcome.Revoked:
                return await RejectAsync(module.Slug, "revoked", ModuleErrors.LicenceRevoked, cancellationToken);
            case LicensingOutcome.Activated when activation.ActivationToken is not null:
                break;
            default:
                return await RejectAsync(module.Slug, "licensing_unreachable", ModuleErrors.ServiceUnreachable, cancellationToken);
        }

        var activationToken = activation.ActivationToken!;

        // Verify the returned token LOCALLY against the pinned public keys + confirm it is bound to this install's
        // fingerprint; read the grace boundary from the verified token, never a caller-supplied field (M-6).
        if (ModuleTokenValidator.VerifiedGrace(verifier, activationToken, installIdentity.Fingerprint) is not { } graceEnds)
        {
            // The server bound the slot but we cannot trust the token (e.g. a key-rotation/config mismatch) — free
            // the just-acquired slot rather than stranding it, then reject.
            await licensingClient.DeactivateAsync(activationToken, cancellationToken);
            return await RejectAsync(module.Slug, "token_invalid", ModuleErrors.ActivationUnavailable, cancellationToken);
        }

        if (existing is not null)
        {
            existing.Reactivate(claims.LicenceId, licenceKey, activationToken, graceEnds, clock.UtcNow);
        }
        else
        {
            dbContext.ModuleLicences.Add(
                ModuleLicence.Activate(module.Slug, claims.LicenceId, licenceKey, activationToken, graceEnds, clock.UtcNow));
        }

        MarketplaceMetrics.Activations.Add(1, new KeyValuePair<string, object?>("outcome", "activated"));
        audit.Record("marketplace.module.activated", AuditOutcome.Success, "Module", module.Slug,
            new { module.Slug, Features = module.Features });

        return new ActivateModuleResponse(module.Slug);
    }

    private async Task<Result<ActivateModuleResponse>> RejectAsync(
        string slug, string reason, Error error, CancellationToken cancellationToken)
    {
        MarketplaceMetrics.Activations.Add(1, new KeyValuePair<string, object?>("outcome", "rejected"));
        audit.Record("marketplace.module.activation_rejected", AuditOutcome.Failure, "Module", slug, new { Reason = reason });
        // A failure Result is NOT persisted by the UnitOfWork behaviour (it commits only on success), so save the
        // rejection audit explicitly — else forged/invalid licence-key attempts would leave no trail (docs/29
        // M-13; mirrors LoginCommandHandler's failure-audit save).
        await dbContext.SaveChangesAsync(cancellationToken);
        return error;
    }
}

// --- Deactivate (frees the local activation so the licence can be moved to another install) ---

public sealed record DeactivateModuleCommand(string Slug)
    : ICommand, IAuthorizedRequest
{
    public IReadOnlyCollection<string> RequiredPermissions => [Permissions.Modules.Manage];
}

public sealed class DeactivateModuleCommandHandler(
    IApplicationDbContext dbContext, ILicensingClient licensingClient, IAuditWriter audit)
    : ICommandHandler<DeactivateModuleCommand>
{
    public async Task<Result> Handle(DeactivateModuleCommand command, CancellationToken cancellationToken)
    {
        var licence = await dbContext.ModuleLicences.FirstOrDefaultAsync(l => l.ModuleSlug == command.Slug, cancellationToken);
        if (licence is null)
        {
            return ModuleErrors.NotActivated(command.Slug);
        }

        // Free the server-side activation slot so the licence can move to another install (D-4). Only remove the local
        // row when the server CONFIRMS the slot is freed. A HARD refusal (409 — e.g. the move limit is reached) keeps
        // the slot bound → keep the row + token and report the refusal rather than a false success. A transient outage
        // (unreachable) leaves the deactivation UNCONFIRMED → also keep the row: removing it would delete the only local
        // copy of the token while the slot may still be bound, stranding the module (dark here yet un-movable — a
        // re-activation elsewhere would hit SlotTaken). Report a transient error so the operator retries when reachable.
        var outcome = await licensingClient.DeactivateAsync(licence.ActivationToken, cancellationToken);
        if (outcome == LicensingDeactivateOutcome.Refused)
        {
            MarketplaceMetrics.Deactivations.Add(1, new KeyValuePair<string, object?>("outcome", "refused"));
            return ModuleErrors.DeactivateRefused;
        }

        if (outcome == LicensingDeactivateOutcome.Unavailable)
        {
            MarketplaceMetrics.Deactivations.Add(1, new KeyValuePair<string, object?>("outcome", "unavailable"));
            return ModuleErrors.DeactivateUnavailable;
        }

        dbContext.ModuleLicences.Remove(licence); // outcome == Freed — the server confirmed the slot is free

        MarketplaceMetrics.Deactivations.Add(1, new KeyValuePair<string, object?>("outcome", "ok"));
        audit.Record("marketplace.module.deactivated", AuditOutcome.Success, "Module", command.Slug);

        return Result.Success();
    }
}
