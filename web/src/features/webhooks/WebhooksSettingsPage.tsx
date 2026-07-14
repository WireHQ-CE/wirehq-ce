import { useMemo, useState, type FormEvent, type ReactNode } from 'react';
import { PageHeader } from '@/components/layout/AppShell';
import { Card, CardContent } from '@/components/ui/card';
import { Button } from '@/components/ui/button';
import { Input, Field } from '@/components/ui/input';
import { Dialog } from '@/components/ui/dialog';
import { CodeBlock } from '@/components/ui/code-block';
import { useToast } from '@/components/ui/toast';
import { ApiError } from '@/lib/api/client';
import { useAuthStore } from '@/stores/auth-store';
import {
  useWebhooks,
  useWebhookEventTypes,
  useWebhookDeliveries,
  useCreateWebhook,
  useUpdateWebhook,
  useSetWebhookStatus,
  useRotateWebhookSecret,
  useTestWebhook,
  useDeleteWebhook,
  type Webhook,
  type WebhookDelivery,
  type WebhookEventType,
} from './api';

const STATUS_TONE: Record<Webhook['status'], string> = {
  Active: 'bg-success-500/15 text-success-700 dark:text-success-400',
  Disabled: 'bg-ink-500/15 text-ink-400',
};

const DELIVERY_TONE: Record<WebhookDelivery['status'], string> = {
  Delivered: 'bg-success-500/15 text-success-700 dark:text-success-400',
  Pending: 'bg-gold-500/15 text-gold-700 dark:text-gold-400',
  Failed: 'bg-danger-500/15 text-danger-600 dark:text-danger-400',
};

function formatDate(iso: string): string {
  return new Date(iso).toLocaleString(undefined, { dateStyle: 'medium', timeStyle: 'short' });
}

export function WebhooksSettingsPage() {
  const hasFeature = useAuthStore((s) => s.hasFeature);
  const hasPermission = useAuthStore((s) => s.hasPermission);
  const canManage = hasPermission('api.keys.manage');
  const { data: webhooks, isLoading } = useWebhooks();
  const toast = useToast();

  const test = useTestWebhook();
  const setStatus = useSetWebhookStatus();
  const rotate = useRotateWebhookSecret();
  const del = useDeleteWebhook();

  // editing: undefined = closed, null = create, Webhook = edit.
  const [editing, setEditing] = useState<Webhook | null | undefined>(undefined);
  const [secret, setSecret] = useState<string | null>(null);
  const [deliveriesFor, setDeliveriesFor] = useState<Webhook | null>(null);

  if (!hasFeature('api.keys')) {
    return (
      <>
        <PageHeader title="Webhooks" subtitle="Get notified when things happen in your organization." />
        <Card className="max-w-2xl">
          <CardContent>
            <p className="text-sm text-ink-400">
              Webhooks are an <span className="font-medium text-ink-200">Enterprise</span> feature. Contact sales to
              enable outbound event notifications for your organization.
            </p>
          </CardContent>
        </Card>
      </>
    );
  }

  const fail = (e: unknown, fallback: string) => toast(e instanceof ApiError ? e.message : fallback, 'error');

  const onTest = (webhook: Webhook) =>
    test.mutate(webhook.id, { onSuccess: () => toast('Test event queued.'), onError: (e) => fail(e, 'Could not send a test event.') });

  const onToggle = (webhook: Webhook) =>
    setStatus.mutate(
      { id: webhook.id, enabled: webhook.status !== 'Active' },
      {
        onSuccess: () => toast(webhook.status === 'Active' ? 'Endpoint disabled.' : 'Endpoint enabled.'),
        onError: (e) => fail(e, 'Could not change the endpoint.'),
      },
    );

  const onRotate = (webhook: Webhook) => {
    if (!window.confirm(`Rotate the signing secret for “${webhook.url}”? The current secret stops working immediately.`)) return;
    rotate.mutate(webhook.id, { onSuccess: (res) => setSecret(res.signingSecret), onError: (e) => fail(e, 'Could not rotate the secret.') });
  };

  const onDelete = (webhook: Webhook) => {
    if (!window.confirm(`Delete the webhook “${webhook.url}”? Its delivery history is removed too.`)) return;
    del.mutate(webhook.id, { onSuccess: () => toast('Webhook deleted.'), onError: (e) => fail(e, 'Could not delete the webhook.') });
  };

  return (
    <>
      <PageHeader
        title="Webhooks"
        subtitle="WireHQ POSTs a signed request to your endpoint when a subscribed event happens."
        action={canManage ? <Button onClick={() => setEditing(null)}>New endpoint</Button> : undefined}
      />

      {isLoading ? (
        <p className="text-sm text-ink-400">Loading…</p>
      ) : (
        <div className="max-w-3xl">
          <Card>
            <CardContent>
              {webhooks && webhooks.length > 0 ? (
                <div className="divide-y divide-ink-800">
                  {webhooks.map((webhook) => (
                    <WebhookRow key={webhook.id} webhook={webhook}>
                      <div className="flex flex-wrap justify-end gap-2">
                        <Button variant="ghost" onClick={() => setDeliveriesFor(webhook)}>Deliveries</Button>
                        {canManage && (
                          <>
                            <Button variant="ghost" onClick={() => onTest(webhook)}>Test</Button>
                            <Button variant="ghost" onClick={() => setEditing(webhook)}>Edit</Button>
                            <Button variant="ghost" onClick={() => onToggle(webhook)}>
                              {webhook.status === 'Active' ? 'Disable' : 'Enable'}
                            </Button>
                            <Button variant="ghost" onClick={() => onRotate(webhook)}>Rotate secret</Button>
                            <Button variant="ghost" onClick={() => onDelete(webhook)}>Delete</Button>
                          </>
                        )}
                      </div>
                    </WebhookRow>
                  ))}
                </div>
              ) : (
                <p className="text-sm text-ink-400">
                  No webhook endpoints yet.{canManage ? ' Create one to receive event notifications.' : ''}
                </p>
              )}
            </CardContent>
          </Card>
        </div>
      )}

      {editing !== undefined && (
        <WebhookEditorDialog
          webhook={editing}
          onClose={() => setEditing(undefined)}
          onCreated={(newSecret) => {
            setEditing(undefined);
            setSecret(newSecret);
          }}
        />
      )}

      {secret && <SecretRevealDialog secret={secret} onClose={() => setSecret(null)} />}

      {deliveriesFor && <DeliveriesDialog webhook={deliveriesFor} onClose={() => setDeliveriesFor(null)} />}
    </>
  );
}

