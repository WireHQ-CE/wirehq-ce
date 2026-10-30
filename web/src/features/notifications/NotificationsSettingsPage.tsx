import { useState, type FormEvent } from 'react';
import { PageHeader } from '@/components/layout/AppShell';
import { Card, CardContent } from '@/components/ui/card';
import { Button } from '@/components/ui/button';
import { Input, Field } from '@/components/ui/input';
import { Select } from '@/components/ui/select';
import { Dialog } from '@/components/ui/dialog';
import { useToast } from '@/components/ui/toast';
import { ApiError } from '@/lib/api/client';
import { useAuthStore } from '@/stores/auth-store';
import { useOrgRoles } from '@/features/roles/api';
import {
  useNotificationRules,
  useNotificationEventTypes,
  useNotificationDeliveries,
  useNotificationChannelConfigs,
  useCreateNotificationRule,
  useUpdateNotificationRule,
  useSetNotificationRuleStatus,
  useTestNotificationRule,
  useDeleteNotificationRule,
  useSetChatDestination,
  useActiveEscalations,
  useAcknowledgeAlert,
  useSmsProviderConfig,
  useSetSmsProvider,
  useMintSmsInboundWebhookToken,
  useCompleteSmsInboundTokenRotation,
  useClearSmsInboundWebhookToken,
  useSmsOptOuts,
  useRecordSmsOptOut,
  type SmsProviderConfig,
  type SmsInboundWebhookToken,
  type SmsOptOut,
  useSetSmsA2pAttestation,
  type NotificationRule,
  type NotificationDelivery,
  type EscalationStepInput,
} from './api';

const DELIVERY_TONE: Record<NotificationDelivery['status'], string> = {
  Delivered: 'bg-success-500/15 text-success-700 dark:text-success-400',
  Pending: 'bg-gold-500/15 text-gold-700 dark:text-gold-400',
  Failed: 'bg-danger-500/15 text-danger-600 dark:text-danger-400',
  Cancelled: 'bg-ink-500/15 text-ink-400',
  // Amber, not red: nothing is known to have gone wrong. It may well have arrived.
  Unresolved: 'bg-gold-500/15 text-gold-700 dark:text-gold-400',
};

/** What a status means in the operator's terms, where the word alone would mislead. */
const DELIVERY_LABEL: Partial<Record<NotificationDelivery['status'], string>> = {
  Unresolved: 'Possibly sent',
};

function formatDate(iso: string): string {
  return new Date(iso).toLocaleString(undefined, { dateStyle: 'medium', timeStyle: 'short' });
}

export function NotificationsSettingsPage() {
  const hasPermission = useAuthStore((s) => s.hasPermission);
  const hasFeature = useAuthStore((s) => s.hasFeature);
  const canManage = hasPermission('notifications.manage');
  const canAcknowledge = hasPermission('notifications.acknowledge');
  const canChat = hasFeature('notifications.chat');
  const canSms = hasFeature('notifications.sms');
  const { data: rules, isLoading } = useNotificationRules();
  const toast = useToast();

  const test = useTestNotificationRule();
  const setStatus = useSetNotificationRuleStatus();
  const del = useDeleteNotificationRule();

  // editing: undefined = closed, null = create, NotificationRule = edit.
  const [editing, setEditing] = useState<NotificationRule | null | undefined>(undefined);

  if (!canManage) {
    return (
      <>
        <PageHeader title="Notifications" subtitle="Get notified when things happen in your organization." />
        <Card className="max-w-2xl">
          <CardContent>
            <p className="text-sm text-ink-400">
              You need the <span className="font-medium text-ink-200">Manage notifications</span> permission to
              configure notification rules. Ask an organization owner or admin.
            </p>
          </CardContent>
        </Card>
      </>
    );
  }

  const fail = (e: unknown, fallback: string) => toast(e instanceof ApiError ? e.message : fallback, 'error');

  const onTest = (rule: NotificationRule) =>
    test.mutate(rule.id, { onSuccess: () => toast('Test notification sent to your email.'), onError: (e) => fail(e, 'Could not send a test.') });

  const onToggle = (rule: NotificationRule) =>
    setStatus.mutate(
      { id: rule.id, enabled: !rule.enabled },
      { onSuccess: () => toast(rule.enabled ? 'Rule disabled.' : 'Rule enabled.'), onError: (e) => fail(e, 'Could not change the rule.') },
    );

  const onDelete = (rule: NotificationRule) => {
    if (!window.confirm(`Delete the rule “${rule.name}”?`)) return;
    del.mutate(rule.id, { onSuccess: () => toast('Rule deleted.'), onError: (e) => fail(e, 'Could not delete the rule.') });
  };

  return (
    <>
      <PageHeader
        title="Notifications"
        subtitle={
          canChat && canSms
            ? 'Email, chat, or text your team when a matching event happens.'
            : canSms
            ? 'Email or text your team when a matching event happens.'
            : canChat
            ? 'Email or chat your team when a matching event happens.'
            : 'Email your team when a matching event happens.'
        }
        action={<Button onClick={() => setEditing(null)}>New rule</Button>}
      />

      {canAcknowledge && <ActiveAlertsCard />}

      {isLoading ? (
        <p className="text-sm text-ink-400">Loading…</p>
      ) : (
        <div className="max-w-3xl space-y-6">
          <Card>
            <CardContent>
              {rules && rules.length > 0 ? (
                <div className="divide-y divide-ink-800">
                  {rules.map((rule) => (
                    <div key={rule.id} className="flex flex-col gap-2 py-3 first:pt-0 last:pb-0 sm:flex-row sm:items-start sm:justify-between sm:gap-4">
                      <div className="min-w-0">
                        <div className="flex items-center gap-2">
                          <p className="truncate text-sm font-medium text-ink-100">{rule.name}</p>
                          {!rule.enabled && <span className="shrink-0 rounded-full bg-ink-500/15 px-2 py-0.5 text-xs font-medium text-ink-400">Disabled</span>}
                        </div>
                        <p className="mt-0.5 text-xs text-ink-500">
                          <span className="font-mono">{rule.eventPattern}</span>
                          {rule.additionalPatterns.length > 0 && (
                            <span className="text-ink-400"> +{rule.additionalPatterns.length}</span>
                          )}{' '}
                          → {rule.channel} · {rule.audience === 'Role' ? 'role' : 'opted-in users'}
                        </p>
                      </div>
                      <div className="flex flex-wrap justify-end gap-2">
                        <Button variant="ghost" onClick={() => onTest(rule)}>Test</Button>
                        <Button variant="ghost" onClick={() => setEditing(rule)}>Edit</Button>
                        <Button variant="ghost" onClick={() => onToggle(rule)}>{rule.enabled ? 'Disable' : 'Enable'}</Button>
                        <Button variant="ghost" onClick={() => onDelete(rule)}>Delete</Button>
                      </div>
                    </div>
                  ))}
                </div>
              ) : (
                <p className="text-sm text-ink-400">No notification rules yet. Create one to email your team on matching events.</p>
              )}
            </CardContent>
          </Card>

          {canChat && <ChatDestinationCard />}

          {canSms && <SmsProviderCard />}

          <DeliveryHistory />
        </div>
      )}

      {editing !== undefined && <RuleEditorDialog rule={editing} onClose={() => setEditing(undefined)} />}
    </>
  );
}

