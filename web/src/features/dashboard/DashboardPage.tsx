import { useQuery } from '@tanstack/react-query';
import { Link } from 'react-router-dom';
import { Activity, AlertTriangle, ArrowRight, Boxes, Cpu, Network, Server, Users } from 'lucide-react';
import { PageHeader } from '@/components/layout/AppShell';
import { StatCard } from '@/components/data/StatCard';
import { Card, CardContent, CardHeader, CardTitle } from '@/components/ui/card';
import { Badge } from '@/components/ui/badge';
import { EmptyState } from '@/components/data/EmptyState';
import { api } from '@/lib/api/client';
import { useAuthStore } from '@/stores/auth-store';
import { EDITION } from '@/lib/edition';
import { useCurrentOrganization } from '@/features/organizations/api';
import type { Fleet, FleetInstance } from '@/features/wireguard/types';

interface PlanUsage {
  instances: number;
  peers: number;
  gateways: number;
  networks: number;
  seats: number;
}

const statusTone: Record<string, 'success' | 'warning' | 'danger' | 'neutral' | 'info'> = {
  Running: 'success',
  Created: 'info',
  Stopped: 'neutral',
  Degraded: 'warning',
  Error: 'danger',
};

export function DashboardPage() {
  const user = useAuthStore((s) => s.user);
  const hasFeature = useAuthStore((s) => s.hasFeature);
  const hasPermission = useAuthStore((s) => s.hasPermission);
  const { data: org, isLoading: orgLoading } = useCurrentOrganization();

  // Plan usage is available to any member (no permission/feature gate) — drives the headline counts.
  const usage = useQuery({ queryKey: ['entitlements', 'usage'], queryFn: () => api.get<PlanUsage>('/api/v1/entitlements/usage') });
  const u = usage.data;

  // The live fleet view is a Pro feature (and needs WireGuard read) — only query it when entitled, so we
  // never trip a 403.
  const canSeeFleet = hasFeature('fleet.dashboard') && hasPermission('wg.instances.read');
  const fleet = useQuery({
    queryKey: ['wg', 'fleet'],
    queryFn: () => api.get<Fleet>('/api/v1/wireguard/fleet'),
    enabled: canSeeFleet,
    refetchInterval: 30_000,
  });

  return (
    <>
      <PageHeader
        title={`Welcome, ${user?.name?.split(' ')[0] ?? 'there'}`}
        subtitle="Your WireGuard control plane at a glance."
      />

      <div className="grid grid-cols-1 gap-4 sm:grid-cols-2 lg:grid-cols-4">
        <StatCard label="Networks" value={u?.networks ?? '—'} icon={Network} />
        <StatCard label="Instances (gateways)" value={u?.instances ?? '—'} icon={Server} />
        <StatCard label="Peers (devices)" value={u?.peers ?? '—'} icon={Boxes} />
        <StatCard label="Connected gateways" value={u?.gateways ?? '—'} hint="SSH / Agent" icon={Cpu} />
      </div>

      <div className="mt-6 grid grid-cols-1 gap-4 lg:grid-cols-3">
        <Card className="lg:col-span-2">
          <CardHeader className="flex-row items-center justify-between">
            <CardTitle>Fleet activity</CardTitle>
            {canSeeFleet && (fleet.data?.instances.length ?? 0) > 0 && (
              <Link to="/app/wireguard" className="inline-flex items-center gap-1 text-sm font-medium text-gold-600 hover:underline dark:text-gold-400">
                View fleet <ArrowRight className="size-3.5" />
              </Link>
            )}
          </CardHeader>
          <CardContent>
            <FleetActivity canSeeFleet={canSeeFleet} hasFeatureFleet={hasFeature('fleet.dashboard')} fleet={fleet.data} loading={fleet.isLoading} />
          </CardContent>
        </Card>

        <Card>
          <CardHeader>
            <CardTitle>Organization</CardTitle>
          </CardHeader>
          <CardContent className="space-y-3 text-sm">
            <Row label="Name" value={org?.name ?? '—'} />
            <Row label="Edition" value={org?.edition ?? '—'} />
            <Row label="Members" value={orgLoading ? '—' : String(org?.memberCount ?? 0)} />
            <Row label="Teams" value={orgLoading ? '—' : String(org?.teamCount ?? 0)} />
            <Row label="Status" value={org?.status ?? '—'} />
          </CardContent>
        </Card>
      </div>
    </>
  );
}

