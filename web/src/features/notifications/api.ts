import { useMutation, useQuery, useQueryClient } from '@tanstack/react-query';
import { api } from '@/lib/api/client';

// Notification rules + delivery history for the Settings → Notifications console (docs/35-notifications.md §4.4).
// Kept-core: the dispatch spine ships in every edition. Wave 1 delivers the free-core Email channel.

/** A notification routing rule in the active organization. */
export interface NotificationRule {
  id: string;
  name: string;
  eventPattern: string;
  /** Advanced (notifications.routing): extra event globs this rule also matches, beyond the primary. */
  additionalPatterns: string[];
  /** Advanced (notifications.routing): Immediate, or a Daily/Weekly coalesced digest. */
  digestCadence: 'Immediate' | 'Daily' | 'Weekly';
  channel: 'Email' | 'Chat' | 'Sms';
  audience: 'OptedInUsers' | 'Role';
  audienceRef: string | null;
  /** Advanced (notifications.routing): quiet-hours window as "HH:mm:ss" in {@link quietHoursTimeZone}; null = off. */
  quietHoursStart: string | null;
  quietHoursEnd: string | null;
  /** IANA time zone the quiet window is interpreted in; null = no quiet hours. */
  quietHoursTimeZone: string | null;
  /** Advanced (notifications.routing): the ordered escalation chain (empty = none). */
  escalationSteps: EscalationStep[];
  enabled: boolean;
  createdAtUtc: string;
}

/** One step of a rule's escalation chain (read shape). */
export interface EscalationStep {
  stepOrder: number;
  delayMinutes: number;
  channel: 'Email' | 'Chat';
  audience: 'OptedInUsers' | 'Role';
  audienceRef: string | null;
}

/** One escalation step (write shape — the server assigns order by position). */
export interface EscalationStepInput {
  delayMinutes: number;
  channelKind: 'Email' | 'Chat';
  audience: 'OptedInUsers' | 'Role';
  audienceRef: string | null;
}

/** An active (unacknowledged) escalation alert — for the acknowledge UI. */
export interface ActiveEscalation {
  jobId: string;
  action: string;
  summary: string;
  escalationLevel: number;
  escalationStepCount: number;
  nextDueAtUtc: string | null;
  createdAtUtc: string;
}

/** A curated event pattern for the rule editor, grouped for the picker. */
export interface NotificationEventType {
  pattern: string;
  label: string;
  group: string;
  /** A literal critical action (docs/36 §6) — the only events an SMS rule may target. */
  isCritical: boolean;
}

/** One delivery — the org-visible outbox history. */
export interface NotificationDelivery {
  id: string;
  channel: string;
  recipient: string;
  subject: string;
  /**
   * `Unresolved` (SMS, W4) means the message was handed to the provider and its outcome was never learned — a timeout
   * or a provider 5xx. It is terminal and deliberately NOT retried: resending would charge the operator twice and text
   * a real person twice. The provider's status callback may later resolve it to Delivered or Failed.
   */
  status: 'Pending' | 'Delivered' | 'Failed' | 'Cancelled' | 'Unresolved';
  attempts: number;
  lastError: string | null;
  createdAtUtc: string;
  deliveredAtUtc: string | null;
}

export interface UpsertRuleInput {
  name: string;
  eventPattern: string;
  /** Advanced (notifications.routing): extra event globs; [] for a plain single-event rule. */
  additionalPatterns: string[];
  /** Advanced (notifications.routing): 'Immediate' | 'Daily' | 'Weekly'. */
  digestCadence: 'Immediate' | 'Daily' | 'Weekly';
  /** SMS (notifications.sms) is critical-only + immediate; escalation steps stay Email|Chat until Slice F. */
  channelKind: 'Email' | 'Chat' | 'Sms';
  audience: 'OptedInUsers' | 'Role';
  audienceRef: string | null;
  /** Advanced (notifications.routing): quiet-hours window ("HH:mm:ss") + IANA time zone; all null = off. */
  quietHoursStart: string | null;
  quietHoursEnd: string | null;
  quietHoursTimeZone: string | null;
  /** Advanced (notifications.routing): the escalation chain ([] = none). */
  escalationSteps: EscalationStepInput[];
}

/** Per-channel config status — never the destination URL (a bearer secret). */
export interface NotificationChannelConfig {
  channel: string;
  provider: string;
  configured: boolean;
  enabled: boolean;
}

export interface SetChatDestinationInput {
  provider: 'Slack' | 'Teams';
  destinationUrl: string;
}

