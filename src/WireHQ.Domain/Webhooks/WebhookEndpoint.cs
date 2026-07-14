using WireHQ.Domain.Common;
using WireHQ.Shared.Results;

namespace WireHQ.Domain.Webhooks;

/// <summary>
/// A per-organization <b>outbound webhook endpoint</b> (docs/26-api-keys-webhooks.md §7, ADR-043): a URL WireHQ
/// <b>POSTs to</b> when a subscribed event happens. Events are <b>audit action names</b> (or <c>prefix.*</c> globs),
/// so the whole audit stream is the catalog. Each delivery is HMAC-signed with the endpoint's
/// <see cref="SigningSecretCiphertext"/> — a random secret stored <b>reversibly</b> (via <c>ISecretProtector</c>,
/// unlike an API key's one-way hash) so the sender can re-sign, shown to the operator once and regenerable. Tenant-
/// owned in the reused <c>identity</c> schema (RLS for free). Entitlement-gated core (<c>api.keys</c>) — usable in
/// every edition, no CE strip; the interceptor/sender are idle until an endpoint exists.
/// </summary>
public sealed class WebhookEndpoint : AggregateRoot, ITenantOwned, IAuditable
{
    public const int MaxUrlLength = 2048;
    public const int MaxDescriptionLength = 256;
    public const int MaxEventTypes = 64;
    public const int MaxEventTypeLength = 128;

    private readonly List<WebhookEventSubscription> _eventTypes = [];

    // EF Core
    private WebhookEndpoint()
    {
    }

    private WebhookEndpoint(Guid id, Guid organizationId, string url, string? description, string signingSecretCiphertext)
        : base(id)
    {
        OrganizationId = organizationId;
        Url = url;
        Description = description;
        SigningSecretCiphertext = signingSecretCiphertext;
        Status = WebhookEndpointStatus.Active;
    }

    public Guid OrganizationId { get; private set; }

    public string Url { get; private set; } = null!;

    public string? Description { get; private set; }

    /// <summary>The subscribed audit-action patterns (exact names or <c>prefix.*</c> globs). Normal child entities
    /// keyed by <see cref="WebhookEventSubscription.EndpointId"/> — the ApiKeyScope/RolePermission lesson.</summary>
    public IReadOnlyCollection<WebhookEventSubscription> EventTypes => _eventTypes.AsReadOnly();

    /// <summary>The HMAC signing secret, encrypted at rest (reversible — the sender unprotects it to sign). Never
    /// returned by a query; the plaintext is shown once at create/rotate.</summary>
    public string SigningSecretCiphertext { get; private set; } = null!;

    public WebhookEndpointStatus Status { get; private set; }

    // IAuditable
    public DateTimeOffset CreatedAtUtc { get; private set; }
    public Guid? CreatedBy { get; private set; }
    public DateTimeOffset? UpdatedAtUtc { get; private set; }
    public Guid? UpdatedBy { get; private set; }

    public static Result<WebhookEndpoint> Create(
        Guid organizationId, string url, string? description, IReadOnlyCollection<string> eventTypes, string signingSecretCiphertext)
    {
        var validation = Validate(url, description, eventTypes);
        if (validation is { } error)
        {
            return error;
        }

        var endpoint = new WebhookEndpoint(Guid.CreateVersion7(), organizationId, url.Trim(), Normalize(description), signingSecretCiphertext);
        endpoint.ReplaceEventTypes(eventTypes);
        return endpoint;
    }

    /// <summary>Update the URL / description / subscriptions. The secret is rotated separately.</summary>
    public Result Update(string url, string? description, IReadOnlyCollection<string> eventTypes)
    {
        var validation = Validate(url, description, eventTypes);
        if (validation is { } error)
        {
            return error;
        }

        Url = url.Trim();
        Description = Normalize(description);
        ReplaceEventTypes(eventTypes);
        return Result.Success();
    }

    public void RotateSecret(string signingSecretCiphertext) => SigningSecretCiphertext = signingSecretCiphertext;

    public void Disable() => Status = WebhookEndpointStatus.Disabled;

    public void Enable() => Status = WebhookEndpointStatus.Active;

    public bool IsActive => Status == WebhookEndpointStatus.Active;

    /// <summary>True when this endpoint subscribes to the given audit action (exact or <c>prefix.*</c>).</summary>
    public bool Matches(string action) => _eventTypes.Any(s => WebhookEventMatcher.Matches(s.Pattern, action));

