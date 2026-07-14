using System.Text.Json;
using FluentValidation;
using Microsoft.EntityFrameworkCore;
using WireHQ.Application.Abstractions;
using WireHQ.Application.Abstractions.Persistence;
using WireHQ.Application.Abstractions.Security;
using WireHQ.Application.Authorization;
using WireHQ.Application.Common.Messaging;
using WireHQ.Application.Entitlements;
using WireHQ.Domain.Auditing;
using WireHQ.Domain.Webhooks;
using WireHQ.Shared.Results;

namespace WireHQ.Application.Features.Webhooks;

// Create / update / enable-disable / delete / rotate-secret / test an org's webhook endpoints
// (docs/26-api-keys-webhooks.md §8). All gated api.keys.manage + PlanFeatures.ApiKeys (Enterprise). Kept-core.

// --- Create ---

public sealed record CreateWebhookCommand(string Url, string? Description, IReadOnlyList<string> EventTypes)
    : ICommand<CreateWebhookResponse>, IAuthorizedRequest, IRequiresVerifiedEmail, IRequiresFeature
{
    public IReadOnlyCollection<string> RequiredPermissions => [Permissions.ApiKeys.Manage];

    public string RequiredFeature => PlanFeatures.ApiKeys;
}

/// <summary>The new endpoint's id and its plaintext signing secret — returned ONCE (only the ciphertext is stored).</summary>
public sealed record CreateWebhookResponse(Guid Id, string SigningSecret);

public sealed class CreateWebhookCommandValidator : AbstractValidator<CreateWebhookCommand>
{
    public CreateWebhookCommandValidator()
    {
        RuleFor(x => x.Url).NotEmpty().MaximumLength(WebhookEndpoint.MaxUrlLength);
        RuleFor(x => x.EventTypes).NotEmpty();
    }
}

public sealed class CreateWebhookCommandHandler(
    IApplicationDbContext dbContext, ITenantContext tenant, ISecretProtector secretProtector, IAuditWriter audit)
    : ICommandHandler<CreateWebhookCommand, CreateWebhookResponse>
{
    // No async work — secret gen + protect + Add are synchronous. Non-async + Task.FromResult to dodge CS1998.
    public Task<Result<CreateWebhookResponse>> Handle(CreateWebhookCommand command, CancellationToken cancellationToken) =>
        Task.FromResult(Create(command));

    private Result<CreateWebhookResponse> Create(CreateWebhookCommand command)
    {
        if (tenant.OrganizationId is not { } organizationId)
        {
            return Error.Forbidden("auth.tenant_required", "No active organization.");
        }

        var secret = WebhookSecret.Generate();
        var result = WebhookEndpoint.Create(organizationId, command.Url, command.Description, command.EventTypes, secretProtector.Protect(secret));
        if (result.IsFailure)
        {
            return result.Error;
        }

        var endpoint = result.Value;
        dbContext.WebhookEndpoints.Add(endpoint);

        audit.Record("webhooks.endpoint_created", AuditOutcome.Success, nameof(WebhookEndpoint), endpoint.Id.ToString(),
            new { endpoint.Url, EventTypes = command.EventTypes });

        return new CreateWebhookResponse(endpoint.Id, secret);
    }
}

// --- Update (url / description / subscriptions) ---

public sealed record UpdateWebhookCommand(Guid Id, string Url, string? Description, IReadOnlyList<string> EventTypes)
    : ICommand, IAuthorizedRequest, IRequiresFeature
{
    public IReadOnlyCollection<string> RequiredPermissions => [Permissions.ApiKeys.Manage];

    public string RequiredFeature => PlanFeatures.ApiKeys;
}

public sealed class UpdateWebhookCommandValidator : AbstractValidator<UpdateWebhookCommand>
{
    public UpdateWebhookCommandValidator()
    {
        RuleFor(x => x.Url).NotEmpty().MaximumLength(WebhookEndpoint.MaxUrlLength);
        RuleFor(x => x.EventTypes).NotEmpty();
    }
}

public sealed class UpdateWebhookCommandHandler(IApplicationDbContext dbContext, IAuditWriter audit)
    : ICommandHandler<UpdateWebhookCommand>
{
    public async Task<Result> Handle(UpdateWebhookCommand command, CancellationToken cancellationToken)
    {
        var endpoint = await dbContext.WebhookEndpoints
            .Include(e => e.EventTypes)
            .FirstOrDefaultAsync(e => e.Id == command.Id, cancellationToken);
        if (endpoint is null)
        {
            return WebhookErrors.NotFound;
        }

        var result = endpoint.Update(command.Url, command.Description, command.EventTypes);
        if (result.IsFailure)
        {
            return result.Error;
        }

        audit.Record("webhooks.endpoint_updated", AuditOutcome.Success, nameof(WebhookEndpoint), endpoint.Id.ToString(),
            new { endpoint.Url, EventTypes = command.EventTypes });

        return Result.Success();
    }
}

// --- Enable / disable ---

public sealed record SetWebhookStatusCommand(Guid Id, bool Enabled)
    : ICommand, IAuthorizedRequest, IRequiresFeature
{
    public IReadOnlyCollection<string> RequiredPermissions => [Permissions.ApiKeys.Manage];

    public string RequiredFeature => PlanFeatures.ApiKeys;
}

