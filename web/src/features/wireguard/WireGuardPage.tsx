import { useState, type FormEvent } from 'react';
import { Link } from 'react-router-dom';
import { Activity, Lock, Network, Plus, Server, Trash2, Users } from 'lucide-react';
import { EDITION } from '@/lib/edition';
import { PageHeader } from '@/components/layout/AppShell';
import { Card } from '@/components/ui/card';
import { Button } from '@/components/ui/button';
import { Badge } from '@/components/ui/badge';
import { Input, Field } from '@/components/ui/input';
import { Select } from '@/components/ui/select';
import { Dialog } from '@/components/ui/dialog';
import { Tabs } from '@/components/ui/tabs';
import { EmptyState } from '@/components/data/EmptyState';
import { useToast } from '@/components/ui/toast';
import { ApiError } from '@/lib/api/client';
import { noFormErrors, toFormErrors, type FormErrors } from '@/lib/api/form-errors';
import { useAuthStore } from '@/stores/auth-store';
import {
  useCreateInstance,
  useCreateNetwork,
  useDeleteNetwork,
  useInstances,
  useNetworks,
  useWireGuardOverview,
} from './api';
import { SshTargetsPanel } from './SshTargetsPanel';
import { AgentsPanel } from './AgentsPanel';
import { FleetPanel } from './FleetPanel';
import type { NetworkListItem } from './types';

const instanceTone: Record<string, 'success' | 'warning' | 'danger' | 'neutral' | 'info'> = {
  Running: 'success',
  Created: 'info',
  Stopped: 'neutral',
  Degraded: 'warning',
  Error: 'danger',
};

export function WireGuardPage() {
  const canSeeFleet = useAuthStore((s) => s.hasFeature('fleet.dashboard'));
  const [tab, setTab] = useState<'fleet' | 'instances' | 'networks' | 'targets' | 'agents'>(canSeeFleet ? 'fleet' : 'instances');
  const overview = useWireGuardOverview();
  const canManage = useAuthStore((s) => s.hasPermission('wg.instances.manage'));
  const canReadTargets = useAuthStore((s) => s.hasPermission('orch.targets.read'));
  const canManageTargets = useAuthStore((s) => s.hasPermission('orch.targets.manage'));
  const canReadAgents = useAuthStore((s) => s.hasPermission('orch.agents.read'));
  const canManageAgents = useAuthStore((s) => s.hasPermission('orch.agents.manage'));
  // In the Community Edition, remote deployment (SSH Targets + Agents) is the paid "Remote
  // Deployment" Marketplace module (docs/18) — the tabs stay visible but disabled until the module
  // licensing lands (phase 2). UI gate only for now; the SaaS build is untouched.
  const remoteDeploymentLocked = EDITION === 'community';

  return (
    <>
      <PageHeader
        title="WireGuard"
        subtitle="Manage gateways (instances), networks, and the peers that connect to them."
      />

      <div className="mb-6 grid grid-cols-2 gap-3 sm:grid-cols-4">
        <StatCard icon={Server} label="Instances" value={overview.data?.instances} />
        <StatCard icon={Network} label="Networks" value={overview.data?.networks} />
        <StatCard icon={Users} label="Peers" value={overview.data?.peers} />
        <StatCard icon={Activity} label="Active peers" value={overview.data?.activePeers} />
      </div>

      <Tabs
        value={tab}
        onChange={setTab}
        tabs={[
          ...(canSeeFleet ? [{ value: 'fleet' as const, label: 'Fleet' }] : []),
          { value: 'instances', label: 'Instances' },
          { value: 'networks', label: 'Networks' },
          ...(canReadTargets
            ? [{
                value: 'targets' as const,
                label: remoteDeploymentLocked ? (
                  <span className="inline-flex items-center gap-1.5"><Lock className="size-3" /> Targets</span>
                ) : (
                  'Targets'
                ),
                disabled: remoteDeploymentLocked,
                title: remoteDeploymentLocked ? 'Part of the Remote Deployment module' : undefined,
              }]
            : []),
          ...(canReadAgents
            ? [{
                value: 'agents' as const,
                label: remoteDeploymentLocked ? (
                  <span className="inline-flex items-center gap-1.5"><Lock className="size-3" /> Agents</span>
                ) : (
                  'Agents'
                ),
                disabled: remoteDeploymentLocked,
                title: remoteDeploymentLocked ? 'Part of the Remote Deployment module' : undefined,
              }]
            : []),
        ]}
        className="mb-4"
      />

      {remoteDeploymentLocked && (canReadTargets || canReadAgents) && (
        <div className="mb-4 flex items-center gap-2.5 rounded-lg border border-ink-200 bg-ink-50 px-3.5 py-2.5 text-sm text-ink-600 dark:border-ink-800 dark:bg-ink-900 dark:text-ink-300">
          <Lock className="size-4 shrink-0 text-gold-500 dark:text-gold-400" />
          <span className="flex-1">
            <span className="font-medium text-ink-800 dark:text-ink-100">Targets &amp; Agents</span> — push
            configs to your gateways over SSH or the outbound agent — are part of the{' '}
            <span className="font-medium text-ink-800 dark:text-ink-100">Remote Deployment</span> module.
          </span>
          <Link to="/app/modules" className="shrink-0 text-gold-600 hover:underline dark:text-gold-400">
            View in Modules
          </Link>
        </div>
      )}

      {tab === 'fleet' && canSeeFleet && <FleetPanel />}
      {tab === 'instances' && <InstancesTab canManage={canManage} />}
      {tab === 'networks' && <NetworksTab canManage={canManage} />}
      {tab === 'targets' && !remoteDeploymentLocked && <SshTargetsPanel canManage={canManageTargets} />}
      {tab === 'agents' && !remoteDeploymentLocked && <AgentsPanel canManage={canManageAgents} />}
    </>
  );
}