function DeliveryHistory() {
  const { data: deliveries } = useNotificationDeliveries();
  if (!deliveries || deliveries.length === 0) return null;

  return (
    <Card>
      <CardContent>
        <h2 className="mb-3 text-sm font-semibold text-ink-200">Recent deliveries</h2>
        <div className="divide-y divide-ink-800">
          {deliveries.map((d) => (
            <div key={d.id} className="flex items-center justify-between gap-4 py-2 first:pt-0 last:pb-0">
              <div className="min-w-0">
                <p className="truncate text-sm text-ink-200">{d.subject}</p>
                <p className="truncate text-xs text-ink-500">{d.recipient} · {formatDate(d.createdAtUtc)}</p>
                {d.lastError && <p className="truncate text-xs text-danger-500">{d.lastError}</p>}
              </div>
              <span className={`shrink-0 rounded-full px-2 py-0.5 text-xs font-medium ${DELIVERY_TONE[d.status]}`}>{DELIVERY_LABEL[d.status] ?? d.status}</span>
            </div>
          ))}
        </div>
      </CardContent>
    </Card>
  );
}

function ChatDestinationCard() {
  const { data: configs } = useNotificationChannelConfigs();
  const setDestination = useSetChatDestination();
  const toast = useToast();
  const chat = configs?.find((c) => c.channel === 'Chat');

  const [provider, setProvider] = useState<'Slack' | 'Teams'>('Slack');
  const [url, setUrl] = useState('');

  const save = (e: FormEvent) => {
    e.preventDefault();
    setDestination.mutate(
      { provider, destinationUrl: url.trim() },
      {
        onSuccess: () => { toast('Chat destination saved.'); setUrl(''); },
        onError: (err) => toast(err instanceof ApiError ? err.message : 'Could not save the destination.', 'error'),
      },
    );
  };

  return (
    <Card>
      <CardContent>
        <h2 className="mb-1 text-sm font-semibold text-ink-200">Chat destination</h2>
        <p className="mb-3 text-xs text-ink-500">
          {chat?.configured
            ? `Configured — ${chat.provider} (the webhook URL is hidden). Re-paste to change it.`
            : 'Paste a Microsoft Teams or Slack incoming-webhook URL to enable Chat rules.'}
        </p>
        <form onSubmit={save} className="flex flex-col gap-3 sm:flex-row sm:items-end">
          <Field label="Provider" htmlFor="chat-provider">
            <Select id="chat-provider" value={provider} onChange={(e) => setProvider(e.target.value as 'Slack' | 'Teams')}>
              <option value="Slack">Slack</option>
              <option value="Teams">Microsoft Teams</option>
            </Select>
          </Field>
          <Field label="Incoming webhook URL" htmlFor="chat-url">
            <Input id="chat-url" type="url" value={url} onChange={(e) => setUrl(e.target.value)} placeholder="https://hooks.slack.com/services/…" />
          </Field>
          <Button type="submit" disabled={setDestination.isPending || !url.trim()}>Save</Button>
        </form>
      </CardContent>
    </Card>
  );
}