function FleetActivity({
  canSeeFleet,
  hasFeatureFleet,
  fleet,
  loading,
}: {
  canSeeFleet: boolean;
  hasFeatureFleet: boolean;
  fleet?: Fleet;
  loading: boolean;
}) {
  // No WireGuard plan feature → show what the live view offers + the right unlock path for the edition. On the
  // Community Edition (lean free core, no billing/plan page) the fleet dashboard is a Marketplace module the
  // operator activates in Settings → Modules; on SaaS it is a Pro plan upgrade. (docs/29 M-10)
  if (!canSeeFleet) {
    if (!hasFeatureFleet) {
      const community = EDITION === 'community';
      return (
        <EmptyState
          icon={Activity}
          title={community ? 'Live fleet status is a Marketplace module' : 'Live fleet status is a Pro feature'}
          description={
            community
              ? 'Online/offline status, last handshakes, config drift and throughput across every gateway — activate the Fleet Dashboard module to light this up.'
              : 'Online/offline status, last handshakes, config drift and throughput across every gateway — upgrade to Pro to light this up.'
          }
          action={
            <Link
              to={community ? '/app/modules' : '/app/settings/plan'}
              className="inline-flex items-center gap-1 rounded-md bg-gold-500 px-3 py-1.5 text-sm font-medium text-ink-950 hover:bg-gold-600"
            >
              {community ? 'Browse modules' : 'Upgrade to Pro'} <ArrowRight className="size-3.5" />
            </Link>
          }
        />
      );
    }
    return (
      <EmptyState
        icon={Activity}
        title="No fleet access"
        description="You don't have permission to view WireGuard status. Ask an organization admin for access."
      />
    );
  }

  if (loading) {
    return <div className="h-40 animate-pulse rounded bg-ink-100 dark:bg-ink-800" />;
  }

  if (!fleet || fleet.instances.length === 0) {
    return (
      <EmptyState
        icon={Server}
        title="No instances yet"
        description="Create your first WireGuard network and instance, then bind it to a target to see live activity here."
        action={
          <Link to="/app/wireguard" className="inline-flex items-center gap-1 rounded-md bg-gold-500 px-3 py-1.5 text-sm font-medium text-ink-950 hover:bg-gold-600">
            Get started <ArrowRight className="size-3.5" />
          </Link>
        }
      />
    );
  }

  const s = fleet.summary;
  const topInstances = fleet.instances.slice(0, 5);

  return (
    <div className="space-y-4">
      <div className="grid grid-cols-2 gap-3 sm:grid-cols-4">
        <MiniStat icon={Activity} label="Running" value={`${s.running}/${s.totalInstances}`} />
        <MiniStat icon={AlertTriangle} label="Config drift" value={s.drifted} tone={s.drifted > 0 ? 'warning' : undefined} />
        <MiniStat icon={Cpu} label="Agents online" value={`${s.agentsOnline}/${s.agentsTotal}`} />
        <MiniStat icon={Users} label="Peers connected" value={`${s.peersConnected}/${s.peersTotal}`} />
      </div>

      <ul className="divide-y divide-ink-100 dark:divide-ink-800">
        {topInstances.map((i) => (
          <InstanceRow key={i.instanceId} instance={i} />
        ))}
      </ul>
    </div>
  );
}

function InstanceRow({ instance: i }: { instance: FleetInstance }) {
  return (
    <li className="flex items-center justify-between gap-3 py-2.5 text-sm">
      <div className="min-w-0">
        <div className="flex items-center gap-2">
          <Badge tone={statusTone[i.status] ?? 'neutral'}>{i.status}</Badge>
          {i.hasDrift && <Badge tone="warning">Drift</Badge>}
          <span className="truncate font-medium text-ink-800 dark:text-ink-100">{i.name}</span>
        </div>
        <div className="mt-0.5 truncate text-xs text-ink-400">
          {i.networkName ?? 'No network'} · {i.targetKind}
        </div>
      </div>
      <div className="shrink-0 text-right">
        <div className="tabular-nums text-ink-700 dark:text-ink-200">
          {i.peersConnected}<span className="text-ink-400">/{i.peersTotal} peers</span>
        </div>
        <div className="text-xs text-ink-400">
          {i.peersTotal === 0 ? '—' : `↓ ${formatBytes(i.rxBytes)} · ↑ ${formatBytes(i.txBytes)}`}
        </div>
      </div>
    </li>
  );
}

function MiniStat({
  icon: Icon,
  label,
  value,
  tone,
}: {
  icon: typeof Activity;
  label: string;
  value: string | number;
  tone?: 'warning';
}) {
  return (
    <div className="rounded-lg border border-ink-200 px-3 py-2 dark:border-ink-800">
      <div className="flex items-center gap-1.5 text-xs text-ink-400">
        <Icon className="size-3.5" /> {label}
      </div>
      <div className={`mt-1 text-lg font-semibold tabular-nums ${tone === 'warning' ? 'text-warning-700 dark:text-warning-500' : 'text-ink-900 dark:text-ink-50'}`}>
        {value}
      </div>
    </div>
  );
}

function Row({ label, value }: { label: string; value: string }) {
  return (
    <div className="flex items-center justify-between">
      <span className="text-ink-500">{label}</span>
      <span className="text-ink-800 dark:text-ink-200">{value}</span>
    </div>
  );
}

function formatBytes(bytes: number): string {
  if (bytes <= 0) return '0 B';
  const units = ['B', 'KB', 'MB', 'GB', 'TB'];
  const exponent = Math.min(Math.floor(Math.log(bytes) / Math.log(1024)), units.length - 1);
  const value = bytes / 1024 ** exponent;
  return `${value.toFixed(value < 10 && exponent > 0 ? 1 : 0)} ${units[exponent]}`;
}
