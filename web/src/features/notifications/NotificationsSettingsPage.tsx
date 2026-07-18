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
  type NotificationRule,
  type NotificationDelivery,
  type EscalationStepInput,
} from './api';

const DELIVERY_TONE: Record<NotificationDelivery['status'], string> = {
  Delivered: 'bg-success-500/15 text-success-700 dark:text-success-400',
  Pending: 'bg-gold-500/15 text-gold-700 dark:text-gold-400',
  Failed: 'bg-danger-500/15 text-danger-600 dark:text-danger-400',
  Cancelled: 'bg-ink-500/15 text-ink-400',
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
          canChat
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
              <span className={`shrink-0 rounded-full px-2 py-0.5 text-xs font-medium ${DELIVERY_TONE[d.status]}`}>{d.status}</span>
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
  const canRoute = hasFeature('notifications.routing');
  const [name, setName] = useState(rule?.name ?? '');
  const [eventPattern, setEventPattern] = useState(rule?.eventPattern ?? '');
  const [additionalPatterns, setAdditionalPatterns] = useState<string[]>(rule?.additionalPatterns ?? []);
  const [digestCadence, setDigestCadence] = useState<'Immediate' | 'Daily' | 'Weekly'>(rule?.digestCadence ?? 'Immediate');
  const [channelKind, setChannelKind] = useState<'Email' | 'Chat'>(rule?.channel === 'Chat' ? 'Chat' : 'Email');
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
      // Only an entitled editor can change the advanced set; otherwise preserve what the rule already has
      // (never silently strip a rule's multi-pattern config on a plain name edit by a downgraded user).
      additionalPatterns: canRoute ? additionalPatterns : (rule?.additionalPatterns ?? []),
      digestCadence: canRoute ? digestCadence : (rule?.digestCadence ?? 'Immediate'),
      channelKind,
      audience,
      audienceRef: audience === 'Role' ? (audienceRef || null) : null,
      // Quiet hours are advanced; a downgraded (non-routing) editor preserves whatever the rule already has.
      quietHoursStart: canRoute ? (quietOn ? `${quietStart}:00` : null) : (rule?.quietHoursStart ?? null),
      quietHoursEnd: canRoute ? (quietOn ? `${quietEnd}:00` : null) : (rule?.quietHoursEnd ?? null),
      quietHoursTimeZone: canRoute ? (quietOn ? quietTz : null) : (rule?.quietHoursTimeZone ?? null),
      // A downgraded (non-routing) editor preserves the rule's existing chain; digest rules can't escalate.
      escalationSteps: !canRoute
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

  // Quiet hours must be all-or-none and a non-zero window (mirrors the domain's InvalidQuietHours rule).
  const quietPartial = canRoute && !!quietStart !== !!quietEnd;
  const quietSameTime = canRoute && quietOn && quietStart === quietEnd;
  // Each escalation step needs a positive delay and, for a role audience, a role.
  const escalationValid = !canRoute || !canEscalate
    || escalationSteps.every((s) => s.delayMinutes >= 1 && (s.audience !== 'Role' || !!s.audienceRef));
  const valid = name.trim().length > 0 && eventPattern.trim().length > 0 && (audience !== 'Role' || !!audienceRef)
    && !quietPartial && !quietSameTime && escalationValid;

  return (
    <Dialog
      open
      onClose={onClose}
      title={isEdit ? 'Edit rule' : 'New rule'}
      description="WireHQ emails the audience when a matching event happens in your organization."
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
            {(eventTypes ?? []).map((et) => (
              <option key={et.pattern} value={et.pattern}>{et.label} ({et.pattern})</option>
            ))}
          </Select>
        </Field>

        {canRoute && (
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

        {canRoute && (
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

        {canRoute && (
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

        {canRoute && canEscalate && (
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
          <Select id="rule-channel" value={channelKind} onChange={(e) => setChannelKind(e.target.value as 'Email' | 'Chat')} disabled={!canChat}>
            <option value="Email">Email (free)</option>
            {canChat && <option value="Chat">Chat (Teams / Slack)</option>}
          </Select>
          {channelKind === 'Chat' && (
            <p className="mt-1 text-xs text-ink-500">Set your Teams/Slack destination below before Chat rules can deliver.</p>
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