function SmsProviderCard() {
  const { data: config } = useSmsProviderConfig();
  const setProvider = useSetSmsProvider();
  const setA2p = useSetSmsA2pAttestation();
  const toast = useToast();
  const [reference, setReference] = useState('');

  // Twilio-first (docs/36 §2). A select keeps room for Vonage without a layout change.
  const [provider] = useState<'Twilio'>('Twilio');
  const [accountSid, setAccountSid] = useState('');
  const [authToken, setAuthToken] = useState('');
  const [fromValue, setFromValue] = useState('');

  const save = (e: FormEvent) => {
    e.preventDefault();
    setProvider.mutate(
      { provider, accountSid: accountSid.trim(), authToken: authToken.trim(), fromValue: fromValue.trim() },
      {
        onSuccess: () => {
          // The credential is write-only — clear every input so the secret is never left in the DOM.
          toast('SMS provider saved.');
          setAccountSid('');
          setAuthToken('');
          setFromValue('');
        },
        onError: (err) => toast(err instanceof ApiError ? err.message : 'Could not save the SMS provider.', 'error'),
      },
    );
  };

  const valid = accountSid.trim() && authToken.trim() && fromValue.trim();

  return (
    <Card>
      <CardContent>
        <div className="mb-1 flex items-center gap-2">
          <h2 className="text-sm font-semibold text-ink-200">SMS provider</h2>
          <span className="rounded-full bg-ink-500/15 px-2 py-0.5 text-xs font-medium text-ink-400">Coming soon</span>
        </div>
        <p className="mb-3 text-xs text-ink-500">
          {config?.configured
            ? `Configured — ${config.provider}${config.fromMasked ? ` · from ${config.fromMasked}` : ''} (the credential is hidden). Re-enter to change it.`
            : 'Connect your own Twilio account to send critical alerts over SMS. Your credentials, your number, your carrier bill.'}
        </p>
        <form onSubmit={save} className="space-y-3">
          <div className="grid gap-3 sm:grid-cols-2">
            <Field label="Provider" htmlFor="sms-provider">
              <Select id="sms-provider" value={provider} disabled>
                <option value="Twilio">Twilio</option>
              </Select>
            </Field>
            <Field label="From number" htmlFor="sms-from">
              <Input id="sms-from" value={fromValue} onChange={(e) => setFromValue(e.target.value)} placeholder="+15551234567 or a Messaging Service SID" />
            </Field>
            <Field label="Account SID" htmlFor="sms-sid">
              <Input id="sms-sid" value={accountSid} onChange={(e) => setAccountSid(e.target.value)} placeholder="ACxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxx" autoComplete="off" />
            </Field>
            <Field label="Auth token" htmlFor="sms-token">
              <Input id="sms-token" type="password" value={authToken} onChange={(e) => setAuthToken(e.target.value)} placeholder="Write-only — never shown again" autoComplete="off" />
            </Field>
          </div>
          <div className="flex justify-end">
            <Button type="submit" disabled={setProvider.isPending || !valid}>Save</Button>
          </div>
        </form>

        {config?.configured && (
          <div className="mt-4 border-t border-ink-800 pt-4">
            <h3 className="mb-1 text-xs font-semibold text-ink-300">A2P / 10DLC attestation</h3>
            {config.a2pAttested ? (
              <div className="flex items-start justify-between gap-3">
                <p className="text-xs text-ink-500">
                  Attested{config.a2pAttestedAtUtc ? ` ${formatDate(config.a2pAttestedAtUtc)}` : ''}
                  {config.a2pReference ? ` · ${config.a2pReference}` : ''}. SMS sending is unlocked once the module is live.
                </p>
                <Button
                  type="button"
                  variant="ghost"
                  onClick={() => setA2p.mutate({ attested: false, reference: null }, {
                    onSuccess: () => toast('A2P attestation revoked.'),
                    onError: (e) => toast(e instanceof ApiError ? e.message : 'Could not update the attestation.', 'error'),
                  })}
                >
                  Revoke
                </Button>
              </div>
            ) : (
              <div className="space-y-2">
                <p className="text-xs text-ink-500">
                  Before WireHQ can send, confirm your 10DLC/A2P campaign is registered and approved with your provider,
                  and that WireHQ&rsquo;s critical-alert messages are covered by your registered sample templates. Alert
                  content is dynamic (event-driven).
                </p>
                <Field label="Campaign / brand reference (optional)" htmlFor="sms-a2p-ref">
                  <Input id="sms-a2p-ref" value={reference} onChange={(e) => setReference(e.target.value)} placeholder="Your A2P campaign or brand id" maxLength={128} />
                </Field>
                <div className="flex justify-end">
                  <Button
                    type="button"
                    onClick={() => setA2p.mutate({ attested: true, reference: reference.trim() || null }, {
                      onSuccess: () => { toast('A2P campaign attested.'); setReference(''); },
                      onError: (e) => toast(e instanceof ApiError ? e.message : 'Could not save the attestation.', 'error'),
                    })}
                    disabled={setA2p.isPending}
                  >
                    I attest
                  </Button>
                </div>
              </div>
            )}
          </div>
        )}

        {config?.configured && <InboundWebhookSection config={config} />}

        {/* Always rendered, never gated on `configured` — a revocation can arrive before a provider is connected, and
            the ledger must stay readable after one is removed. It is also the org's compliance record, not a feature. */}
        <OptOutLedgerSection />
      </CardContent>
    </Card>
  );
}

/** Matches the server's ListSmsOptOutsQuery default, so "am I looking at a capped list?" is answerable client-side. */
const OPT_OUT_PAGE_SIZE = 50;

const OPT_OUT_SOURCE_LABEL: Record<SmsOptOut['source'], string> = {
  StopKeyword: 'Replied STOP',
  StartKeyword: 'Replied START',
  CarrierError: 'Carrier opt-out',
  OperatorRequest: 'Recorded by your team',
};

/**
 * The opt-out ledger: who has asked not to be texted, and when. Numbers are masked — this answers "is this person opted
 * out?" and "when did that happen?", which is what an operator needs; it is not a list of the org's phone numbers, so
 * search is an exact lookup you can only run on a number you already have.
 *
 * There is no control here to lift a suppression. That is the point of the feature, not an omission.
 */