/** SMS provider status — provider + configured + masked From + A2P attestation; NEVER the credential (write-only). docs/36 §5. */
export interface SmsProviderConfig {
  provider: string;
  configured: boolean;
  enabled: boolean;
  fromMasked: string | null;
  /** A2P/10DLC campaign attestation (docs/36 §4.6) — the pre-send gate that unlocks SMS sending. */
  a2pAttested: boolean;
  a2pAttestedAtUtc: string | null;
  a2pReference: string | null;
  /**
   * Inbound STOP/HELP webhook state (W2). Carries the token PREFIX and a token-FREE url prefix — never the token
   * itself, which is shown exactly once at mint and cannot be recovered.
   */
  inboundTokenPresent: boolean;
  inboundTokenPrefix: string | null;
  inboundTokenMintedAtUtc: string | null;
  inboundUrlPrefix: string | null;
  /** False when this installation has no public inbound URL configured — minting is refused until it does. */
  inboundEndpointConfigured: boolean;
  /** True once a correctly-signed request has ARRIVED on the current token. Sending holds until then. */
  inboundReady: boolean;
  inboundVerifiedAtUtc: string | null;
  inboundRotating: boolean;
  inboundPreviousExpiresAtUtc: string | null;
  /** Set when a request arrived on the OLD token — the provider has not been repointed yet. */
  inboundPreviousSeenAtUtc: string | null;
  inboundRejectedCount: number;
  inboundLastRejectedAtUtc: string | null;
}

/** The show-once mint result: the plaintext token and full URL appear here and nowhere else. */
export interface SmsInboundWebhookToken {
  token: string;
  inboundUrl: string | null;
  mintedAtUtc: string;
  previousExpiresAtUtc: string | null;
}

export interface SetSmsA2pAttestationInput {
  attested: boolean;
  reference: string | null;
}

export interface SetSmsProviderInput {
  /** Twilio-first (docs/36 §2); Vonage is an additive drop-in later. */
  provider: 'Twilio';
  accountSid: string;
  /** Write-only — never returned by the API; cleared from the form after save. */
  authToken: string;
  fromValue: string;
}

const rulesKey = ['notification-rules'] as const;

export function useNotificationRules() {
  return useQuery({ queryKey: rulesKey, queryFn: () => api.get<NotificationRule[]>('/api/v1/notifications/rules') });
}

export function useNotificationEventTypes() {
  return useQuery({
    queryKey: ['notification-event-types'] as const,
    queryFn: () => api.get<NotificationEventType[]>('/api/v1/notifications/event-types'),
  });
}

export function useNotificationDeliveries() {
  return useQuery({
    queryKey: ['notification-deliveries'] as const,
    queryFn: () => api.get<NotificationDelivery[]>('/api/v1/notifications/deliveries'),
  });
}

export function useNotificationChannelConfigs() {
  return useQuery({
    queryKey: ['notification-channel-configs'] as const,
    queryFn: () => api.get<NotificationChannelConfig[]>('/api/v1/notifications/channel-configs'),
  });
}

function useRulesMutation<TArgs, TResult>(fn: (args: TArgs) => Promise<TResult>) {
  const qc = useQueryClient();
  return useMutation({
    mutationFn: fn,
    onSuccess: () => {
      qc.invalidateQueries({ queryKey: rulesKey });
      qc.invalidateQueries({ queryKey: ['notification-deliveries'] });
    },
  });
}

export const useCreateNotificationRule = () =>
  useRulesMutation((input: UpsertRuleInput) => api.post<string>('/api/v1/notifications/rules', input));

export const useUpdateNotificationRule = () =>
  useRulesMutation(({ id, ...input }: UpsertRuleInput & { id: string }) => api.put<void>(`/api/v1/notifications/rules/${id}`, input));

export const useSetNotificationRuleStatus = () =>
  useRulesMutation(({ id, enabled }: { id: string; enabled: boolean }) => api.post<void>(`/api/v1/notifications/rules/${id}/status`, { enabled }));

export const useTestNotificationRule = () =>
  useRulesMutation((id: string) => api.post<void>(`/api/v1/notifications/rules/${id}/test`));

export const useDeleteNotificationRule = () =>
  useRulesMutation((id: string) => api.delete<void>(`/api/v1/notifications/rules/${id}`));

export function useSetChatDestination() {
  const qc = useQueryClient();
  return useMutation({
    mutationFn: (input: SetChatDestinationInput) => api.put<void>('/api/v1/notifications/channel-configs/chat', input),
    onSuccess: () => qc.invalidateQueries({ queryKey: ['notification-channel-configs'] }),
  });
}

const smsConfigKey = ['notification-sms-config'] as const;

/** The org's SMS provider status (gated on notifications.sms server-side). */
export function useSmsProviderConfig() {
  return useQuery({
    queryKey: smsConfigKey,
    queryFn: () => api.get<SmsProviderConfig>('/api/v1/notifications/channel-configs/sms'),
  });
}

export function useSetSmsProvider() {
  const qc = useQueryClient();
  return useMutation({
    mutationFn: (input: SetSmsProviderInput) => api.put<void>('/api/v1/notifications/channel-configs/sms', input),
    onSuccess: () => qc.invalidateQueries({ queryKey: smsConfigKey }),
  });
}

