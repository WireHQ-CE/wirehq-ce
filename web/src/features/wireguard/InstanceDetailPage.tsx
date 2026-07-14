import { useState, type FormEvent, type ReactNode } from 'react';
import { Link, useNavigate, useParams } from 'react-router-dom';
import { ArrowLeft, FileText, FileUp, KeyRound, Pencil, Play, Power, RotateCw, Square, Trash2, UserPlus, Users } from 'lucide-react';
import { Card, CardContent, CardHeader, CardTitle } from '@/components/ui/card';
import { Button } from '@/components/ui/button';
import { Badge } from '@/components/ui/badge';
import { Input, Field } from '@/components/ui/input';
import { Dialog } from '@/components/ui/dialog';
import { EmptyState } from '@/components/data/EmptyState';
import { useToast } from '@/components/ui/toast';
import { ApiError } from '@/lib/api/client';
import { noFormErrors, toFormErrors, type FormErrors } from '@/lib/api/form-errors';
import { useAuthStore } from '@/stores/auth-store';
import {
  useControlInstance,
  useDeleteInstance,
  useDeletePeer,
  useInstance,
  usePeers,
  useRotatePeerKeys,
  useSetPeerEnabled,
  useUpdateInstance,
} from './api';
import { PeerWizard } from './PeerWizard';
import { BulkEnrollmentWizard } from './BulkEnrollmentWizard';
import { DeploymentPanel } from './DeploymentPanel';
import { PeerConfigDialog } from './PeerConfigDialog';
import { EditPeerDialog } from './EditPeerDialog';
import { ServerConfigDialog } from './ServerConfigDialog';
import type { InstanceDetail, PeerListItem } from './types';

const statusTone: Record<string, 'success' | 'warning' | 'danger' | 'neutral' | 'info'> = {
  Running: 'success',
  Created: 'info',
  Stopped: 'neutral',
  Degraded: 'warning',
  Error: 'danger',
};

const peerTone: Record<string, 'success' | 'warning' | 'danger' | 'neutral'> = {
  Active: 'success',
  Pending: 'warning',
  Disabled: 'neutral',
  Revoked: 'danger',
};

const byteUnits = ['B', 'KB', 'MB', 'GB', 'TB'];

function formatBytes(bytes: number): string {
  if (bytes <= 0) return '0 B';
  const i = Math.min(Math.floor(Math.log(bytes) / Math.log(1024)), byteUnits.length - 1);
  const value = bytes / 1024 ** i;
  return `${i === 0 || value >= 100 ? Math.round(value) : value.toFixed(1)} ${byteUnits[i]}`;
}

function formatRelative(iso: string): string {
  const seconds = Math.round((Date.now() - new Date(iso).getTime()) / 1000);
  if (seconds < 60) return `${Math.max(seconds, 0)}s ago`;
  const minutes = Math.round(seconds / 60);
  if (minutes < 60) return `${minutes}m ago`;
  const hours = Math.round(minutes / 60);
  if (hours < 24) return `${hours}h ago`;
  return `${Math.round(hours / 24)}d ago`;
}

export function InstanceDetailPage() {
  const { id = '' } = useParams();
  const { data: instance, isLoading, isError } = useInstance(id);
  const canManage = useAuthStore((s) => s.hasPermission('wg.instances.manage'));
  const canExport = useAuthStore((s) => s.hasPermission('wg.instances.export'));
  // CSV bulk enrolment is a Pro+ feature (and permission-gated).
  const canEnroll = useAuthStore((s) => s.hasPermission('wg.enrollment.manage') && s.hasFeature('bulk_enrollment'));

  if (isLoading) {
    return <div className="h-40 animate-pulse rounded-lg bg-ink-100 dark:bg-ink-800" />;
  }

  if (isError || !instance) {
    return (
      <div className="space-y-3">
        <BackLink />
        <p className="text-sm text-ink-500">This instance could not be found.</p>
      </div>
    );
  }

  return (
    <>
      <BackLink />
      <div className="mb-6 mt-3 flex flex-wrap items-start justify-between gap-3">
        <div>
          <div className="flex items-center gap-3">
            <h1 className="text-h1 text-ink-900 dark:text-ink-50">{instance.name}</h1>
            <Badge tone={statusTone[instance.status] ?? 'neutral'} dot>{instance.status}</Badge>
          </div>
          <p className="mt-1 font-mono text-sm text-ink-400">{instance.slug} · {instance.providerType}</p>
        </div>
        <div className="flex flex-wrap items-center gap-2">
          {canExport && <ServerConfigButton instance={instance} />}
          {canManage && <InstanceActions instance={instance} />}
        </div>
      </div>

      <Card className="mb-6">
        <CardHeader><CardTitle>Interface</CardTitle></CardHeader>
        <CardContent className="grid grid-cols-1 gap-x-8 gap-y-4 pt-0 sm:grid-cols-2 lg:grid-cols-3">
          <Detail label="Public key" value={instance.publicKey} mono />
          <Detail label="Interface address" value={instance.interfaceAddress} mono />
          <Detail label="Listen port" value={String(instance.listenPort)} mono />
          <Detail label="Endpoint" value={instance.endpointHost ?? '—'} mono />
          <Detail label="MTU" value={String(instance.mtu)} mono />
          <Detail label="DNS" value={instance.dns.length ? instance.dns.join(', ') : '—'} mono />
          {instance.description && <Detail label="Description" value={instance.description} />}
        </CardContent>
      </Card>

      <DeploymentPanel instanceId={instance.id} canManage={canManage} />

      <PeersSection instanceId={instance.id} canManage={canManage} canEnroll={canEnroll} />
    </>
  );
}

