using FluentValidation;
using Microsoft.EntityFrameworkCore;
using WireHQ.Application.Abstractions;
using WireHQ.Application.Abstractions.Persistence;
using WireHQ.Application.Authorization;
using WireHQ.Application.Common.Messaging;
using WireHQ.Application.Entitlements;
using WireHQ.Domain.ApiKeys;
using WireHQ.Domain.Auditing;
using WireHQ.Shared.Results;

namespace WireHQ.Application.Features.ApiKeys;

// Create / revoke an organization's API keys (docs/26-api-keys-webhooks.md §6). Enterprise-gated (api.keys) +
// api.keys.manage. Kept-core (entitlement-gated, not CE-stripped) — the CE (Enterprise by default) gets them.

// --- Create ---

public sealed record CreateApiKeyCommand(string Name, IReadOnlyList<string> Scopes, DateTimeOffset? ExpiresAtUtc)
    : ICommand<CreateApiKeyResponse>, IAuthorizedRequest, IRequiresVerifiedEmail, IRequiresFeature
{
    public IReadOnlyCollection<string> RequiredPermissions => [Permissions.ApiKeys.Manage];

    public string RequiredFeature => PlanFeatures.ApiKeys;
}

/// <summary>The new key's id and its plaintext secret — returned ONCE (only the hash is stored).</summary>
public sealed record CreateApiKeyResponse(Guid Id, string Key);

public sealed class CreateApiKeyCommandValidator : AbstractValidator<CreateApiKeyCommand>
{
    public CreateApiKeyCommandValidator()
    {
        RuleFor(x => x.Name).NotEmpty().MaximumLength(ApiKey.MaxNameLength);
        RuleFor(x => x.Scopes).NotEmpty();
    }
}

public sealed class CreateApiKeyCommandHandler(
    IApplicationDbContext dbContext, ITenantContext tenant, ICurrentUser currentUser, IDateTimeProvider clock, IAuditWriter audit)
    : ICommandHandler<CreateApiKeyCommand, CreateApiKeyResponse>
{
    // No async work — everything is in-memory (scope validation, token gen) + a synchronous Add. Non-async +
    // Task.FromResult to dodge CS1998 (warnings-as-errors).
    public Task<Result<CreateApiKeyResponse>> Handle(CreateApiKeyCommand command, CancellationToken cancellationToken) =>
        Task.FromResult(Create(command));

    private Result<CreateApiKeyResponse> Create(CreateApiKeyCommand command)
    {
        if (tenant.OrganizationId is not { } organizationId)
        {
            return Error.Forbidden("auth.tenant_required", "No active organization.");
        }

        if (command.ExpiresAtUtc is { } expiry && expiry <= clock.UtcNow)
        {
            return ApiKeyErrors.InvalidExpiry;
        }

        // Scopes are permission keys. Each must be a real catalogue permission AND one the actor holds — the
        // escalation guard, so a key can never be more powerful than the person who minted it (docs/26 §6).
        var catalogue = Permissions.All.Select(p => p.Key).ToHashSet(StringComparer.Ordinal);
        var scopes = command.Scopes.Distinct(StringComparer.Ordinal).ToList();
        if (scopes.Any(s => !catalogue.Contains(s)))
        {
            return ApiKeyErrors.UnknownScope;
        }

        if (scopes.Any(s => !currentUser.HasPermission(s)))
        {
            return ApiKeyErrors.ScopeNotGrantable;
        }

        var generated = ApiKeyToken.Generate();
        var keyResult = ApiKey.Create(
            organizationId, command.Name, generated.DisplayPrefix, generated.Hash, scopes, currentUser.UserId, command.ExpiresAtUtc);
        if (keyResult.IsFailure)
        {
            return keyResult.Error;
        }

        var key = keyResult.Value;
        dbContext.ApiKeys.Add(key);

        audit.Record("api.keys.created", AuditOutcome.Success, nameof(ApiKey), key.Id.ToString(),
            new { key.Name, key.KeyPrefix, ScopeCount = scopes.Count, key.ExpiresAtUtc });

        return new CreateApiKeyResponse(key.Id, generated.Plaintext);
    }
}

// --- Revoke (hard delete — the key stops working immediately) ---

public sealed record RevokeApiKeyCommand(Guid Id)
    : ICommand, IAuthorizedRequest, IRequiresFeature
{
    public IReadOnlyCollection<string> RequiredPermissions => [Permissions.ApiKeys.Manage];

    public string RequiredFeature => PlanFeatures.ApiKeys;
}

public sealed class RevokeApiKeyCommandHandler(IApplicationDbContext dbContext, IAuditWriter audit)
    : ICommandHandler<RevokeApiKeyCommand>
{
    public async Task<Result> Handle(RevokeApiKeyCommand command, CancellationToken cancellationToken)
    {
        var key = await dbContext.ApiKeys.FirstOrDefaultAsync(k => k.Id == command.Id, cancellationToken);
        if (key is null)
        {
            return ApiKeyErrors.NotFound;
        }

        dbContext.ApiKeys.Remove(key); // scopes cascade

        audit.Record("api.keys.revoked", AuditOutcome.Success, nameof(ApiKey), key.Id.ToString(), new { key.Name, key.KeyPrefix });

        return Result.Success();
    }
}
