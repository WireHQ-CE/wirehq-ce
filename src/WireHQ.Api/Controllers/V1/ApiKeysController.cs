using Asp.Versioning;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using WireHQ.Api.Controllers;
using WireHQ.Application.Features.ApiKeys;

namespace WireHQ.Api.Controllers.V1;

/// <summary>
/// API keys for the active organization (docs/26-api-keys-webhooks.md §6). A key is a scoped, revocable bearer
/// secret that lets a script/CI/service call the WireHQ API without a human login; its scopes are permission keys,
/// and an actor can only grant a key scopes it holds. Org-scoped, RBAC-gated (<c>api.keys.manage</c>) and
/// Enterprise-gated (<c>api.keys</c>). Kept-core — API keys are an entitlement-gated platform capability (usable in
/// the CE, which defaults to Enterprise), not a SaaS-only module.
/// </summary>
[ApiVersion("1.0")]
[Route("api/v{version:apiVersion}/api-keys")]
[Authorize]
public sealed class ApiKeysController : ApiControllerBase
{
    [HttpGet]
    public async Task<IActionResult> List(CancellationToken cancellationToken) =>
        Ok(await Sender.Send(new ListApiKeysQuery(), cancellationToken));

    /// <summary>The grantable-scope catalog for the key editor.</summary>
    [HttpGet("scopes")]
    public async Task<IActionResult> Scopes(CancellationToken cancellationToken) =>
        Ok(await Sender.Send(new ListApiKeyScopesQuery(), cancellationToken));

    /// <summary>Create a key → returns the plaintext secret ONCE (only its hash is stored).</summary>
    [HttpPost]
    public async Task<IActionResult> Create(CreateApiKeyRequest request, CancellationToken cancellationToken) =>
        Created(await Sender.Send(new CreateApiKeyCommand(request.Name, request.Scopes ?? [], request.ExpiresAtUtc), cancellationToken));

    [HttpDelete("{id:guid}")]
    public async Task<IActionResult> Revoke(Guid id, CancellationToken cancellationToken) =>
        NoContent(await Sender.Send(new RevokeApiKeyCommand(id), cancellationToken));
}

public sealed record CreateApiKeyRequest(string Name, IReadOnlyList<string>? Scopes, DateTimeOffset? ExpiresAtUtc);