function BackLink() {
  return (
    <Link to="/app/wireguard" className="inline-flex items-center gap-1.5 text-sm text-ink-500 transition-colors hover:text-ink-800 dark:hover:text-ink-200">
      <ArrowLeft className="size-4" /> WireGuard
    </Link>
  );
}

// ---- Peers ----

function PeersSection({ instanceId, canManage, canEnroll }: { instanceId: string; canManage: boolean; canEnroll: boolean }) {
  const toast = useToast();
  const { data, isLoading } = usePeers(instanceId);
  const setEnabled = useSetPeerEnabled(instanceId);
  const del = useDeletePeer(instanceId);
  const rotate = useRotatePeerKeys(instanceId);
  const [adding, setAdding] = useState(false);
  const [bulkEnrolling, setBulkEnrolling] = useState(false);
  const [configPeer, setConfigPeer] = useState<PeerListItem | null>(null);
  const [editingPeer, setEditingPeer] = useState<PeerListItem | null>(null);

  function toggle(peer: PeerListItem) {
    setEnabled.mutate(
      { peerId: peer.id, enabled: peer.status !== 'Active' },
      { onError: (err) => toast(err instanceof ApiError ? err.message : 'Could not update the peer.', 'error') },
    );
  }

  function rotateKeys(peer: PeerListItem) {
    if (!window.confirm(`Rotate keys for "${peer.name}"? The old config stops working immediately.`)) return;
    rotate.mutate(peer.id, {
      onSuccess: () => toast('Keys rotated. Open Config to download the new file.'),
      onError: (err) => toast(err instanceof ApiError ? err.message : 'Could not rotate keys.', 'error'),
    });
  }

  function remove(peer: PeerListItem) {
    if (!window.confirm(`Delete peer "${peer.name}"? This cannot be undone.`)) return;
    del.mutate(peer.id, {
      onSuccess: () => toast('Peer deleted.'),
      onError: (err) => toast(err instanceof ApiError ? err.message : 'Could not delete the peer.', 'error'),
    });
  }

  return (
    <Card className="overflow-hidden">
      <CardHeader>
        <CardTitle>Peers{data ? ` (${data.length})` : ''}</CardTitle>
        <div className="flex items-center gap-2">
          {canEnroll && <Button variant="secondary" size="sm" onClick={() => setBulkEnrolling(true)}><FileUp /> Bulk enroll</Button>}
          {canManage && <Button size="sm" onClick={() => setAdding(true)}><UserPlus /> Add peer</Button>}
        </div>
      </CardHeader>

      {isLoading ? (
        <div className="px-5 pb-5"><div className="h-24 animate-pulse rounded bg-ink-100 dark:bg-ink-800" /></div>
      ) : !data || data.length === 0 ? (
        <EmptyState
          icon={Users}
          title="No peers yet"
          description="Add a device to generate its keys, address, and a ready-to-use config + QR code."
          action={canManage && <Button onClick={() => setAdding(true)}><UserPlus /> Add peer</Button>}
        />
      ) : (
        <table className="w-full text-sm">
          <thead>
            <tr className="border-b text-left text-xs uppercase tracking-wide text-ink-500 dark:border-ink-800">
              <th className="px-5 py-3 font-medium">Name</th>
              <th className="px-5 py-3 font-medium">Address</th>
              <th className="px-5 py-3 font-medium">Status</th>
              <th className="px-5 py-3 font-medium">Last handshake</th>
              <th className="px-5 py-3 font-medium">Transfer</th>
              <th className="px-5 py-3" />
            </tr>
          </thead>
          <tbody>
            {data.map((p) => (
              <tr key={p.id} className="border-b last:border-0 hover:bg-ink-50 dark:border-ink-800 dark:hover:bg-ink-850">
                <td className="px-5 py-3">
                  <div className="font-medium text-ink-800 dark:text-ink-100">{p.name}</div>
                  {p.deviceType && <div className="text-xs text-ink-400">{p.deviceType}</div>}
                </td>
                <td className="px-5 py-3 font-mono text-ink-500">{p.assignedAddress}</td>
                <td className="px-5 py-3"><Badge tone={peerTone[p.status] ?? 'neutral'} dot>{p.status}</Badge></td>
                <td
                  className="px-5 py-3 text-ink-400"
                  title={p.lastHandshakeAtUtc ? new Date(p.lastHandshakeAtUtc).toLocaleString() : ''}
                >
                  {p.lastHandshakeAtUtc ? formatRelative(p.lastHandshakeAtUtc) : '—'}
                </td>
                <td className="px-5 py-3 whitespace-nowrap text-ink-400">
                  {p.rxBytes > 0 || p.txBytes > 0 ? (
                    <span className="font-mono text-xs">↓{formatBytes(p.rxBytes)} ↑{formatBytes(p.txBytes)}</span>
                  ) : (
                    '—'
                  )}
                </td>
                <td className="px-5 py-3">
                  <div className="flex items-center justify-end gap-0.5">
                    <IconAction label="Config & QR" onClick={() => setConfigPeer(p)}><FileText /></IconAction>
                    {canManage && (
                      <>
                        <IconAction label="Rotate keys" onClick={() => rotateKeys(p)}><KeyRound /></IconAction>
                        <IconAction label={p.status === 'Active' ? 'Disable' : 'Enable'} onClick={() => toggle(p)}><Power /></IconAction>
                        <IconAction label="Edit" onClick={() => setEditingPeer(p)}><Pencil /></IconAction>
                        <IconAction label="Delete" onClick={() => remove(p)}><Trash2 /></IconAction>
                      </>
                    )}
                  </div>
                </td>
              </tr>
            ))}
          </tbody>
        </table>
      )}

      {adding && <PeerWizard instanceId={instanceId} onClose={() => setAdding(false)} />}
      {bulkEnrolling && <BulkEnrollmentWizard instanceId={instanceId} onClose={() => setBulkEnrolling(false)} />}
      {configPeer && <PeerConfigDialog peer={configPeer} onClose={() => setConfigPeer(null)} />}
      {editingPeer && <EditPeerDialog instanceId={instanceId} peer={editingPeer} onClose={() => setEditingPeer(null)} />}
    </Card>
  );
}

