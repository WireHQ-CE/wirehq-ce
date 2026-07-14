import { useState, type FormEvent } from 'react';
import { Pencil, Plug, Plus, Server, Trash2 } from 'lucide-react';
import { Card } from '@/components/ui/card';
import { Button } from '@/components/ui/button';
import { Badge } from '@/components/ui/badge';
import { Input, Field } from '@/components/ui/input';
import { Select } from '@/components/ui/select';
import { Dialog } from '@/components/ui/dialog';
import { EmptyState } from '@/components/data/EmptyState';
import { useToast } from '@/components/ui/toast';
import { ApiError } from '@/lib/api/client';
import { noFormErrors, toFormErrors, type FormErrors } from '@/lib/api/form-errors';
import {
  useCreateSshTarget,
  useDeleteSshTarget,
  useSshTargets,
  useTestSshTarget,
  useUpdateSshTarget,
} from './api';
import type { SshTarget } from './types';

/**
 * SSH deployment targets: the remote hosts WireHQ pushes configs to. Manage credentials (write-only,
 * encrypted server-side), test connectivity (which pins the host key on first contact), edit, delete.
 */
export function SshTargetsPanel({ canManage }: { canManage: boolean }) {
  const { data, isLoading } = useSshTargets();
  const [creating, setCreating] = useState(false);
  const [editing, setEditing] = useState<SshTarget | null>(null);

  return (
    <>
      <div className="mb-3 flex justify-end">
        {canManage && <Button onClick={() => setCreating(true)}><Plus /> New target</Button>}
      </div>

      <Card className="overflow-hidden">
        {isLoading ? (
          <div className="px-5 py-6"><div className="h-20 animate-pulse rounded bg-ink-100 dark:bg-ink-800" /></div>
        ) : !data || data.length === 0 ? (
          <EmptyState
            icon={Server}
            title="No deployment targets"
            description="Register a remote host so WireHQ can deploy a gateway's config to it over SSH."
            action={canManage && <Button onClick={() => setCreating(true)}><Plus /> New target</Button>}
          />
        ) : (
          <table className="w-full text-sm">
            <thead>
              <tr className="border-b text-left text-xs uppercase tracking-wide text-ink-500 dark:border-ink-800">
                <th className="px-5 py-3 font-medium">Name</th>
                <th className="px-5 py-3 font-medium">Host</th>
                <th className="px-5 py-3 font-medium">Auth</th>
                <th className="px-5 py-3 font-medium">Host key</th>
                <th className="px-5 py-3" />
              </tr>
            </thead>
            <tbody>
              {data.map((t) => (
                <TargetRow key={t.id} target={t} canManage={canManage} onEdit={() => setEditing(t)} />
              ))}
            </tbody>
          </table>
        )}
      </Card>

      {creating && <SshTargetDialog onClose={() => setCreating(false)} />}
      {editing && <SshTargetDialog target={editing} onClose={() => setEditing(null)} />}
    </>
  );
}

function TargetRow({ target, canManage, onEdit }: { target: SshTarget; canManage: boolean; onEdit: () => void }) {
  const toast = useToast();
  const test = useTestSshTarget();
  const update = useUpdateSshTarget();
  const del = useDeleteSshTarget();

  function runTest() {
    test.mutate(target.id, {
      onSuccess: (res) => {
        if (!res.reachable) {
          toast(res.error ?? 'Could not reach the host.', 'error');
          return;
        }
        // Pin the host key on first successful contact (trust-on-first-use).
        if (!target.hostKeyFingerprint && res.hostKeyFingerprint) {
          update.mutate({ id: target.id, input: { hostKeyFingerprint: res.hostKeyFingerprint } });
          toast(`Reachable. Host key pinned. WireGuard tools ${res.wireGuardPresent ? 'present' : 'NOT found'}.`);
        } else {
          toast(`Reachable. WireGuard tools ${res.wireGuardPresent ? 'present' : 'NOT found'}.`);
        }
      },
      onError: (err) => toast(err instanceof ApiError ? err.message : 'Connection test failed.', 'error'),
    });
  }

  function remove() {
    if (!window.confirm(`Delete SSH target "${target.name}"?`)) return;
    del.mutate(target.id, {
      onSuccess: () => toast('Target deleted.'),
      onError: (err) => toast(err instanceof ApiError ? err.message : 'Could not delete the target.', 'error'),
    });
  }

  return (
    <tr className="border-b last:border-0 hover:bg-ink-50 dark:border-ink-800 dark:hover:bg-ink-850">
      <td className="px-5 py-3 font-medium text-ink-800 dark:text-ink-100">{target.name}</td>
      <td className="px-5 py-3 font-mono text-ink-500">{target.username}@{target.host}:{target.port}</td>
      <td className="px-5 py-3 text-ink-500">{target.authKind === 'PrivateKey' ? 'Key' : 'Password'}</td>
      <td className="px-5 py-3">
        {target.hostKeyFingerprint
          ? <Badge tone="success" dot>Pinned</Badge>
          : <Badge tone="warning" dot>Unpinned</Badge>}
      </td>
      <td className="px-5 py-3">
        <div className="flex items-center justify-end gap-0.5">
          {canManage && (
            <>
              <Button variant="ghost" size="sm" onClick={runTest} disabled={test.isPending} title="Test connection">
                <Plug /> {test.isPending ? 'Testing…' : 'Test'}
              </Button>
              <Button variant="ghost" size="icon" onClick={onEdit} aria-label="Edit" title="Edit"><Pencil className="text-ink-400" /></Button>
              <Button variant="ghost" size="icon" onClick={remove} disabled={del.isPending} aria-label="Delete" title="Delete"><Trash2 className="text-ink-400" /></Button>
            </>
          )}
        </div>
      </td>
    </tr>
  );
}