function StatCard({ icon: Icon, label, value }: { icon: typeof Server; label: string; value?: number }) {
  return (
    <Card className="flex items-center gap-3 p-4">
      <div className="grid size-9 place-items-center rounded-md bg-gold-400/10 text-gold-700 dark:text-gold-400">
        <Icon className="size-5" />
      </div>
      <div>
        <div className="text-h3 tabular-nums text-ink-900 dark:text-ink-50">{value ?? '—'}</div>
        <div className="text-xs text-ink-500">{label}</div>
      </div>
    </Card>
  );
}

// ---- Instances ----

function InstancesTab({ canManage }: { canManage: boolean }) {
  const { data, isLoading } = useInstances();
  const [creating, setCreating] = useState(false);

  return (
    <>
      <div className="mb-3 flex justify-end">
        {canManage && (
          <Button onClick={() => setCreating(true)}>
            <Plus /> New instance
          </Button>
        )}
      </div>

      <Card className="overflow-hidden">
        {isLoading ? (
          <TableSkeleton />
        ) : !data || data.length === 0 ? (
          <EmptyState
            icon={Server}
            title="No instances yet"
            description="An instance is a WireGuard gateway (the server interface peers connect to)."
            action={canManage && <Button onClick={() => setCreating(true)}><Plus /> New instance</Button>}
          />
        ) : (
          <table className="w-full text-sm">
            <thead>
              <tr className="border-b text-left text-xs uppercase tracking-wide text-ink-500 dark:border-ink-800">
                <th className="px-5 py-3 font-medium">Name</th>
                <th className="px-5 py-3 font-medium">Address</th>
                <th className="px-5 py-3 font-medium">Port</th>
                <th className="px-5 py-3 font-medium">Status</th>
                <th className="px-5 py-3 font-medium">Peers</th>
              </tr>
            </thead>
            <tbody>
              {data.map((i) => (
                <tr key={i.id} className="border-b last:border-0 hover:bg-ink-50 dark:border-ink-800 dark:hover:bg-ink-850">
                  <td className="px-5 py-3">
                    <Link to={`/app/wireguard/instances/${i.id}`} className="font-medium text-ink-800 hover:text-gold-700 dark:text-ink-100 dark:hover:text-gold-400">
                      {i.name}
                    </Link>
                    <div className="text-xs text-ink-400">{i.slug} · {i.providerType}</div>
                  </td>
                  <td className="px-5 py-3 font-mono text-ink-500">{i.interfaceAddress}</td>
                  <td className="px-5 py-3 tabular-nums text-ink-500">{i.listenPort}</td>
                  <td className="px-5 py-3"><Badge tone={instanceTone[i.status] ?? 'neutral'} dot>{i.status}</Badge></td>
                  <td className="px-5 py-3 tabular-nums text-ink-500">{i.peerCount}</td>
                </tr>
              ))}
            </tbody>
          </table>
        )}
      </Card>

      {creating && <CreateInstanceDialog onClose={() => setCreating(false)} />}
    </>
  );
}