/** Record/revoke the org's A2P/10DLC campaign attestation (docs/36 §4.6). */
export function useSetSmsA2pAttestation() {
  const qc = useQueryClient();
  return useMutation({
    mutationFn: (input: SetSmsA2pAttestationInput) => api.put<void>('/api/v1/notifications/channel-configs/sms/a2p', input),
    onSuccess: () => qc.invalidateQueries({ queryKey: smsConfigKey }),
  });
}

/**
 * Mint (or rotate) the inbound webhook token. The response is the ONLY time the plaintext is available — it is not
 * cached, and the caller must show it once and then discard it.
 */
export function useMintSmsInboundWebhookToken() {
  const qc = useQueryClient();
  return useMutation({
    mutationFn: (rotate: boolean) =>
      api.post<SmsInboundWebhookToken>('/api/v1/notifications/channel-configs/sms/inbound-token', { rotate }),
    onSuccess: () => qc.invalidateQueries({ queryKey: smsConfigKey }),
  });
}

/** End a rotation early, once the provider is confirmed on the new URL. */
export function useCompleteSmsInboundTokenRotation() {
  const qc = useQueryClient();
  return useMutation({
    mutationFn: () =>
      api.post<void>('/api/v1/notifications/channel-configs/sms/inbound-token/complete-rotation', {}),
    onSuccess: () => qc.invalidateQueries({ queryKey: smsConfigKey }),
  });
}

/** Revoke every inbound token — the leak response. Sending holds until a new one is minted AND demonstrated. */
export function useClearSmsInboundWebhookToken() {
  const qc = useQueryClient();
  return useMutation({
    mutationFn: () => api.delete<void>('/api/v1/notifications/channel-configs/sms/inbound-token'),
    onSuccess: () => qc.invalidateQueries({ queryKey: smsConfigKey }),
  });
}

/**
 * One row of the append-only SMS suppression ledger. The number arrives MASKED — the console proves that a suppression
 * exists and when, it is not a directory of the org's numbers. `isActive` is the authoritative "currently opted out";
 * a cleared row is a state transition, never a deletion (docs/36 §4.3).
 */
export interface SmsOptOut {
  id: string;
  phoneMasked: string;
  sender: string | null;
  source: 'StopKeyword' | 'StartKeyword' | 'CarrierError' | 'OperatorRequest';
  keyword: string | null;
  clearedKeyword: string | null;
  reason: string | null;
  suppressedAtUtc: string;
  clearedAtUtc: string | null;
  isActive: boolean;
}

export interface RecordSmsOptOutResult {
  /** False when the number was already actively suppressed — a no-op, reported honestly rather than as a new record. */
  appended: boolean;
  phoneMasked: string;
}

const smsOptOutsKey = (phone: string) => ['notification-sms-opt-outs', phone] as const;

/**
 * The org's SMS suppression ledger. `phone` is an EXACT lookup, not a prefix search — with a masked display, a partial
 * match would be a number-harvesting oracle (docs/36 §4.3). Server-side it is gated on notifications.manage only, NOT
 * the SMS entitlement: the duty not to text someone who revoked consent outlives the subscription.
 */
export function useSmsOptOuts(phone: string) {
  return useQuery({
    queryKey: smsOptOutsKey(phone),
    queryFn: () =>
      api.get<SmsOptOut[]>(
        `/api/v1/notifications/sms/opt-outs${phone ? `?phone=${encodeURIComponent(phone)}` : ''}`,
      ),
  });
}

/**
 * Record an opt-out that arrived off-channel (phone, email, ticket). The number goes in the BODY, never the query, so it
 * cannot reach a trace or an access log. There is deliberately no inverse mutation.
 */
export function useRecordSmsOptOut() {
  const qc = useQueryClient();
  return useMutation({
    mutationFn: (input: { phone: string; reason: string | null }) =>
      api.post<RecordSmsOptOutResult>('/api/v1/notifications/sms/opt-outs', input),
    onSuccess: () => qc.invalidateQueries({ queryKey: ['notification-sms-opt-outs'] }),
  });
}

const activeEscalationsKey = ['notification-active-escalations'] as const;

/** The org's active (unacknowledged) escalation alerts — gated on notifications.acknowledge server-side. */
export function useActiveEscalations() {
  return useQuery({
    queryKey: activeEscalationsKey,
    queryFn: () => api.get<ActiveEscalation[]>('/api/v1/notifications/alerts/active'),
  });
}

export function useAcknowledgeAlert() {
  const qc = useQueryClient();
  return useMutation({
    mutationFn: (jobId: string) => api.post<void>(`/api/v1/notifications/alerts/${jobId}/acknowledge`),
    onSuccess: () => qc.invalidateQueries({ queryKey: activeEscalationsKey }),
  });
}