function SshTargetDialog({ target, onClose }: { target?: SshTarget; onClose: () => void }) {
  const toast = useToast();
  const create = useCreateSshTarget();
  const update = useUpdateSshTarget();
  const editing = !!target;
  const pending = create.isPending || update.isPending;

  const [form, setForm] = useState({
    name: target?.name ?? '',
    host: target?.host ?? '',
    port: String(target?.port ?? 22),
    username: target?.username ?? 'root',
    authKind: target?.authKind ?? 'PrivateKey',
    credential: '',
    hostKeyFingerprint: target?.hostKeyFingerprint ?? '',
  });
  const [errors, setErrors] = useState<FormErrors>(noFormErrors);

  function submit(e: FormEvent) {
    e.preventDefault();
    setErrors(noFormErrors);
    const onError = (err: unknown) => setErrors(toFormErrors(err, 'Could not save the target.'));

    if (editing) {
      update.mutate(
        {
          id: target!.id,
          input: {
            name: form.name.trim(),
            host: form.host.trim(),
            port: Number(form.port),
            username: form.username.trim(),
            hostKeyFingerprint: form.hostKeyFingerprint.trim() || undefined,
            authKind: form.credential.trim() ? (form.authKind as 'PrivateKey' | 'Password') : undefined,
            credential: form.credential.trim() || undefined,
          },
        },
        { onSuccess: () => { toast('Target updated.'); onClose(); }, onError },
      );
    } else {
      create.mutate(
        {
          name: form.name.trim(),
          host: form.host.trim(),
          port: Number(form.port),
          username: form.username.trim(),
          authKind: form.authKind as 'PrivateKey' | 'Password',
          credential: form.credential,
          hostKeyFingerprint: form.hostKeyFingerprint.trim() || undefined,
        },
        { onSuccess: () => { toast('Target registered.'); onClose(); }, onError },
      );
    }
  }

  return (
    <Dialog
      open
      onClose={onClose}
      title={editing ? 'Edit SSH target' : 'New SSH target'}
      description="A remote host WireHQ deploys a gateway config to. The credential is encrypted and never shown again."
      footer={
        <>
          <Button variant="secondary" onClick={onClose}>Cancel</Button>
          <Button type="submit" form="ssh-target" disabled={pending}>
            {pending ? 'Saving…' : editing ? 'Save changes' : 'Register target'}
          </Button>
        </>
      }
    >
      <form id="ssh-target" onSubmit={submit} className="space-y-4">
        <Field label="Name" htmlFor="st-name" error={errors.fields.name}>
          <Input id="st-name" required placeholder="Edge gateway" value={form.name} onChange={(e) => setForm({ ...form, name: e.target.value })} />
        </Field>
        <div className="grid grid-cols-[1fr_auto] gap-3">
          <Field label="Host" htmlFor="st-host" error={errors.fields.host}>
            <Input id="st-host" required placeholder="vpn.example.com" value={form.host} onChange={(e) => setForm({ ...form, host: e.target.value })} />
          </Field>
          <Field label="Port" htmlFor="st-port" error={errors.fields.port}>
            <Input id="st-port" type="number" className="w-24" value={form.port} onChange={(e) => setForm({ ...form, port: e.target.value })} />
          </Field>
        </div>
        <div className="grid grid-cols-2 gap-3">
          <Field label="Username" htmlFor="st-user" error={errors.fields.username}>
            <Input id="st-user" required value={form.username} onChange={(e) => setForm({ ...form, username: e.target.value })} />
          </Field>
          <Field label="Auth" htmlFor="st-auth" error={errors.fields.authKind}>
            <Select id="st-auth" value={form.authKind} onChange={(e) => setForm({ ...form, authKind: e.target.value as 'PrivateKey' | 'Password' })}>
              <option value="PrivateKey">Private key</option>
              <option value="Password">Password</option>
            </Select>
          </Field>
        </div>
        <Field error={errors.fields.credential} label={editing ? `${form.authKind === 'PrivateKey' ? 'Private key' : 'Password'} (leave blank to keep)` : (form.authKind === 'PrivateKey' ? 'Private key (PEM)' : 'Password')} htmlFor="st-cred">
          {form.authKind === 'PrivateKey' ? (
            <textarea
              id="st-cred"
              required={!editing}
              rows={4}
              placeholder="-----BEGIN OPENSSH PRIVATE KEY-----"
              value={form.credential}
              onChange={(e) => setForm({ ...form, credential: e.target.value })}
              className="w-full rounded-md border bg-ink-0 px-3 py-2 font-mono text-xs text-ink-800 outline-none focus:border-gold-400 dark:border-ink-700 dark:bg-ink-900 dark:text-ink-100"
            />
          ) : (
            <Input id="st-cred" type="password" required={!editing} value={form.credential} onChange={(e) => setForm({ ...form, credential: e.target.value })} />
          )}
        </Field>
        <Field label="Host-key fingerprint (optional — pinned automatically on Test)" htmlFor="st-fp" error={errors.fields.hostKeyFingerprint}>
          <Input id="st-fp" placeholder="SHA256:…" value={form.hostKeyFingerprint} onChange={(e) => setForm({ ...form, hostKeyFingerprint: e.target.value })} />
        </Field>
        {errors.general && <p className="text-sm text-danger-600 dark:text-danger-500">{errors.general}</p>}
      </form>
    </Dialog>
  );
}