function CreateInstanceDialog({ onClose }: { onClose: () => void }) {
  const toast = useToast();
  const networks = useNetworks();
  const create = useCreateInstance();
  const [form, setForm] = useState({
    networkId: '',
    name: '',
    listenPort: '51820',
    interfaceAddress: '',
    endpointHost: '',
  });
  const [errors, setErrors] = useState<FormErrors>(noFormErrors);

  function submit(e: FormEvent) {
    e.preventDefault();
    setErrors(noFormErrors);
    create.mutate(
      {
        networkId: form.networkId,
        name: form.name.trim(),
        listenPort: Number(form.listenPort),
        interfaceAddress: form.interfaceAddress.trim(),
        endpointHost: form.endpointHost.trim() || undefined,
      },
      {
        onSuccess: () => {
          toast('Instance created.');
          onClose();
        },
        onError: (err) => setErrors(toFormErrors(err, 'Could not create the instance.')),
      },
    );
  }

  const noNetworks = networks.data && networks.data.length === 0;

  return (
    <Dialog
      open
      onClose={onClose}
      title="New instance"
      description="A WireGuard gateway interface. Its server keypair is generated automatically."
      footer={
        <>
          <Button variant="secondary" onClick={onClose}>Cancel</Button>
          <Button type="submit" form="create-instance" disabled={create.isPending || !!noNetworks}>
            {create.isPending ? 'Creating…' : 'Create instance'}
          </Button>
        </>
      }
    >
      {noNetworks ? (
        <p className="text-sm text-ink-500">Create a network first — instances allocate peer addresses from a network's CIDR.</p>
      ) : (
        <form id="create-instance" onSubmit={submit} className="space-y-4">
          <Field label="Network" htmlFor="net" error={errors.fields.networkId}>
            <Select id="net" required value={form.networkId} onChange={(e) => setForm({ ...form, networkId: e.target.value })}>
              <option value="" disabled>Select a network…</option>
              {networks.data?.map((n) => (
                <option key={n.id} value={n.id}>{n.name} ({n.cidr})</option>
              ))}
            </Select>
          </Field>
          <Field label="Name" htmlFor="name" error={errors.fields.name}>
            <Input id="name" required placeholder="Corp Gateway" value={form.name} onChange={(e) => setForm({ ...form, name: e.target.value })} />
          </Field>
          <div className="grid grid-cols-2 gap-3">
            <Field label="Interface address" htmlFor="addr" error={errors.fields.interfaceAddress}>
              <Input id="addr" required placeholder="10.8.0.1/24" value={form.interfaceAddress} onChange={(e) => setForm({ ...form, interfaceAddress: e.target.value })} />
            </Field>
            <Field label="Listen port" htmlFor="port" error={errors.fields.listenPort}>
              <Input id="port" required type="number" value={form.listenPort} onChange={(e) => setForm({ ...form, listenPort: e.target.value })} />
            </Field>
          </div>
          <Field label="Endpoint host (optional)" htmlFor="ep" error={errors.fields.endpointHost}>
            <Input id="ep" placeholder="vpn.example.com:51820" value={form.endpointHost} onChange={(e) => setForm({ ...form, endpointHost: e.target.value })} />
          </Field>
          {errors.general && <p className="text-sm text-danger-600 dark:text-danger-500">{errors.general}</p>}
        </form>
      )}
    </Dialog>
  );
}

// ---- Networks ----

function NetworksTab({ canManage }: { canManage: boolean }) {
  const { data, isLoading } = useNetworks();
  const [creating, setCreating] = useState(false);

  return (
    <>
      <div className="mb-3 flex justify-end">
        {canManage && <Button onClick={() => setCreating(true)}><Plus /> New network</Button>}
      </div>

      <Card className="overflow-hidden">
        {isLoading ? (
          <TableSkeleton />
        ) : !data || data.length === 0 ? (
          <EmptyState
            icon={Network}
            title="No networks yet"
            description="A network is the CIDR address pool that peer IPs are allocated from."
            action={canManage && <Button onClick={() => setCreating(true)}><Plus /> New network</Button>}
          />
        ) : (
          <table className="w-full text-sm">
            <thead>
              <tr className="border-b text-left text-xs uppercase tracking-wide text-ink-500 dark:border-ink-800">
                <th className="px-5 py-3 font-medium">Name</th>
                <th className="px-5 py-3 font-medium">CIDR</th>
                <th className="px-5 py-3 font-medium">Instances</th>
                <th className="px-5 py-3" />
              </tr>
            </thead>
            <tbody>
              {data.map((n) => (
                <NetworkRow key={n.id} network={n} canManage={canManage} />
              ))}
            </tbody>
          </table>
        )}
      </Card>

      {creating && <CreateNetworkDialog onClose={() => setCreating(false)} />}
    </>
  );
}