function IconAction({ label, onClick, children }: { label: string; onClick: () => void; children: ReactNode }) {
  return (
    <Button variant="ghost" size="icon" onClick={onClick} aria-label={label} title={label} className="text-ink-400 hover:text-ink-700 dark:hover:text-ink-100">
      {children}
    </Button>
  );
}

function ServerConfigButton({ instance }: { instance: InstanceDetail }) {
  const [open, setOpen] = useState(false);
  return (
    <>
      <Button variant="secondary" size="sm" onClick={() => setOpen(true)}><FileText /> Server config</Button>
      {open && <ServerConfigDialog instance={instance} onClose={() => setOpen(false)} />}
    </>
  );
}

// ---- Instance actions ----

function InstanceActions({ instance }: { instance: InstanceDetail }) {
  const toast = useToast();
  const navigate = useNavigate();
  const del = useDeleteInstance();
  const control = useControlInstance(instance.id);
  const [editing, setEditing] = useState(false);

  function remove() {
    if (!window.confirm(`Delete instance "${instance.name}" and all its peers? This cannot be undone.`)) return;
    del.mutate(instance.id, {
      onSuccess: () => {
        toast('Instance deleted.');
        navigate('/app/wireguard');
      },
      onError: (err) => toast(err instanceof ApiError ? err.message : 'Could not delete the instance.', 'error'),
    });
  }

  function send(action: 'start' | 'stop' | 'restart') {
    control.mutate(action, {
      onSuccess: () => toast(`Instance ${action} requested.`),
      onError: (err) => toast(err instanceof ApiError ? err.message : `Could not ${action} the instance.`, 'error'),
    });
  }

  return (
    <>
      {/* Control is shown only when the provider supports it — the config-only Local provider does not. */}
      {instance.canControl && (
        <>
          <Button variant="secondary" size="sm" onClick={() => send('start')} disabled={control.isPending}><Play /> Start</Button>
          <Button variant="secondary" size="sm" onClick={() => send('stop')} disabled={control.isPending}><Square /> Stop</Button>
          <Button variant="secondary" size="sm" onClick={() => send('restart')} disabled={control.isPending}><RotateCw /> Restart</Button>
        </>
      )}
      <Button variant="secondary" size="sm" onClick={() => setEditing(true)}><Pencil /> Edit</Button>
      <Button variant="destructive" size="sm" onClick={remove} disabled={del.isPending}><Trash2 /> Delete</Button>
      {editing && <EditInstanceDialog instance={instance} onClose={() => setEditing(false)} />}
    </>
  );
}

