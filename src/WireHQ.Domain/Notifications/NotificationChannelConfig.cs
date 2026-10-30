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

    /// <summary>SHA-256 hash (hex) of the org's <b>inbound webhook token</b> (Wave 4 Slice D). The plaintext token sits
    /// in the operator's provider-configured inbound URL and is shown to them ONCE; only its hash is stored, so the
    /// inbound STOP/HELP webhook resolves the org by hashing the presented token and matching — never by decrypting a
    /// credential first (docs/36 §5). Null until a token is minted.</summary>
    public string? InboundWebhookTokenHash { get; private set; }

    public const int InboundWebhookTokenPrefixLength = 6;

    /// <summary>How long a rotated-away token keeps working, so the operator can update their provider without
    /// dropping a STOP in the gap. Lazily expired on read — no background job.</summary>
    public static readonly TimeSpan InboundWebhookTokenGrace = TimeSpan.FromHours(72);

    /// <summary>First few characters of the plaintext token, so the console can say WHICH token is live without
    /// storing the secret. The full value is unrecoverable after mint.</summary>
    public string? InboundWebhookTokenPrefix { get; private set; }

    public DateTimeOffset? InboundWebhookTokenMintedAtUtc { get; private set; }

    /// <summary>The previous token's hash during a rotation overlap, and when it stops being accepted.</summary>
    public string? PreviousInboundWebhookTokenHash { get; private set; }

    public DateTimeOffset? PreviousInboundWebhookTokenExpiresAtUtc { get; private set; }

    /// <summary>When a correctly-signed inbound request last arrived bearing the CURRENT token — i.e. proof that the
    /// operator really did wire this URL into their provider. Bound to the current hash: minting or rotating clears it,
    /// because a new token has never been proven. This is what makes "STOP is receivable" a machine-checked fact rather
    /// than a checklist item, and it is the precondition <see cref="InboundReady"/> exposes to the send path.</summary>
    public DateTimeOffset? InboundCurrentTokenVerifiedAtUtc { get; private set; }

    /// <summary>When a request last arrived on the PREVIOUS token — "your provider is still pointed at the old URL".</summary>
    public DateTimeOffset? InboundPreviousTokenSeenAtUtc { get; private set; }

    /// <summary>Signature-verification failures against a resolved config — the only signal that someone is probing
    /// the endpoint, or that the operator's provider is misconfigured.</summary>
    public DateTimeOffset? InboundLastRejectedAtUtc { get; private set; }

    public int InboundRejectedCount { get; private set; }

    public const int MaxA2pReferenceLength = 128;

    /// <summary>When the operator attested their 10DLC/A2P campaign is registered (Wave 4 Slice D, §4.6). Null = not
    /// attested ⇒ the pre-send gate holds every SMS send. Cleared whenever the sender changes (<see cref="SetSmsProvider"/>),
    /// so a new number/provider must be re-attested. This is an operator trust attestation, not a provider check.</summary>
    public DateTimeOffset? A2pAttestedAtUtc { get; private set; }

    /// <summary>The user who made the current A2P attestation (audit trail); null when not attested.</summary>
    public Guid? A2pAttestedBy { get; private set; }

    /// <summary>Optional operator-supplied campaign / brand reference recorded with the attestation (for their records).</summary>
    public string? A2pReference { get; private set; }

    /// <summary>True when the org currently has a live A2P attestation — the pre-send precondition for any SMS send.</summary>
    public bool A2pAttested => A2pAttestedAtUtc is not null;

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

    /// <summary>Set the SMS provider credential (Wave 4). Changing the credential/sender <b>clears any A2P attestation</b>
    /// — a different account/number is a different campaign, so the operator must re-attest before it can send (§4.6).</summary>
    public void SetSmsProvider(NotificationProviderKind provider, string credentialCiphertext, string fromValue)
    {
        ProviderKind = provider;
        CredentialCiphertext = credentialCiphertext;
        FromValue = fromValue;
        ClearA2pAttestation();

        // A different account/number is a different sender, so the old readiness proof was for something else — the
        // operator must demonstrate the webhook again. The TOKEN deliberately survives: the inbound URL is
        // account-independent, and invalidating it would silently break a provider that is still posting to it.
        InboundCurrentTokenVerifiedAtUtc = null;
        InboundPreviousTokenSeenAtUtc = null;
    }

    /// <summary>Record the operator's A2P/10DLC attestation (Wave 4 Slice D §4.6) — a deliberate, audited trust
    /// statement that their campaign is registered and their content is covered by registered sample templates.</summary>
    public void AttestA2p(Guid attestedByUserId, string? reference, DateTimeOffset nowUtc)
    {
        A2pAttestedAtUtc = nowUtc;
        A2pAttestedBy = attestedByUserId;
        A2pReference = string.IsNullOrWhiteSpace(reference) ? null : reference.Trim();
    }

    /// <summary>Revoke the A2P attestation (operator toggles it off, or the sender changed) — SMS sending holds again.</summary>
    public void ClearA2pAttestation()
    {
        A2pAttestedAtUtc = null;
        A2pAttestedBy = null;
        A2pReference = null;
    }

    /// <summary>
    /// Store the SHA-256 hash of a freshly-minted inbound webhook token (Wave 4 Slice D). The caller generates the
    /// random token, shows the plaintext to the operator ONCE, and passes only its hash here.
    ///
    /// <paramref name="grace"/> null = <b>revoke and mint</b>: the old hash stops working immediately. That is the
    /// correct behaviour for a suspected leak, and it is why revocation must never be entitlement-gated — an operator
    /// whose module has lapsed must still be able to close an anonymous endpoint.
    /// <paramref name="grace"/> non-null = <b>rotate with overlap</b>: the old hash keeps working until it expires, so
    /// the operator can repoint their provider without dropping a STOP in the gap.
    ///
    /// Either way both proofs are cleared — a new token has never been demonstrated to work, and treating it as
    /// proven would defeat the readiness hold entirely.
    /// </summary>
    public void MintInboundWebhookToken(string tokenHash, string tokenPrefix, DateTimeOffset nowUtc, TimeSpan? grace)
    {
        if (grace is { } window && InboundWebhookTokenHash is not null)
        {
            PreviousInboundWebhookTokenHash = InboundWebhookTokenHash;
            PreviousInboundWebhookTokenExpiresAtUtc = nowUtc + window;
        }
        else
        {
            PreviousInboundWebhookTokenHash = null;
            PreviousInboundWebhookTokenExpiresAtUtc = null;
        }

        InboundWebhookTokenHash = tokenHash;
        InboundWebhookTokenPrefix = tokenPrefix;
        InboundWebhookTokenMintedAtUtc = nowUtc;
        InboundCurrentTokenVerifiedAtUtc = null;
        InboundPreviousTokenSeenAtUtc = null;
    }

    /// <summary>End a rotation early — the operator confirms their provider is on the new URL.</summary>
    public void CompleteInboundWebhookTokenRotation()
    {
        PreviousInboundWebhookTokenHash = null;
        PreviousInboundWebhookTokenExpiresAtUtc = null;
    }

    /// <summary>Revoke every token for this org. The inbound endpoint then resolves nothing and drops silently.</summary>
    public void ClearInboundWebhookToken()
    {
        InboundWebhookTokenHash = null;
        InboundWebhookTokenPrefix = null;
        InboundWebhookTokenMintedAtUtc = null;
        PreviousInboundWebhookTokenHash = null;
        PreviousInboundWebhookTokenExpiresAtUtc = null;
        InboundCurrentTokenVerifiedAtUtc = null;
        InboundPreviousTokenSeenAtUtc = null;
    }

    /// <summary>Record that a correctly-signed request arrived on the current token — the readiness proof.</summary>
    public void MarkInboundCurrentTokenVerified(DateTimeOffset nowUtc) => InboundCurrentTokenVerifiedAtUtc ??= nowUtc;

    /// <summary>Record that a request arrived on the PREVIOUS token — the provider has not been repointed yet.</summary>
    public void MarkInboundPreviousTokenSeen(DateTimeOffset nowUtc) => InboundPreviousTokenSeenAtUtc = nowUtc;

    public void RecordInboundRejected(DateTimeOffset nowUtc)
    {
        InboundLastRejectedAtUtc = nowUtc;
        InboundRejectedCount++;
    }

    /// <summary>True while a rotated-away token is still inside its grace window. Lazy — evaluated on read.</summary>
    public bool IsInboundTokenRotating(DateTimeOffset nowUtc) =>
        PreviousInboundWebhookTokenHash is not null
        && PreviousInboundWebhookTokenExpiresAtUtc is { } expires && expires > nowUtc;

    /// <summary>
    /// The single definition of "this org can receive a STOP". Both send sites consult it, so the guarantee cannot
    /// hold in one place and not the other. A token that exists but has never carried a verified request does NOT
    /// count: an unconnected webhook is indistinguishable, from the subscriber's side, from having no webhook at all.
    /// </summary>
    public bool InboundReady => InboundWebhookTokenHash is not null && InboundCurrentTokenVerifiedAtUtc is not null;

    /// <summary>True once an inbound webhook token has been minted for this org's SMS channel.</summary>
    public bool HasInboundWebhookToken => !string.IsNullOrEmpty(InboundWebhookTokenHash);

    public void Enable() => Enabled = true;

    public void Disable() => Enabled = false;
}
