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
  type NotificationRule,
  type NotificationDelivery,
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
                          <span className="font-mono">{rule.eventPattern}</span> → {rule.channel} · {rule.audience === 'Role' ? 'role' : 'opted-in users'}
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

function RuleEditorDialog({ rule, onClose }: { rule: NotificationRule | null; onClose: () => void }) {
  const isEdit = rule !== null;
  const { data: eventTypes } = useNotificationEventTypes();
  const { data: roles } = useOrgRoles();
  const create = useCreateNotificationRule();
  const update = useUpdateNotificationRule();
  const toast = useToast();

  const hasFeature = useAuthStore((s) => s.hasFeature);
  const canChat = hasFeature('notifications.chat');
  const [name, setName] = useState(rule?.name ?? '');
  const [eventPattern, setEventPattern] = useState(rule?.eventPattern ?? '');
  const [channelKind, setChannelKind] = useState<'Email' | 'Chat'>(rule?.channel === 'Chat' ? 'Chat' : 'Email');
  const [audience, setAudience] = useState<'OptedInUsers' | 'Role'>(rule?.audience ?? 'OptedInUsers');
  const [audienceRef, setAudienceRef] = useState<string>(rule?.audienceRef ?? '');

  const pending = create.isPending || update.isPending;

  const submit = (e: FormEvent) => {
    e.preventDefault();
    const input = {
      name: name.trim(),
      eventPattern: eventPattern.trim(),
      channelKind,
      audience,
      audienceRef: audience === 'Role' ? (audienceRef || null) : null,
    };
    const onError = (err: unknown) => toast(err instanceof ApiError ? err.message : 'Could not save the rule.', 'error');
    if (isEdit) {
      update.mutate({ id: rule.id, ...input }, { onSuccess: () => { toast('Rule updated.'); onClose(); }, onError });
    } else {
      create.mutate(input, { onSuccess: () => { toast('Rule created.'); onClose(); }, onError });
    }
  };

  const valid = name.trim().length > 0 && eventPattern.trim().length > 0 && (audience !== 'Role' || !!audienceRef);

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