function WebhookRow({ webhook, children }: { webhook: Webhook; children: ReactNode }) {
  return (
    <div className="flex flex-col gap-2 py-3 first:pt-0 last:pb-0 sm:flex-row sm:items-start sm:justify-between sm:gap-4">
      <div className="min-w-0">
        <div className="flex items-center gap-2">
          <p className="truncate font-mono text-sm text-ink-100">{webhook.url}</p>
          <span className={`shrink-0 rounded-full px-2 py-0.5 text-xs font-medium ${STATUS_TONE[webhook.status]}`}>
            {webhook.status}
          </span>
        </div>
        {webhook.description && <p className="mt-0.5 text-xs text-ink-400">{webhook.description}</p>}
        <p className="mt-0.5 text-xs text-ink-500">{webhook.eventTypes.join(', ')}</p>
      </div>
      {children}
    </div>
  );
}

function WebhookEditorDialog({
  webhook,
  onClose,
  onCreated,
}: {
  webhook: Webhook | null;
  onClose: () => void;
  onCreated: (secret: string) => void;
}) {
  const isEdit = webhook !== null;
  const { data: eventTypes } = useWebhookEventTypes();
  const create = useCreateWebhook();
  const update = useUpdateWebhook();
  const toast = useToast();

  const [url, setUrl] = useState(webhook?.url ?? '');
  const [description, setDescription] = useState(webhook?.description ?? '');
  const [selected, setSelected] = useState<Set<string>>(new Set(webhook?.eventTypes ?? []));

  const grouped = useMemo(() => {
    const groups = new Map<string, WebhookEventType[]>();
    for (const option of eventTypes ?? []) {
      const list = groups.get(option.group);
      if (list) list.push(option);
      else groups.set(option.group, [option]);
    }
    return [...groups.entries()];
  }, [eventTypes]);

  // A subscribed pattern that isn't in the catalog (a custom one) still shows as checked so an edit preserves it.
  const custom = useMemo(
    () => [...selected].filter((p) => !(eventTypes ?? []).some((o) => o.pattern === p)),
    [selected, eventTypes],
  );

  const toggle = (pattern: string) =>
    setSelected((current) => {
      const next = new Set(current);
      if (next.has(pattern)) next.delete(pattern);
      else next.add(pattern);
      return next;
    });

  const pending = create.isPending || update.isPending;

  const submit = (e: FormEvent) => {
    e.preventDefault();
    const input = { url: url.trim(), description: description.trim() || null, eventTypes: [...selected] };
    if (isEdit) {
      update.mutate(
        { id: webhook.id, ...input },
        { onSuccess: () => { toast('Webhook updated.'); onClose(); }, onError: (err) => toast(err instanceof ApiError ? err.message : 'Could not save the webhook.', 'error') },
      );
    } else {
      create.mutate(input, {
        onSuccess: (res) => { toast('Webhook created.'); onCreated(res.signingSecret); },
        onError: (err) => toast(err instanceof ApiError ? err.message : 'Could not create the webhook.', 'error'),
      });
    }
  };

  return (
    <Dialog
      open
      onClose={onClose}
      title={isEdit ? 'Edit webhook' : 'New webhook'}
      description="WireHQ POSTs a signed JSON body to this URL when a subscribed event happens."
      className="max-w-2xl"
      footer={
        <>
          <Button variant="secondary" onClick={onClose}>Cancel</Button>
          <Button form="webhook-form" type="submit" disabled={pending || !url.trim() || selected.size === 0}>
            {isEdit ? 'Save changes' : 'Create webhook'}
          </Button>
        </>
      }
    >
      <form id="webhook-form" onSubmit={submit} className="space-y-4">
        <Field label="Payload URL" htmlFor="webhook-url">
          <Input id="webhook-url" type="url" value={url} onChange={(e) => setUrl(e.target.value)} required maxLength={2048}
            placeholder="https://example.com/webhooks/wirehq" />
        </Field>
        <Field label="Description" htmlFor="webhook-desc">
          <Input id="webhook-desc" value={description} onChange={(e) => setDescription(e.target.value)} maxLength={256}
            placeholder="Optional — what this endpoint is for" />
        </Field>

        <div className="space-y-4">
          <p className="text-sm font-medium text-ink-200">Events</p>
          {grouped.map(([group, options]) => (
            <div key={group} className="space-y-1.5">
              <p className="text-xs font-semibold uppercase tracking-wide text-ink-500">{group}</p>
              {options.map((option) => (
                <label key={option.pattern} className="flex items-start gap-2 text-sm">
                  <input type="checkbox" className="mt-0.5" checked={selected.has(option.pattern)} onChange={() => toggle(option.pattern)} />
                  <span>
                    <span className="text-ink-200">{option.description}</span>
                    <span className="ml-1 text-xs text-ink-500">({option.pattern})</span>
                  </span>
                </label>
              ))}
            </div>
          ))}
          {custom.length > 0 && (
            <div className="space-y-1.5">
              <p className="text-xs font-semibold uppercase tracking-wide text-ink-500">Custom</p>
              {custom.map((pattern) => (
                <label key={pattern} className="flex items-start gap-2 text-sm">
                  <input type="checkbox" className="mt-0.5" checked onChange={() => toggle(pattern)} />
                  <span className="font-mono text-xs text-ink-300">{pattern}</span>
                </label>
              ))}
            </div>
          )}
        </div>
      </form>
    </Dialog>
  );
}

