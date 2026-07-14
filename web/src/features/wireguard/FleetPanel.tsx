import { Link } from 'react-router-dom';
import { Activity, AlertTriangle, Cpu, Server, Users } from 'lucide-react';
import { Card } from '@/components/ui/card';
import { Badge } from '@/components/ui/badge';
import { EmptyState } from '@/components/data/EmptyState';
import { useFleet } from './api';
import type { FleetInstance } from './types';

const statusTone: Record<string, 'success' | 'warning' | 'danger' | 'neutral' | 'info'> = {
  Running: 'success',
  Created: 'info',
  Stopped: 'neutral',
  Degraded: 'warning',
  Error: 'danger',
};

/**
 * The fleet dashboard: a cross-instance operational overview — health, config drift, and peer connectivity
 * across every deployment target (Local / SSH / Agent), with a fleet-wide summary. Read-only; drills into
 * each instance. (docs/12 §13 Phase 3)
 */
export function FleetPanel() {
  const { data, isLoading } = useFleet();
  const summary = data?.summary;

  return (
    <>
      <div className="mb-4 grid grid-cols-2 gap-3 sm:grid-cols-3 lg:grid-cols-5">
        <FleetStat icon={Activity} label="Running" value={summary?.running} />
        <FleetStat icon={Server} label="Degraded" value={summary?.degraded} tone={summary && summary.degraded > 0 ? 'warning' : undefined} />
        <FleetStat icon={AlertTriangle} label="Config drift" value={summary?.drifted} tone={summary && summary.drifted > 0 ? 'warning' : undefined} />
        <FleetStat icon={Cpu} label="Agents online" value={summary ? `${summary.agentsOnline}/${summary.agentsTotal}` : undefined} />
        <FleetStat icon={Users} label="Peers connected" value={summary ? `${summary.peersConnected}/${summary.peersTotal}` : undefined} />
      </div>

      <Card className="overflow-hidden">
        {isLoading ? (
          <div className="px-5 py-6"><div className="h-24 animate-pulse rounded bg-ink-100 dark:bg-ink-800" /></div>
        ) : !data || data.instances.length === 0 ? (
          <EmptyState
            icon={Server}
            title="No instances yet"
            description="Create a WireGuard instance and bind it to a target to see it here."
          />
        ) : (
          <table className="w-full text-sm">
            <thead>
              <tr className="border-b text-left text-xs uppercase tracking-wide text-ink-500 dark:border-ink-800">
                <th className="px-5 py-3 font-medium">Instance</th>
                <th className="px-5 py-3 font-medium">Target</th>
                <th className="px-5 py-3 font-medium">Status</th>
                <th className="px-5 py-3 font-medium">Peers</th>
                <th className="px-5 py-3 font-medium">Transfer</th>
                <th className="px-5 py-3 font-medium">Last activity</th>
              </tr>
            </thead>
            <tbody>
              {data.instances.map((i) => (
                <FleetRow key={i.instanceId} row={i} />
              ))}
            </tbody>
          </table>
        )}
      </Card>
    </>
  );
}

function FleetRow({ row }: { row: FleetInstance }) {
  return (
    <tr className="border-b last:border-0 hover:bg-ink-50 dark:border-ink-800 dark:hover:bg-ink-850">
      <td className="px-5 py-3">
        <Link to={`/app/wireguard/instances/${row.instanceId}`} className="font-medium text-ink-800 hover:text-gold-700 dark:text-ink-100 dark:hover:text-gold-400">
          {row.name}
        </Link>
        <div className="text-xs text-ink-400">{row.networkName ?? '—'}</div>
      </td>
      <td className="px-5 py-3">
        <span className="text-ink-700 dark:text-ink-200">{row.targetKind}</span>
        {row.targetName && <span className="text-ink-400"> · {row.targetName}</span>}
      </td>
      <td className="px-5 py-3">
        <span className="flex items-center gap-2">
          <Badge tone={statusTone[row.status] ?? 'neutral'} dot>{row.status}</Badge>
          {row.hasDrift && <Badge tone="warning" dot>Drift</Badge>}
        </span>
      </td>
      <td className="px-5 py-3 tabular-nums text-ink-500">
        {row.peersConnected}<span className="text-ink-400">/{row.peersTotal}</span>
      </td>
      <td className="px-5 py-3 tabular-nums text-ink-500">
        {row.peersTotal === 0 ? '—' : `↓ ${formatBytes(row.rxBytes)} · ↑ ${formatBytes(row.txBytes)}`}
      </td>
      <td className="px-5 py-3 text-ink-500" title={row.observedAtUtc ? new Date(row.observedAtUtc).toLocaleString() : ''}>
        {row.observedAtUtc ? formatRelative(row.observedAtUtc) : '—'}
      </td>
    </tr>
  );
}

function FleetStat({ icon: Icon, label, value, tone }: { icon: typeof Server; label: string; value?: number | string; tone?: 'warning' }) {
  return (
    <Card className="flex items-center gap-3 p-4">
      <div className={`grid size-9 place-items-center rounded-md ${tone === 'warning' ? 'bg-warning-500/10 text-warning-700 dark:text-warning-500' : 'bg-gold-400/10 text-gold-700 dark:text-gold-400'}`}>
        <Icon className="size-5" />
      </div>
      <div>
        <div className="text-h3 tabular-nums text-ink-900 dark:text-ink-50">{value ?? '—'}</div>
        <div className="text-xs text-ink-500">{label}</div>
      </div>
    </Card>
  );
}

function formatBytes(bytes: number): string {
  if (bytes <= 0) return '0 B';
  const units = ['B', 'KB', 'MB', 'GB', 'TB'];
  const i = Math.min(Math.floor(Math.log(bytes) / Math.log(1024)), units.length - 1);
  return `${(bytes / 1024 ** i).toFixed(i === 0 ? 0 : 1)} ${units[i]}`;
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