function OptOutLedgerSection() {
  const [search, setSearch] = useState('');
  const [applied, setApplied] = useState('');
  const { data: optOuts, isLoading, error } = useSmsOptOuts(applied);
  const record = useRecordSmsOptOut();
  const toast = useToast();
  const [recordPhone, setRecordPhone] = useState('');
  const [recordReason, setRecordReason] = useState('');

  const submitSearch = (e: FormEvent) => {
    e.preventDefault();
    setApplied(search.trim());
  };

  const submitRecord = (e: FormEvent) => {
    e.preventDefault();
    record.mutate(
      { phone: recordPhone.trim(), reason: recordReason.trim() || null },
      {
        onSuccess: (result) => {
          toast(
            result.appended
              ? `Opt-out recorded for ${result.phoneMasked}. WireHQ will not text this number.`
              : `${result.phoneMasked} was already opted out — nothing changed.`,
          );
          setRecordPhone('');
          setRecordReason('');
        },
        onError: (err) =>
          toast(err instanceof ApiError ? err.message : 'Could not record the opt-out.', 'error'),
      },
    );
  };

  return (
    <div className="mt-6 border-t border-ink-800 pt-4">
      <h3 className="mb-1 text-xs font-semibold text-ink-300">Opt-outs</h3>
      <p className="mb-3 text-xs text-ink-400">
        Everyone who has asked not to receive SMS from you. WireHQ refuses to text these numbers, and the record is kept
        permanently as your proof — an opt-out is never deleted, only replaced if the person texts START themselves.
        Numbers are shown partly hidden; search for a full number to check a specific one.
      </p>

      <form onSubmit={submitSearch} className="mb-3 flex flex-wrap items-end gap-2">
        <div className="min-w-56 flex-1">
          <Field label="Check a number" htmlFor="sms-opt-out-search">
            <Input
              id="sms-opt-out-search"
              value={search}
              onChange={(e) => setSearch(e.target.value)}
              placeholder="+447700900123"
              autoComplete="off"
            />
          </Field>
        </div>
        <Button type="submit" variant="ghost">Check</Button>
        {applied && (
          <Button type="button" variant="ghost" onClick={() => { setSearch(''); setApplied(''); }}>
            Show all
          </Button>
        )}
      </form>

      {error instanceof ApiError && <p className="mb-3 text-xs text-danger-400">{error.message}</p>}

      {isLoading ? (
        <p className="text-xs text-ink-500">Loading…</p>
      ) : !optOuts?.length ? (
        <p className="text-xs text-ink-500">
          {applied ? 'That number has never opted out.' : 'Nobody has opted out.'}
        </p>
      ) : (
        <ul className="mb-4 space-y-2">
          {optOuts.map((o) => (
            <li key={o.id} className="rounded border border-ink-800 p-2 text-xs">
              <div className="flex flex-wrap items-center gap-2">
                <span className="font-medium text-ink-100">{o.phoneMasked}</span>
                <span
                  className={
                    o.isActive
                      ? 'rounded-full bg-danger-500/15 px-2 py-0.5 text-danger-600 dark:text-danger-400'
                      : 'rounded-full bg-ink-500/15 px-2 py-0.5 text-ink-400'
                  }
                >
                  {o.isActive ? 'Opted out' : 'Opted back in'}
                </span>
                <span className="text-ink-500">{OPT_OUT_SOURCE_LABEL[o.source]}</span>
              </div>
              <p className="mt-1 text-ink-500">
                {formatDate(o.suppressedAtUtc)}
                {o.clearedAtUtc ? ` · opted back in ${formatDate(o.clearedAtUtc)}` : ''}
                {o.sender ? ` · sender ${o.sender}` : ''}
              </p>
              {o.reason && <p className="mt-1 text-ink-400">{o.reason}</p>}
            </li>
          ))}
        </ul>
      )}

      {/* Say when the list is capped rather than letting a truncated page read as "this is everyone". */}
      {!applied && optOuts && optOuts.length >= OPT_OUT_PAGE_SIZE && (
        <p className="mb-4 text-xs text-ink-500">
          Showing the {OPT_OUT_PAGE_SIZE} most recent. To check one particular number, search for it above.
        </p>
      )}

      <div className="mt-4 border-t border-ink-800 pt-3">
        <h4 className="mb-1 text-xs font-semibold text-ink-300">Record an opt-out</h4>
        <p className="mb-2 text-xs text-ink-500">
          If someone asked you to stop by phone, email or in person, record it here — a request to stop counts however it
          reaches you. <strong className="text-ink-300">This cannot be undone from here:</strong> only the person
          themselves can opt back in, by texting START to your number.
        </p>
        <form onSubmit={submitRecord} className="space-y-2">
          <div className="grid gap-2 sm:grid-cols-2">
            <Field label="Phone number" htmlFor="sms-opt-out-phone">
              <Input
                id="sms-opt-out-phone"
                value={recordPhone}
                onChange={(e) => setRecordPhone(e.target.value)}
                placeholder="+447700900123 (include the country code)"
                autoComplete="off"
              />
            </Field>
            <Field label="How they asked (optional)" htmlFor="sms-opt-out-reason">
              <Input
                id="sms-opt-out-reason"
                value={recordReason}
                onChange={(e) => setRecordReason(e.target.value)}
                placeholder="e.g. phoned support, ticket 1234"
                maxLength={256}
              />
            </Field>
          </div>
          <div className="flex justify-end">
            <Button type="submit" disabled={record.isPending || !recordPhone.trim()}>
              Record opt-out
            </Button>
          </div>
        </form>
      </div>
    </div>
  );
}

/**
 * The inbound STOP/HELP webhook: mint the token, show the paste-ready URL once, and report whether the endpoint has
 * actually been proven to work. That last part is the point — sending is HELD until a correctly-signed request has
 * arrived, because an unconnected webhook is, from the subscriber's side, the same as having no webhook at all.
 */