function SecretRevealDialog({ secret, onClose }: { secret: string; onClose: () => void }) {
  return (
    <Dialog
      open
      onClose={onClose}
      title="Signing secret"
      description="Copy this secret now — it won't be shown again."
      className="max-w-2xl"
      footer={<Button onClick={onClose}>Done</Button>}
    >
      <div className="space-y-3">
        <div className="rounded-md border border-gold-500/40 bg-gold-500/5 p-3 text-sm text-ink-300">
          Use this to verify each delivery's <code className="font-mono text-xs">X-WireHQ-Signature</code> header
          (HMAC-SHA256 of the raw body). It's the only time the secret is displayed — store it somewhere safe.
        </div>
        <CodeBlock content={secret} />
      </div>
    </Dialog>
  );
}

function DeliveriesDialog({ webhook, onClose }: { webhook: Webhook; onClose: () => void }) {
  const { data: deliveries, isLoading } = useWebhookDeliveries(webhook.id);

  return (
    <Dialog open onClose={onClose} title="Recent deliveries" description={webhook.url} className="max-w-2xl" footer={<Button onClick={onClose}>Close</Button>}>
      {isLoading ? (
        <p className="text-sm text-ink-400">Loading…</p>
      ) : deliveries && deliveries.length > 0 ? (
        <div className="divide-y divide-ink-800">
          {deliveries.map((delivery) => (
            <div key={delivery.id} className="flex items-center justify-between gap-4 py-2.5 first:pt-0 last:pb-0">
              <div className="min-w-0">
                <div className="flex items-center gap-2">
                  <span className={`shrink-0 rounded-full px-2 py-0.5 text-xs font-medium ${DELIVERY_TONE[delivery.status]}`}>
                    {delivery.status}
                  </span>
                  <p className="truncate font-mono text-xs text-ink-200">{delivery.eventType}</p>
                </div>
                <p className="mt-0.5 text-xs text-ink-500">
                  {formatDate(delivery.createdAtUtc)}
                  {delivery.attempts > 0 ? ` · ${delivery.attempts} attempt${delivery.attempts === 1 ? '' : 's'}` : ''}
                  {delivery.lastResponseCode != null ? ` · HTTP ${delivery.lastResponseCode}` : ''}
                </p>
              </div>
            </div>
          ))}
        </div>
      ) : (
        <p className="text-sm text-ink-400">No deliveries yet.</p>
      )}
    </Dialog>
  );
}
