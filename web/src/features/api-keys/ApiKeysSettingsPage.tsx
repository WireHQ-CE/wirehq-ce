import { useMemo, useState, type FormEvent, type ReactNode } from 'react';
import { PageHeader } from '@/components/layout/AppShell';
import { Card, CardContent } from '@/components/ui/card';
import { Button } from '@/components/ui/button';
import { Input, Field } from '@/components/ui/input';
import { Select } from '@/components/ui/select';
import { Dialog } from '@/components/ui/dialog';
import { CodeBlock } from '@/components/ui/code-block';
import { useToast } from '@/components/ui/toast';
import { ApiError } from '@/lib/api/client';
import { useAuthStore } from '@/stores/auth-store';
import {
  useApiKeys,
  useApiKeyScopes,
  useCreateApiKey,
  useRevokeApiKey,
  type ApiKey,
  type ApiKeyScopeOption,
} from './api';

const STATUS_TONE: Record<ApiKey['status'], string> = {
  Active: 'bg-success-500/15 text-success-700 dark:text-success-400',
  Expired: 'bg-gold-500/15 text-gold-700 dark:text-gold-400',
  Revoked: 'bg-danger-500/15 text-danger-600 dark:text-danger-400',
};

const EXPIRY_OPTIONS: { label: string; days: number | null }[] = [
  { label: 'No expiry', days: null },
  { label: '30 days', days: 30 },
  { label: '90 days', days: 90 },
  { label: '1 year', days: 365 },
];

function formatDate(iso: string): string {
  return new Date(iso).toLocaleDateString(undefined, { year: 'numeric', month: 'short', day: 'numeric' });
}

export function ApiKeysSettingsPage() {
  const hasFeature = useAuthStore((s) => s.hasFeature);
  const hasPermission = useAuthStore((s) => s.hasPermission);
  const canManage = hasPermission('api.keys.manage');
  const { data: keys, isLoading } = useApiKeys();
  const toast = useToast();
  const revoke = useRevokeApiKey();

  const [creating, setCreating] = useState(false);

  if (!hasFeature('api.keys')) {
    return (
      <>
        <PageHeader title="API keys" subtitle="Programmatic access to the WireHQ API." />
        <Card className="max-w-2xl">
          <CardContent>
            <p className="text-sm text-ink-400">
              API keys are an <span className="font-medium text-ink-200">Enterprise</span> feature. Contact sales to
              enable programmatic API access for your organization.
            </p>
          </CardContent>
        </Card>
      </>
    );
  }

  const remove = (key: ApiKey) => {
    if (!window.confirm(`Revoke the key “${key.name}”? Any integration using it will stop working immediately.`)) return;
    revoke.mutate(key.id, {
      onSuccess: () => toast('API key revoked.'),
      onError: (e) => toast(e instanceof ApiError ? e.message : 'Could not revoke the key.', 'error'),
    });
  };

  return (
    <>
      <PageHeader
        title="API keys"
        subtitle="Scoped, revocable keys let scripts and services call the WireHQ API without a user login."
        action={canManage ? <Button onClick={() => setCreating(true)}>New key</Button> : undefined}
      />

      {isLoading ? (
        <p className="text-sm text-ink-400">Loading…</p>
      ) : (
        <div className="max-w-3xl">
          <Card>
            <CardContent>
              {keys && keys.length > 0 ? (
                <div className="divide-y divide-ink-800">
                  {keys.map((key) => (
                    <ApiKeyRow key={key.id} apiKey={key}>
                      {canManage && (
                        <Button variant="ghost" onClick={() => remove(key)}>
                          Revoke
                        </Button>
                      )}
                    </ApiKeyRow>
                  ))}
                </div>
              ) : (
                <p className="text-sm text-ink-400">
                  No API keys yet.{canManage ? ' Create one to let a script or service call the API.' : ''}
                </p>
              )}
            </CardContent>
          </Card>
        </div>
      )}

      {creating && <CreateKeyDialog onClose={() => setCreating(false)} />}
    </>
  );
}

function ApiKeyRow({ apiKey, children }: { apiKey: ApiKey; children: ReactNode }) {
  const meta = [
    `${apiKey.scopes.length} scope${apiKey.scopes.length === 1 ? '' : 's'}`,
    apiKey.lastUsedAtUtc ? `last used ${formatDate(apiKey.lastUsedAtUtc)}` : 'never used',
    apiKey.expiresAtUtc ? `expires ${formatDate(apiKey.expiresAtUtc)}` : 'no expiry',
  ].join(' · ');

  return (
    <div className="flex items-center justify-between gap-4 py-3 first:pt-0 last:pb-0">
      <div className="min-w-0">
        <div className="flex items-center gap-2">
          <p className="truncate text-sm font-medium text-ink-100">{apiKey.name}</p>
          <span className={`shrink-0 rounded-full px-2 py-0.5 text-xs font-medium ${STATUS_TONE[apiKey.status]}`}>
            {apiKey.status}
          </span>
        </div>
        <p className="mt-0.5 font-mono text-xs text-ink-400">{apiKey.keyPrefix}…</p>
        <p className="mt-0.5 text-xs text-ink-500">{meta}</p>
      </div>
      {children}
    </div>
  );
}