function NetworkRow({ network, canManage }: { network: NetworkListItem; canManage: boolean }) {
  const toast = useToast();
  const del = useDeleteNetwork();

  function remove() {
    if (!window.confirm(`Delete network "${network.name}"? This cannot be undone.`)) return;
    del.mutate(network.id, {
      onSuccess: () => toast('Network deleted.'),
      onError: (err) => toast(err instanceof ApiError ? err.message : 'Could not delete the network.', 'error'),
    });
  }

  return (
    <tr className="border-b last:border-0 hover:bg-ink-50 dark:border-ink-800 dark:hover:bg-ink-850">
      <td className="px-5 py-3 font-medium text-ink-800 dark:text-ink-100">{network.name}</td>
      <td className="px-5 py-3 font-mono text-ink-500">{network.cidr}</td>
      <td className="px-5 py-3 tabular-nums text-ink-500">{network.instanceCount}</td>
      <td className="px-5 py-3 text-right">
        {canManage && (
          <Button variant="ghost" size="icon" onClick={remove} disabled={del.isPending} aria-label="Delete network">
            <Trash2 className="text-ink-400" />
          </Button>
        )}
      </td>
    </tr>
  );
}

function CreateNetworkDialog({ onClose }: { onClose: () => void }) {
  const toast = useToast();
  const create = useCreateNetwork();
  const [name, setName] = useState('');
  const [cidr, setCidr] = useState('');
  const [dns, setDns] = useState('');
  const [errors, setErrors] = useState<FormErrors>(noFormErrors);

  function submit(e: FormEvent) {
    e.preventDefault();
    setErrors(noFormErrors);
    create.mutate(
      {
        name: name.trim(),
        cidr: cidr.trim(),
        dns: dns.trim() ? dns.split(',').map((d) => d.trim()).filter(Boolean) : undefined,
      },
      {
        onSuccess: () => {
          toast('Network created.');
          onClose();
        },
        onError: (err) => setErrors(toFormErrors(err, 'Could not create the network.')),
      },
    );
  }

  return (
    <Dialog
      open
      onClose={onClose}
      title="New network"
      description="A CIDR address pool that peer IPs are allocated from."
      footer={
        <>
          <Button variant="secondary" onClick={onClose}>Cancel</Button>
          <Button type="submit" form="create-network" disabled={create.isPending}>
            {create.isPending ? 'Creating…' : 'Create network'}
          </Button>
        </>
      }
    >
      <form id="create-network" onSubmit={submit} className="space-y-4">
        <Field label="Name" htmlFor="nname" error={errors.fields.name}>
          <Input id="nname" required placeholder="Corp" value={name} onChange={(e) => setName(e.target.value)} />
        </Field>
        <Field label="CIDR" htmlFor="cidr" error={errors.fields.cidr}>
          <Input id="cidr" required placeholder="10.8.0.0/24" value={cidr} onChange={(e) => setCidr(e.target.value)} />
        </Field>
        <Field label="DNS servers (optional, comma-separated)" htmlFor="dns" error={errors.fields.dns}>
          <Input id="dns" placeholder="1.1.1.1, 9.9.9.9" value={dns} onChange={(e) => setDns(e.target.value)} />
        </Field>
        {errors.general && <p className="text-sm text-danger-600 dark:text-danger-500">{errors.general}</p>}
      </form>
    </Dialog>
  );
}

function TableSkeleton() {
  return (
    <div className="divide-y dark:divide-ink-800">
      {Array.from({ length: 4 }).map((_, i) => (
        <div key={i} className="flex items-center gap-4 px-5 py-3.5">
          <div className="h-4 w-40 animate-pulse rounded bg-ink-100 dark:bg-ink-800" />
          <div className="h-4 w-32 animate-pulse rounded bg-ink-100 dark:bg-ink-800" />
          <div className="ml-auto h-5 w-16 animate-pulse rounded-full bg-ink-100 dark:bg-ink-800" />
        </div>
      ))}
    </div>
  );
}