function InboundWebhookSection({ config }: { config: SmsProviderConfig }) {
  const mint = useMintSmsInboundWebhookToken();
  const completeRotation = useCompleteSmsInboundTokenRotation();
  const revoke = useClearSmsInboundWebhookToken();
  const toast = useToast();
  const [minted, setMinted] = useState<SmsInboundWebhookToken | null>(null);

  return (
    <div className="mt-6 border-t border-ink-800 pt-4">
      <h3 className="mb-1 text-xs font-semibold text-ink-300">Inbound STOP/HELP webhook</h3>

      {!config.inboundEndpointConfigured ? (
        <p className="text-xs text-ink-400">
          This installation has no public inbound webhook URL configured, so a token cannot be issued yet. Ask your
          administrator to set <code className="text-ink-300">Notifications:Sms:InboundWebhookBaseUrl</code> to the
          public https address of this install.
        </p>
      ) : (
        <>
          <p className="mb-3 text-xs text-ink-400">
            Your provider forwards incoming messages here so WireHQ can honour STOP. Until a real signed request
            arrives on this URL, SMS sending stays on hold — a webhook that has never been reached is indistinguishable
            from one that does not exist.
          </p>

          {minted && (
            <div className="mb-3 rounded border border-amber-700/50 bg-amber-950/30 p-3">
              <p className="mb-2 text-xs font-medium text-amber-200">
                Copy this URL into your provider now — it is shown once and cannot be retrieved again.
              </p>
              <code className="block break-all rounded bg-ink-900 p-2 text-xs text-ink-100">{minted.inboundUrl}</code>
              <Button variant="ghost" className="mt-2" onClick={() => setMinted(null)}>
                I have saved it
              </Button>
            </div>
          )}

          <dl className="mb-3 space-y-1 text-xs">
            <div className="flex gap-2">
              <dt className="text-ink-400">Status</dt>
              <dd className={config.inboundReady ? 'text-emerald-400' : 'text-amber-400'}>
                {config.inboundReady
                  ? `Verified${config.inboundVerifiedAtUtc ? ` ${formatDate(config.inboundVerifiedAtUtc)}` : ''} — STOP messages are reaching WireHQ`
                  : config.inboundTokenPresent
                    ? 'Waiting for the first message — send a STOP to your number to confirm the webhook works'
                    : 'No token issued yet'}
              </dd>
            </div>
            {config.inboundTokenPresent && (
              <div className="flex gap-2">
                <dt className="text-ink-400">Token</dt>
                <dd className="text-ink-200">
                  {config.inboundUrlPrefix}
                  {config.inboundTokenPrefix}…
                  {config.inboundTokenMintedAtUtc ? ` · issued ${formatDate(config.inboundTokenMintedAtUtc)}` : ''}
                </dd>
              </div>
            )}
            {config.inboundRejectedCount > 0 && (
              <div className="flex gap-2">
                <dt className="text-ink-400">Rejected</dt>
                <dd className="text-ink-200">
                  {config.inboundRejectedCount} request{config.inboundRejectedCount === 1 ? '' : 's'} failed signature
                  checks{config.inboundLastRejectedAtUtc ? `, last ${formatDate(config.inboundLastRejectedAtUtc)}` : ''}
                </dd>
              </div>
            )}
          </dl>

          {config.inboundRotating && (
            <div className="mb-3 rounded border border-ink-700 bg-ink-900/50 p-3 text-xs">
              <p className="text-ink-300">
                The previous URL still works until{' '}
                {config.inboundPreviousExpiresAtUtc ? formatDate(config.inboundPreviousExpiresAtUtc) : 'it expires'}, so
                nothing is lost while you update your provider.
                {config.inboundPreviousSeenAtUtc
                  ? ' Messages are still arriving on the old URL — your provider has not been updated yet.'
                  : ''}
              </p>
              <Button
                variant="ghost"
                className="mt-2"
                onClick={() => completeRotation.mutate(undefined, { onSuccess: () => toast('Old URL closed.') })}
                disabled={completeRotation.isPending}
              >
                I have updated my provider — close the old URL now
              </Button>
            </div>
          )}

          <div className="flex flex-wrap gap-2">
            <Button
              onClick={() =>
                mint.mutate(config.inboundTokenPresent, {
                  onSuccess: (result) => {
                    setMinted(result);
                    toast(config.inboundTokenPresent ? 'New URL issued.' : 'Webhook URL issued.');
                  },
                })
              }
              disabled={mint.isPending}
            >
              {config.inboundTokenPresent ? 'Issue a new URL' : 'Get my webhook URL'}
            </Button>
            {config.inboundTokenPresent && (
              <Button
                variant="ghost"
                onClick={() => revoke.mutate(undefined, { onSuccess: () => toast('Webhook URL revoked.') })}
                disabled={revoke.isPending}
              >
                Revoke
              </Button>
            )}
          </div>

          {config.inboundTokenPresent && (
            <p className="mt-2 text-xs text-ink-500">
              Revoking stops SMS sending until a new URL is issued and confirmed — no inbound webhook means no way to
              receive a STOP.
            </p>
          )}
        </>
      )}
    </div>
  );
}

function ActiveAlertsCard() {
  const { data: alerts } = useActiveEscalations();
  const acknowledge = useAcknowledgeAlert();
  const toast = useToast();

  if (!alerts || alerts.length === 0) {
    return null;
  }

  const onAcknowledge = (jobId: string) =>
    acknowledge.mutate(jobId, {
      onSuccess: () => toast('Alert acknowledged — escalation stopped.'),
      onError: (e) => toast(e instanceof ApiError ? e.message : 'Could not acknowledge the alert.', 'error'),
    });

  return (
    <Card className="mb-6 max-w-3xl border-gold-500/40">
      <CardContent>
        <h2 className="mb-1 text-sm font-semibold text-ink-100">Active escalations</h2>
        <p className="mb-3 text-xs text-ink-500">Acknowledge to stop an alert escalating to the next step.</p>
        <ul className="space-y-2">
          {alerts.map((a) => (
            <li key={a.jobId} className="flex flex-wrap items-center justify-between gap-2 rounded-md border border-ink-800 p-2">
              <div className="min-w-0">
                <p className="truncate text-sm text-ink-200">{a.summary}</p>
                <p className="text-xs text-ink-500">
                  {a.action} · step {a.escalationLevel} of {a.escalationStepCount} · started {formatDate(a.createdAtUtc)}
                </p>
              </div>
              <Button variant="secondary" onClick={() => onAcknowledge(a.jobId)} disabled={acknowledge.isPending}>
                Acknowledge
              </Button>
            </li>
          ))}
        </ul>
      </CardContent>
    </Card>
  );
}