    private void ReplaceEventTypes(IReadOnlyCollection<string> eventTypes)
    {
        // Diff against the current set rather than clear-and-re-add: the subscriptions are normal composite-keyed
        // (EndpointId, Pattern) children, so on the tracked update path a Clear() + re-add of a retained pattern
        // marks a Deleted and an Added row with the same key → EF tracking conflict at SaveChanges. Removing only the
        // unwanted and adding only the missing emits just the real inserts/deletes (the Role.SetPermissions lesson).
        var desired = eventTypes.Select(p => p.Trim()).Where(p => p.Length > 0).ToHashSet(StringComparer.Ordinal);
        _eventTypes.RemoveAll(s => !desired.Contains(s.Pattern));
        var existing = _eventTypes.Select(s => s.Pattern).ToHashSet(StringComparer.Ordinal);
        foreach (var pattern in desired)
        {
            if (existing.Add(pattern))
            {
                _eventTypes.Add(new WebhookEventSubscription(Id, pattern));
            }
        }
    }

    private static Error? Validate(string url, string? description, IReadOnlyCollection<string> eventTypes)
    {
        if (string.IsNullOrWhiteSpace(url) || url.Length > MaxUrlLength
            || !Uri.TryCreate(url.Trim(), UriKind.Absolute, out var uri)
            || (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps))
        {
            return WebhookErrors.InvalidUrl;
        }

        if (description is { Length: > MaxDescriptionLength })
        {
            return WebhookErrors.InvalidDescription;
        }

        var patterns = eventTypes.Select(p => p?.Trim() ?? string.Empty).ToList();
        if (patterns.Count == 0 || patterns.Any(string.IsNullOrEmpty))
        {
            return WebhookErrors.NoEventTypes;
        }

        if (patterns.Any(p => p.Length > MaxEventTypeLength))
        {
            return WebhookErrors.InvalidEventType;
        }

        return patterns.Distinct(StringComparer.Ordinal).Count() > MaxEventTypes ? WebhookErrors.TooManyEventTypes : null;
    }

    private static string? Normalize(string? description) =>
        string.IsNullOrWhiteSpace(description) ? null : description.Trim();
}

/// <summary>One subscribed event pattern (an audit action name or a <c>prefix.*</c> glob). A NORMAL child entity
/// keyed by <see cref="EndpointId"/> (the ApiKeyScope lesson — dodges the owned-collection append gotcha).</summary>
public sealed class WebhookEventSubscription
{
    // EF Core
    private WebhookEventSubscription()
    {
    }

    public WebhookEventSubscription(Guid endpointId, string pattern)
    {
        EndpointId = endpointId;
        Pattern = pattern;
    }

    public Guid EndpointId { get; private set; }

    public string Pattern { get; private set; } = null!;
}

public enum WebhookEndpointStatus
{
    Active = 0,
    Disabled = 1,
}

/// <summary>Matches an audit action against a subscription pattern: <c>*</c> (all), a <c>prefix.*</c> glob
/// (matches the prefix and anything under it), or an exact action name.</summary>
public static class WebhookEventMatcher
{
    public static bool Matches(string pattern, string action)
    {
        if (pattern == "*")
        {
            return true;
        }

        if (pattern.EndsWith(".*", StringComparison.Ordinal))
        {
            var prefix = pattern[..^2];
            return action == prefix || action.StartsWith(prefix + ".", StringComparison.Ordinal);
        }

        return string.Equals(pattern, action, StringComparison.Ordinal);
    }
}

public static class WebhookErrors
{
    public static readonly Error InvalidUrl =
        Error.Validation("webhook.invalid_url", "Enter a valid absolute http(s) URL (2048 characters or fewer).");

    public static readonly Error InvalidDescription =
        Error.Validation("webhook.invalid_description", "The description must be 256 characters or fewer.");

    public static readonly Error NoEventTypes =
        Error.Validation("webhook.no_event_types", "Subscribe to at least one event type.");

    public static readonly Error TooManyEventTypes =
        Error.Validation("webhook.too_many_event_types", "An endpoint can subscribe to at most 64 event types.");

    public static readonly Error InvalidEventType =
        Error.Validation("webhook.invalid_event_type", "An event type must be 128 characters or fewer.");

    public static readonly Error NotFound =
        Error.NotFound("webhook.not_found", "Webhook endpoint was not found.");
}