public sealed class SetWebhookStatusCommandHandler(IApplicationDbContext dbContext, IAuditWriter audit)
    : ICommandHandler<SetWebhookStatusCommand>
{
    public async Task<Result> Handle(SetWebhookStatusCommand command, CancellationToken cancellationToken)
    {
        var endpoint = await dbContext.WebhookEndpoints.FirstOrDefaultAsync(e => e.Id == command.Id, cancellationToken);
        if (endpoint is null)
        {
            return WebhookErrors.NotFound;
        }

        if (command.Enabled)
        {
            endpoint.Enable();
        }
        else
        {
            endpoint.Disable();
        }

        audit.Record(command.Enabled ? "webhooks.endpoint_enabled" : "webhooks.endpoint_disabled",
            AuditOutcome.Success, nameof(WebhookEndpoint), endpoint.Id.ToString());

        return Result.Success();
    }
}

// --- Delete (cascade its subscriptions + delivery history) ---

public sealed record DeleteWebhookCommand(Guid Id)
    : ICommand, IAuthorizedRequest, IRequiresFeature
{
    public IReadOnlyCollection<string> RequiredPermissions => [Permissions.ApiKeys.Manage];

    public string RequiredFeature => PlanFeatures.ApiKeys;
}

public sealed class DeleteWebhookCommandHandler(IApplicationDbContext dbContext, IAuditWriter audit)
    : ICommandHandler<DeleteWebhookCommand>
{
    public async Task<Result> Handle(DeleteWebhookCommand command, CancellationToken cancellationToken)
    {
        var endpoint = await dbContext.WebhookEndpoints.FirstOrDefaultAsync(e => e.Id == command.Id, cancellationToken);
        if (endpoint is null)
        {
            return WebhookErrors.NotFound;
        }

        // Subscriptions cascade with the endpoint (their FK stays); deliveries are a soft reference (no FK — see
        // WebhookConfigurations), so remove them explicitly in the same unit of work.
        var deliveries = await dbContext.WebhookDeliveries.Where(d => d.EndpointId == command.Id).ToListAsync(cancellationToken);
        dbContext.WebhookDeliveries.RemoveRange(deliveries);
        dbContext.WebhookEndpoints.Remove(endpoint);

        audit.Record("webhooks.endpoint_deleted", AuditOutcome.Success, nameof(WebhookEndpoint), endpoint.Id.ToString(),
            new { endpoint.Url });

        return Result.Success();
    }
}

// --- Rotate the signing secret ---

public sealed record RotateWebhookSecretCommand(Guid Id)
    : ICommand<RotateWebhookSecretResponse>, IAuthorizedRequest, IRequiresFeature
{
    public IReadOnlyCollection<string> RequiredPermissions => [Permissions.ApiKeys.Manage];

    public string RequiredFeature => PlanFeatures.ApiKeys;
}

/// <summary>The new plaintext signing secret — returned ONCE.</summary>
public sealed record RotateWebhookSecretResponse(string SigningSecret);

public sealed class RotateWebhookSecretCommandHandler(
    IApplicationDbContext dbContext, ISecretProtector secretProtector, IAuditWriter audit)
    : ICommandHandler<RotateWebhookSecretCommand, RotateWebhookSecretResponse>
{
    public async Task<Result<RotateWebhookSecretResponse>> Handle(RotateWebhookSecretCommand command, CancellationToken cancellationToken)
    {
        var endpoint = await dbContext.WebhookEndpoints.FirstOrDefaultAsync(e => e.Id == command.Id, cancellationToken);
        if (endpoint is null)
        {
            return WebhookErrors.NotFound;
        }

        var secret = WebhookSecret.Generate();
        endpoint.RotateSecret(secretProtector.Protect(secret));

        audit.Record("webhooks.secret_rotated", AuditOutcome.Success, nameof(WebhookEndpoint), endpoint.Id.ToString());

        return new RotateWebhookSecretResponse(secret);
    }
}

// --- Send a test event (enqueues a delivery directly, bypassing the subscription cache) ---

public sealed record SendTestWebhookCommand(Guid Id)
    : ICommand, IAuthorizedRequest, IRequiresFeature
{
    public IReadOnlyCollection<string> RequiredPermissions => [Permissions.ApiKeys.Manage];

    public string RequiredFeature => PlanFeatures.ApiKeys;
}

public sealed class SendTestWebhookCommandHandler(IApplicationDbContext dbContext, IDateTimeProvider clock, IAuditWriter audit)
    : ICommandHandler<SendTestWebhookCommand>
{
    private const string TestEventType = "webhooks.test";

    public async Task<Result> Handle(SendTestWebhookCommand command, CancellationToken cancellationToken)
    {
        var endpoint = await dbContext.WebhookEndpoints.FirstOrDefaultAsync(e => e.Id == command.Id, cancellationToken);
        if (endpoint is null)
        {
            return WebhookErrors.NotFound;
        }

        var now = clock.UtcNow;
        var payload = JsonSerializer.Serialize(new
        {
            type = TestEventType,
            occurredAt = now,
            organizationId = endpoint.OrganizationId,
            message = "This is a test event from WireHQ.",
        });

        dbContext.WebhookDeliveries.Add(WebhookDelivery.Create(endpoint.OrganizationId, endpoint.Id, TestEventType, payload, now));

        audit.Record("webhooks.test_sent", AuditOutcome.Success, nameof(WebhookEndpoint), endpoint.Id.ToString());

        return Result.Success();
    }
}