function EditInstanceDialog({ instance, onClose }: { instance: InstanceDetail; onClose: () => void }) {
  const toast = useToast();
  const update = useUpdateInstance(instance.id);
  const [form, setForm] = useState({
    name: instance.name,
    description: instance.description ?? '',
    endpointHost: instance.endpointHost ?? '',
    mtu: String(instance.mtu),
    dns: instance.dns.join(', '),
  });
  const [errors, setErrors] = useState<FormErrors>(noFormErrors);

  function submit(e: FormEvent) {
    e.preventDefault();
    setErrors(noFormErrors);
    update.mutate(
      {
        name: form.name.trim(),
        description: form.description.trim(),
        endpointHost: form.endpointHost.trim(),
        mtu: Number(form.mtu),
        dns: form.dns.trim() ? form.dns.split(',').map((d) => d.trim()).filter(Boolean) : [],
      },
      {
        onSuccess: () => {
          toast('Instance updated.');
          onClose();
        },
        onError: (err) => setErrors(toFormErrors(err, 'Could not update the instance.')),
      },
    );
  }

  return (
    <Dialog
      open
      onClose={onClose}
      title="Edit instance"
      footer={
        <>
          <Button variant="secondary" onClick={onClose}>Cancel</Button>
          <Button type="submit" form="edit-instance" disabled={update.isPending}>
            {update.isPending ? 'Saving…' : 'Save changes'}
          </Button>
        </>
      }
    >
      <form id="edit-instance" onSubmit={submit} className="space-y-4">
        <Field label="Name" htmlFor="ename" error={errors.fields.name}>
          <Input id="ename" required value={form.name} onChange={(e) => setForm({ ...form, name: e.target.value })} />
        </Field>
        <Field label="Description" htmlFor="edesc" error={errors.fields.description}>
          <Input id="edesc" value={form.description} onChange={(e) => setForm({ ...form, description: e.target.value })} />
        </Field>
        <Field label="Endpoint host" htmlFor="eep" error={errors.fields.endpointHost}>
          <Input id="eep" placeholder="vpn.example.com:51820" value={form.endpointHost} onChange={(e) => setForm({ ...form, endpointHost: e.target.value })} />
        </Field>
        <div className="grid grid-cols-2 gap-3">
          <Field label="MTU" htmlFor="emtu" error={errors.fields.mtu}>
            <Input id="emtu" type="number" value={form.mtu} onChange={(e) => setForm({ ...form, mtu: e.target.value })} />
          </Field>
          <Field label="DNS (comma-separated)" htmlFor="edns" error={errors.fields.dns}>
            <Input id="edns" value={form.dns} onChange={(e) => setForm({ ...form, dns: e.target.value })} />
          </Field>
        </div>
        {errors.general && <p className="text-sm text-danger-600 dark:text-danger-500">{errors.general}</p>}
      </form>
    </Dialog>
  );
}

function Detail({ label, value, mono }: { label: string; value: ReactNode; mono?: boolean }) {
  return (
    <div className="min-w-0">
      <div className="text-xs font-medium uppercase tracking-wide text-ink-500">{label}</div>
      <div className={`mt-0.5 truncate text-sm text-ink-800 dark:text-ink-100 ${mono ? 'font-mono' : ''}`} title={typeof value === 'string' ? value : undefined}>
        {value}
      </div>
    </div>
  );
}
