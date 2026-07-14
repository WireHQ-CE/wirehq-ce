using WireHQ.Domain.Common;

namespace WireHQ.Domain.Notifications;

/// <summary>
/// Per-organization configuration for a delivery channel (docs/35-notifications.md §4.2). Wave 1 <see cref="ChannelKind.Email"/>
/// needs none (it uses the operator's SMTP sender — there is deliberately no per-org SMTP override, an unguarded
/// non-HTTP SSRF vector, docs/35 B-7). Wave 2 Chat stores the destination webhook URL; Wave 4 SMS stores the provider
/// credential (encrypted via <c>ISecretProtector</c> — reversible, since the sender needs plaintext, not a one-way
/// hash). One row per (org, channel). Tenant-owned in the reused <c>identity</c> schema.
/// </summary>
public sealed class NotificationChannelConfig : AggregateRoot, ITenantOwned, IAuditable
{
    public const int MaxUrlLength = 2048;
    public const int MaxFromLength = 64;

    // EF Core
    private NotificationChannelConfig()
    {
    }

    private NotificationChannelConfig(Guid id, Guid organizationId, ChannelKind channel)
        : base(id)
    {
        OrganizationId = organizationId;
        ChannelKind = channel;
        ProviderKind = NotificationProviderKind.None;
        Enabled = true;
    }

    public Guid OrganizationId { get; private set; }

    public ChannelKind ChannelKind { get; private set; }

    public NotificationProviderKind ProviderKind { get; private set; }

    /// <summary>The chat destination webhook URL, <b>encrypted at rest</b> via <c>ISecretProtector</c> (reversible — the
    /// sender unprotects it to POST). A Slack/Teams incoming-webhook URL is a bearer secret, so it is never stored in
    /// cleartext and never returned by a query. SSRF-guarded at send time (docs/35 §4.3).</summary>
    public string? DestinationUrl { get; private set; }

    /// <summary>The SMS provider credential, encrypted at rest via <c>ISecretProtector</c> (Wave 4). Never queried out.</summary>
    public string? CredentialCiphertext { get; private set; }

    /// <summary>The SMS sender number / chat "from" label (Wave 4/2).</summary>
    public string? FromValue { get; private set; }

    public bool Enabled { get; private set; }

    // IAuditable
    public DateTimeOffset CreatedAtUtc { get; private set; }
    public Guid? CreatedBy { get; private set; }
    public DateTimeOffset? UpdatedAtUtc { get; private set; }
    public Guid? UpdatedBy { get; private set; }

    public static NotificationChannelConfig Create(Guid organizationId, ChannelKind channel) =>
        new(Guid.CreateVersion7(), organizationId, channel);

    /// <summary>Set the chat destination (Wave 2). <paramref name="destinationCiphertext"/> is the
    /// <c>ISecretProtector</c>-encrypted webhook URL — the caller protects it; never pass a cleartext URL.</summary>
    public void SetChatDestination(NotificationProviderKind provider, string destinationCiphertext, string? fromValue)
    {
        ProviderKind = provider;
        DestinationUrl = destinationCiphertext;
        FromValue = fromValue;
    }

    /// <summary>Set the SMS provider credential (Wave 4).</summary>
    public void SetSmsProvider(NotificationProviderKind provider, string credentialCiphertext, string fromValue)
    {
        ProviderKind = provider;
        CredentialCiphertext = credentialCiphertext;
        FromValue = fromValue;
    }

    public void Enable() => Enabled = true;

    public void Disable() => Enabled = false;
}