function RuleEditorDialog({ rule, onClose }: { rule: NotificationRule | null; onClose: () => void }) {
  const isEdit = rule !== null;
  const { data: eventTypes } = useNotificationEventTypes();
  const { data: roles } = useOrgRoles();
  const create = useCreateNotificationRule();
  const update = useUpdateNotificationRule();
  const toast = useToast();

  const hasFeature = useAuthStore((s) => s.hasFeature);
  const canChat = hasFeature('notifications.chat');
  const canSms = hasFeature('notifications.sms');
  const canRoute = hasFeature('notifications.routing');
  const [name, setName] = useState(rule?.name ?? '');
  const [eventPattern, setEventPattern] = useState(rule?.eventPattern ?? '');
  const [additionalPatterns, setAdditionalPatterns] = useState<string[]>(rule?.additionalPatterns ?? []);
  const [digestCadence, setDigestCadence] = useState<'Immediate' | 'Daily' | 'Weekly'>(rule?.digestCadence ?? 'Immediate');
  const [channelKind, setChannelKind] = useState<'Email' | 'Chat' | 'Sms'>(
    rule?.channel === 'Chat' ? 'Chat' : rule?.channel === 'Sms' ? 'Sms' : 'Email');
  // SMS is critical-only + immediate (docs/36 §6): the editor offers only literal critical events and hides digests,
  // quiet hours, extra patterns, and escalation — the server enforces the same in Create AND Update.
  const isSms = channelKind === 'Sms';
  const criticalEvents = (eventTypes ?? []).filter((et) => et.isCritical);
  const criticalPatterns = new Set(criticalEvents.map((et) => et.pattern));
  const eventOptions = isSms ? criticalEvents : (eventTypes ?? []);
  const [audience, setAudience] = useState<'OptedInUsers' | 'Role'>(rule?.audience ?? 'OptedInUsers');
  const [audienceRef, setAudienceRef] = useState<string>(rule?.audienceRef ?? '');
  // Quiet hours (advanced): "HH:mm" for the time inputs (the API stores "HH:mm:ss"); empty both = off.
  const browserTz = Intl.DateTimeFormat().resolvedOptions().timeZone;
  const timeZones: string[] =
    (Intl as { supportedValuesOf?: (key: string) => string[] }).supportedValuesOf?.('timeZone') ?? [browserTz];
  const [quietStart, setQuietStart] = useState<string>(rule?.quietHoursStart?.slice(0, 5) ?? '');
  const [quietEnd, setQuietEnd] = useState<string>(rule?.quietHoursEnd?.slice(0, 5) ?? '');
  const [quietTz, setQuietTz] = useState<string>(rule?.quietHoursTimeZone ?? browserTz);
  const quietOn = !!quietStart && !!quietEnd;
  // Escalation chain (advanced): the write shape uses channelKind; the read DTO uses channel. Incompatible with digests.
  const [escalationSteps, setEscalationSteps] = useState<EscalationStepInput[]>(
    (rule?.escalationSteps ?? []).map((s) => ({ delayMinutes: s.delayMinutes, channelKind: s.channel, audience: s.audience, audienceRef: s.audienceRef })));
  const updateStep = (i: number, patch: Partial<EscalationStepInput>) =>
    setEscalationSteps((xs) => xs.map((s, j) => (j === i ? { ...s, ...patch } : s)));
  const canEscalate = digestCadence === 'Immediate'; // escalation + digest are mutually exclusive (server-enforced)

  const pending = create.isPending || update.isPending;

  const submit = (e: FormEvent) => {
    e.preventDefault();
    const input = {
      name: name.trim(),
      eventPattern: eventPattern.trim(),
      // SMS is critical-only + immediate + single (docs/36 §6): force the advanced shape empty so the persisted rule
      // can never violate the server's SmsCriticalOnly/SmsImmediateOnly/SmsNoQuietHours checks. Otherwise, only an
      // entitled editor can change the advanced set (never silently strip a rule's config on a plain name edit).
      additionalPatterns: isSms ? [] : (canRoute ? additionalPatterns : (rule?.additionalPatterns ?? [])),
      digestCadence: isSms ? 'Immediate' : (canRoute ? digestCadence : (rule?.digestCadence ?? 'Immediate')),
      channelKind,
      audience,
      audienceRef: audience === 'Role' ? (audienceRef || null) : null,
      // Quiet hours are advanced (and forbidden on SMS); a downgraded editor preserves whatever the rule already has.
      quietHoursStart: isSms ? null : (canRoute ? (quietOn ? `${quietStart}:00` : null) : (rule?.quietHoursStart ?? null)),
      quietHoursEnd: isSms ? null : (canRoute ? (quietOn ? `${quietEnd}:00` : null) : (rule?.quietHoursEnd ?? null)),
      quietHoursTimeZone: isSms ? null : (canRoute ? (quietOn ? quietTz : null) : (rule?.quietHoursTimeZone ?? null)),
      // SMS rules don't escalate in Slice C (SMS escalation is Slice F); a downgraded editor preserves the existing
      // chain; digest rules can't escalate.
      escalationSteps: isSms
        ? []
        : !canRoute
        ? (rule?.escalationSteps ?? []).map((s) => ({ delayMinutes: s.delayMinutes, channelKind: s.channel, audience: s.audience, audienceRef: s.audienceRef }))
        : (canEscalate ? escalationSteps : []),
    };
    const onError = (err: unknown) => toast(err instanceof ApiError ? err.message : 'Could not save the rule.', 'error');
    if (isEdit) {
      update.mutate({ id: rule.id, ...input }, { onSuccess: () => { toast('Rule updated.'); onClose(); }, onError });
    } else {
      create.mutate(input, { onSuccess: () => { toast('Rule created.'); onClose(); }, onError });
    }
  };

  // Quiet hours must be all-or-none and a non-zero window (mirrors the domain's InvalidQuietHours rule). Inert for SMS
  // (the controls are hidden and coerced null on submit).
  const quietPartial = canRoute && !isSms && !!quietStart !== !!quietEnd;
  const quietSameTime = canRoute && !isSms && quietOn && quietStart === quietEnd;
  // Each escalation step needs a positive delay and, for a role audience, a role. Inert for SMS (no escalation).
  const escalationValid = isSms || !canRoute || !canEscalate
    || escalationSteps.every((s) => s.delayMinutes >= 1 && (s.audience !== 'Role' || !!s.audienceRef));
  // An SMS rule must target a literal critical event (mirrors the server's SmsCriticalOnly — inline block, not a 400).
  const smsValid = !isSms || criticalPatterns.has(eventPattern.trim());
  const valid = name.trim().length > 0 && eventPattern.trim().length > 0 && (audience !== 'Role' || !!audienceRef)
    && !quietPartial && !quietSameTime && escalationValid && smsValid;

  return (
    <Dialog
      open
      onClose={onClose}
      title={isEdit ? 'Edit rule' : 'New rule'}
      description="WireHQ notifies the audience when a matching event happens in your organization."
      className="max-w-xl"
      footer={
        <>
          <Button variant="secondary" onClick={onClose}>Cancel</Button>
          <Button form="notification-rule-form" type="submit" disabled={pending || !valid}>
            {isEdit ? 'Save changes' : 'Create rule'}
          </Button>
        </>
      }
    >
      <form id="notification-rule-form" onSubmit={submit} className="space-y-4">
        <Field label="Name" htmlFor="rule-name">
          <Input id="rule-name" value={name} onChange={(e) => setName(e.target.value)} required maxLength={128} placeholder="Security alerts to admins" />
        </Field>

        <Field label="On event" htmlFor="rule-event">
          <Select id="rule-event" value={eventPattern} onChange={(e) => setEventPattern(e.target.value)} required>
            <option value="" disabled>Choose an event…</option>
            {eventOptions.map((et) => (
              <option key={et.pattern} value={et.pattern}>{et.label} ({et.pattern})</option>
            ))}
          </Select>
          {isSms && (
            <p className="mt-1 text-xs text-ink-500">SMS is limited to a single critical event.</p>
          )}
        </Field>

        {canRoute && !isSms && (
          <Field label="Also match — advanced" htmlFor="rule-extra-event">
            {additionalPatterns.length > 0 && (
              <div className="mb-2 flex flex-wrap gap-1.5">
                {additionalPatterns.map((p) => (
                  <span key={p} className="inline-flex items-center gap-1 rounded-full bg-ink-800 px-2 py-0.5 text-xs text-ink-200">
                    <span className="font-mono">{p}</span>
                    <button
                      type="button"
                      onClick={() => setAdditionalPatterns((xs) => xs.filter((x) => x !== p))}
                      className="text-ink-500 hover:text-ink-200"
                      aria-label={`Remove ${p}`}
                    >
                      ×
                    </button>
                  </span>
                ))}
              </div>
            )}
            <Select
              id="rule-extra-event"
              value=""
              onChange={(e) => {
                const v = e.target.value;
                if (v && v !== eventPattern && !additionalPatterns.includes(v)) {
                  setAdditionalPatterns((xs) => [...xs, v]);
                }
              }}
            >
              <option value="">Add another event…</option>
              {(eventTypes ?? [])
                .filter((et) => et.pattern !== eventPattern && !additionalPatterns.includes(et.pattern))
                .map((et) => (
                  <option key={et.pattern} value={et.pattern}>{et.label} ({et.pattern})</option>
                ))}
            </Select>
            <p className="mt-1 text-xs text-ink-500">One rule can react to several events — an Advanced Notifications feature.</p>
          </Field>
        )}

        {canRoute && !isSms && (
          <Field label="Delivery" htmlFor="rule-digest">
            <Select
              id="rule-digest"
              value={digestCadence}
              onChange={(e) => setDigestCadence(e.target.value as 'Immediate' | 'Daily' | 'Weekly')}
            >
              <option value="Immediate">Immediately, as events happen</option>
              <option value="Daily">Daily digest</option>
              <option value="Weekly">Weekly digest</option>
            </Select>
            <p className="mt-1 text-xs text-ink-500">Coalesce matching events into one periodic message — an Advanced Notifications feature.</p>
          </Field>
        )}

        {canRoute && !isSms && (
          <Field label="Quiet hours — advanced" htmlFor="rule-quiet-start">
            <div className="flex flex-wrap items-center gap-2">
              <Input id="rule-quiet-start" type="time" value={quietStart} onChange={(e) => setQuietStart(e.target.value)} aria-label="Quiet hours start" />
              <span className="text-xs text-ink-500">to</span>
              <Input id="rule-quiet-end" type="time" value={quietEnd} onChange={(e) => setQuietEnd(e.target.value)} aria-label="Quiet hours end" />
              {quietOn && (
                <button
                  type="button"
                  onClick={() => { setQuietStart(''); setQuietEnd(''); }}
                  className="text-xs text-ink-500 hover:text-ink-200"
                >
                  Clear
                </button>
              )}
            </div>
            {quietOn && (
              <div className="mt-2">
                <Select id="rule-quiet-tz" value={quietTz} onChange={(e) => setQuietTz(e.target.value)} aria-label="Quiet hours time zone">
                  {timeZones.map((tz) => (
                    <option key={tz} value={tz}>{tz}</option>
                  ))}
                </Select>
              </div>
            )}
            <p className="mt-1 text-xs text-ink-500">
              {quietSameTime
                ? 'Choose a different end time.'
                : 'Hold deliveries during this window and release them when it ends. Leave both empty for none — an Advanced Notifications feature.'}
            </p>
          </Field>
        )}

        {canRoute && canEscalate && !isSms && (
          <Field label="Escalation — advanced" htmlFor="rule-escalation">
            {escalationSteps.length > 0 && (
              <div className="mb-2 space-y-2">
                {escalationSteps.map((step, i) => (
                  <div key={i} className="flex flex-wrap items-center gap-2 rounded-md border border-ink-800 p-2">
                    <span className="text-xs text-ink-500">After</span>
                    <Input
                      id={i === 0 ? 'rule-escalation' : undefined}
                      type="number"
                      min={1}
                      value={step.delayMinutes}
                      onChange={(e) => updateStep(i, { delayMinutes: Number(e.target.value) })}
                      aria-label={`Step ${i + 1} delay in minutes`}
                    />
                    <span className="text-xs text-ink-500">min, notify</span>
                    <Select
                      value={step.audience}
                      onChange={(e) => updateStep(i, { audience: e.target.value as 'OptedInUsers' | 'Role', audienceRef: null })}
                      aria-label={`Step ${i + 1} audience`}
                    >
                      <option value="OptedInUsers">opted-in users</option>
                      <option value="Role">a role</option>
                    </Select>
                    {step.audience === 'Role' && (
                      <Select
                        value={step.audienceRef ?? ''}
                        onChange={(e) => updateStep(i, { audienceRef: e.target.value || null })}
                        aria-label={`Step ${i + 1} role`}
                      >
                        <option value="" disabled>Choose a role…</option>
                        {(roles ?? []).map((r) => (<option key={r.id} value={r.id}>{r.name}</option>))}
                      </Select>
                    )}
                    <Select
                      value={step.channelKind}
                      onChange={(e) => updateStep(i, { channelKind: e.target.value as 'Email' | 'Chat' })}
                      disabled={!canChat}
                      aria-label={`Step ${i + 1} channel`}
                    >
                      <option value="Email">by email</option>
                      {canChat && <option value="Chat">by chat</option>}
                    </Select>
                    <button
                      type="button"
                      onClick={() => setEscalationSteps((xs) => xs.filter((_, j) => j !== i))}
                      className="text-ink-500 hover:text-ink-200"
                      aria-label={`Remove step ${i + 1}`}
                    >
                      ×
                    </button>
                  </div>
                ))}
              </div>
            )}
            {escalationSteps.length < 5 && (
              <Button
                type="button"
                variant="secondary"
                onClick={() => setEscalationSteps((xs) => [...xs, { delayMinutes: 15, channelKind: 'Email', audience: 'OptedInUsers', audienceRef: null }])}
              >
                Add escalation step
              </Button>
            )}
            <p className="mt-1 text-xs text-ink-500">
              If no one acknowledges the alert in WireHQ, notify the next step after its delay — an Advanced Notifications feature.
            </p>
          </Field>
        )}

        <Field label="Channel" htmlFor="rule-channel">
          <Select
            id="rule-channel"
            value={channelKind}
            onChange={(e) => {
              const v = e.target.value as 'Email' | 'Chat' | 'Sms';
              setChannelKind(v);
              // Switching to SMS: drop anything the critical-only shape can't carry so the picker + submit stay coherent.
              if (v === 'Sms') {
                if (!criticalPatterns.has(eventPattern.trim())) setEventPattern('');
                setAdditionalPatterns([]);
              }
            }}
            disabled={!canChat && !canSms}
          >
            <option value="Email">Email (free)</option>
            {canChat && <option value="Chat">Chat (Teams / Slack)</option>}
            {canSms && <option value="Sms">SMS (critical alerts)</option>}
          </Select>
          {channelKind === 'Chat' && (
            <p className="mt-1 text-xs text-ink-500">Set your Teams/Slack destination below before Chat rules can deliver.</p>
          )}
          {isSms && (
            <p className="mt-1 text-xs text-ink-500">
              SMS is for a single critical event, sent immediately — no digests, quiet hours, or wildcards. Configure your provider below.
            </p>
          )}
        </Field>

        <Field label="Send to" htmlFor="rule-audience">
          <Select id="rule-audience" value={audience} onChange={(e) => setAudience(e.target.value as 'OptedInUsers' | 'Role')}>
            <option value="OptedInUsers">Users who opted in to security alerts</option>
            <option value="Role">Members of a role</option>
          </Select>
        </Field>

        {audience === 'Role' && (
          <Field label="Role" htmlFor="rule-role">
            <Select id="rule-role" value={audienceRef} onChange={(e) => setAudienceRef(e.target.value)} required>
              <option value="" disabled>Choose a role…</option>
              {(roles ?? []).map((r) => (
                <option key={r.id} value={r.id}>{r.name}</option>
              ))}
            </Select>
          </Field>
        )}
      </form>
    </Dialog>
  );
}