function CreateKeyDialog({ onClose }: { onClose: () => void }) {
  const hasPermission = useAuthStore((s) => s.hasPermission);
  const { data: scopes } = useApiKeyScopes();
  const create = useCreateApiKey();
  const toast = useToast();

  const [name, setName] = useState('');
  const [expiry, setExpiry] = useState('null');
  const [selected, setSelected] = useState<Set<string>>(new Set());
  const [secret, setSecret] = useState<string | null>(null);

  const grouped = useMemo(() => {
    const groups = new Map<string, ApiKeyScopeOption[]>();
    for (const scope of scopes ?? []) {
      const list = groups.get(scope.group);
      if (list) list.push(scope);
      else groups.set(scope.group, [scope]);
    }
    return [...groups.entries()];
  }, [scopes]);

  const toggle = (key: string) =>
    setSelected((current) => {
      const next = new Set(current);
      if (next.has(key)) next.delete(key);
      else next.add(key);
      return next;
    });

  const submit = (e: FormEvent) => {
    e.preventDefault();
    const days = expiry === 'null' ? null : Number(expiry);
    const expiresAtUtc = days === null ? null : new Date(Date.now() + days * 86_400_000).toISOString();
    create.mutate(
      { name, scopes: [...selected], expiresAtUtc },
      {
        onSuccess: (res) => setSecret(res.key),
        onError: (err) => toast(err instanceof ApiError ? err.message : 'Could not create the key.', 'error'),
      },
    );
  };

  // Phase 2 — the plaintext secret, shown ONCE (only its hash is stored server-side).
  if (secret) {
    return (
      <Dialog
        open
        onClose={onClose}
        title="API key created"
        description="Copy this key now — it won't be shown again."
        className="max-w-2xl"
        footer={<Button onClick={onClose}>Done</Button>}
      >
        <div className="space-y-3">
          <div className="rounded-md border border-gold-500/40 bg-gold-500/5 p-3 text-sm text-ink-300">
            This is the only time the secret is displayed. Store it somewhere safe (a secrets manager) and send it as
            an <code className="font-mono text-xs">X-Api-Key</code> header or a <code className="font-mono text-xs">Bearer</code>{' '}
            token. If you lose it, revoke this key and create a new one.
          </div>
          <CodeBlock content={secret} />
        </div>
      </Dialog>
    );
  }

  // Phase 1 — the create form + scope picker.
  return (
    <Dialog
      open
      onClose={onClose}
      title="New API key"
      description="Pick the scopes this key grants. You can only grant scopes you hold yourself."
      className="max-w-2xl"
      footer={
        <>
          <Button variant="secondary" onClick={onClose}>
            Cancel
          </Button>
          <Button
            form="api-key-form"
            type="submit"
            disabled={create.isPending || !name.trim() || selected.size === 0}
          >
            Create key
          </Button>
        </>
      }
    >
      <form id="api-key-form" onSubmit={submit} className="space-y-4">
        <Field label="Name" htmlFor="api-key-name">
          <Input
            id="api-key-name"
            value={name}
            onChange={(e) => setName(e.target.value)}
            required
            maxLength={128}
            placeholder="e.g. CI deploy"
          />
        </Field>
        <Field label="Expiry" htmlFor="api-key-expiry">
          <Select id="api-key-expiry" value={expiry} onChange={(e) => setExpiry(e.target.value)}>
            {EXPIRY_OPTIONS.map((option) => (
              <option key={option.label} value={option.days === null ? 'null' : String(option.days)}>
                {option.label}
              </option>
            ))}
          </Select>
        </Field>

        <div className="space-y-4">
          <p className="text-sm font-medium text-ink-200">Scopes</p>
          {grouped.map(([group, options]) => (
            <div key={group} className="space-y-1.5">
              <p className="text-xs font-semibold uppercase tracking-wide text-ink-500">{group}</p>
              {options.map((option) => {
                // Mirror the server's escalation guard: you can grant only scopes you hold yourself.
                const disabled = !hasPermission(option.key) && !selected.has(option.key);
                return (
                  <label key={option.key} className={`flex items-start gap-2 text-sm ${disabled ? 'opacity-50' : ''}`}>
                    <input
                      type="checkbox"
                      className="mt-0.5"
                      checked={selected.has(option.key)}
                      disabled={disabled}
                      onChange={() => toggle(option.key)}
                    />
                    <span>
                      <span className="text-ink-200">{option.description}</span>
                      <span className="ml-1 text-xs text-ink-500">({option.key})</span>
                    </span>
                  </label>
                );
              })}
            </div>
          ))}
        </div>
      </form>
    </Dialog>
  );
}
