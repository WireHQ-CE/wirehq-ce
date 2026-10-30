import { useState } from 'react';
import { Ban, Cpu, KeyRound, Play, Plus, ShieldOff } from 'lucide-react';
import { Card } from '@/components/ui/card';
import { Button } from '@/components/ui/button';
import { Badge } from '@/components/ui/badge';
import { Dialog } from '@/components/ui/dialog';
import { CodeBlock } from '@/components/ui/code-block';
import { EmptyState } from '@/components/data/EmptyState';
import { useToast } from '@/components/ui/toast';
import { ApiError } from '@/lib/api/client';
import {
  useAgents,
  useDisableAgent,
  useMintEnrollToken,
  useMintRekeyToken,
  useReactivateAgent,
  useRevokeAgent,
} from './api';
import type { Agent, AgentStatus, MintEnrollTokenResult } from './types';

const statusTone: Record<AgentStatus, 'success' | 'info' | 'warning' | 'danger'> = {
  Active: 'success',
  Pending: 'info',
  Disabled: 'warning',
  Revoked: 'danger',
};

/**
 * Enrolled agents: the outbound-only mTLS hosts that pull signed deployment jobs. Operators mint a
 * single-use enrolment token (shown once), then disable or revoke an agent — revocation rejects its
 * certificate immediately at the gateway (no CRL). (docs/12 §5, ADR-028)
 */
export function AgentsPanel({ canManage }: { canManage: boolean }) {
  const { data, isLoading } = useAgents();
  const [minting, setMinting] = useState(false);

  return (
    <>
      <div className="mb-3 flex justify-end">
        {canManage && <Button onClick={() => setMinting(true)}><Plus /> Enroll agent</Button>}
      </div>

      <Card className="overflow-hidden">
        {isLoading ? (
          <div className="px-5 py-6"><div className="h-20 animate-pulse rounded bg-ink-100 dark:bg-ink-800" /></div>
        ) : !data || data.length === 0 ? (
          <EmptyState
            icon={Cpu}
            title="No agents enrolled"
            description="An agent is a host running the WireHQ binary that pulls config over an outbound-only mTLS connection — no inbound ports needed."
            action={canManage && <Button onClick={() => setMinting(true)}><Plus /> Enroll agent</Button>}
          />
        ) : (
          <table className="w-full text-sm">
            <thead>
              <tr className="border-b text-left text-xs uppercase tracking-wide text-ink-500 dark:text-ink-400 dark:border-ink-800">
                <th className="px-5 py-3 font-medium">Name</th>
                <th className="px-5 py-3 font-medium">Status</th>
                <th className="px-5 py-3 font-medium">Platform</th>
                <th className="px-5 py-3 font-medium">Version</th>
                <th className="px-5 py-3 font-medium">Instances</th>
                <th className="px-5 py-3 font-medium">Last seen</th>
                <th className="px-5 py-3 font-medium">Certificate</th>
                <th className="px-5 py-3" />
              </tr>
            </thead>
            <tbody>
              {data.map((agent) => (
                <AgentRow key={agent.id} agent={agent} canManage={canManage} />
              ))}
            </tbody>
          </table>
        )}
      </Card>

      {minting && <MintTokenDialog onClose={() => setMinting(false)} />}
    </>
  );
}

function AgentRow({ agent, canManage }: { agent: Agent; canManage: boolean }) {
  const toast = useToast();
  const disable = useDisableAgent();
  const reactivate = useReactivateAgent();
  const revoke = useRevokeAgent();
  const busy = disable.isPending || reactivate.isPending || revoke.isPending;
  const [rekeying, setRekeying] = useState(false);
  const health = connectivity(agent);
  const cert = certificateHealth(agent);

  function run(label: string, mutation: typeof disable, confirm?: string) {
    if (confirm && !window.confirm(confirm)) return;
    mutation.mutate(agent.id, {
      onSuccess: () => toast(label),
      onError: (err) => toast(err instanceof ApiError ? err.message : 'The action failed.', 'error'),
    });
  }

  return (
    <tr className="border-b last:border-0 hover:bg-ink-50 dark:border-ink-800 dark:hover:bg-ink-850">
      <td className="px-5 py-3">
        <div className="font-medium text-ink-800 dark:text-ink-100">{agent.name}</div>
        <div className="font-mono text-xs text-ink-500 dark:text-ink-400">{agent.certificateFingerprint.slice(0, 16).toLowerCase()}…</div>
      </td>
      <td className="px-5 py-3"><Badge tone={statusTone[agent.status]} dot>{agent.status}</Badge></td>
      <td className="px-5 py-3 text-ink-500 dark:text-ink-400">{agent.platform ?? '—'}</td>
      <td className="px-5 py-3 tabular-nums text-ink-500 dark:text-ink-400">{agent.version ?? '—'}</td>
      <td className="px-5 py-3">
        {agent.managedInstances === 0 ? (
          <span className="text-ink-500 dark:text-ink-400">—</span>
        ) : (
          <span className="flex items-center gap-2">
            <span className="tabular-nums text-ink-700 dark:text-ink-200">{agent.managedInstances}</span>
            {agent.instancesWithDrift > 0 && (
              <Badge tone="warning" dot>{agent.instancesWithDrift} drift</Badge>
            )}
          </span>
        )}
      </td>
      <td className="px-5 py-3 text-ink-500 dark:text-ink-400" title={lastSeenTitle(agent)}>
        <span className="flex items-center gap-2">
          <span className={`inline-block size-2 shrink-0 rounded-full ${health.dotClass}`} aria-hidden />
          {agent.lastSeenAtUtc ? formatRelative(agent.lastSeenAtUtc) : 'never'}
        </span>
      </td>
      <td className="px-5 py-3 text-ink-500 dark:text-ink-400" title={cert.title}>
        {cert.tone ? <Badge tone={cert.tone} dot>{cert.label}</Badge> : cert.label}
      </td>
      <td className="px-5 py-3">
        {canManage && agent.status !== 'Revoked' && (
          <div className="flex justify-end gap-1">
            <Button variant="ghost" size="sm" onClick={() => setRekeying(true)} disabled={busy}>
              <KeyRound className="size-3.5" /> Re-key
            </Button>
            {agent.status === 'Disabled' ? (
              <Button variant="ghost" size="sm" onClick={() => run('Agent reactivated.', reactivate)} disabled={busy}>
                <Play className="size-3.5" /> Reactivate
              </Button>
            ) : (
              <Button variant="ghost" size="sm" onClick={() => run('Agent disabled.', disable)} disabled={busy}>
                <Ban className="size-3.5" /> Disable
              </Button>
            )}
            <Button
              variant="ghost"
              size="sm"
              onClick={() => run('Agent revoked.', revoke, `Revoke "${agent.name}"? Its certificate is rejected permanently — it must re-enroll.`)}
              disabled={busy}
            >
              <ShieldOff className="size-3.5 text-danger-700 dark:text-danger-500" /> Revoke
            </Button>
          </div>
        )}
        {rekeying && <MintTokenDialog rekeyAgent={agent} onClose={() => setRekeying(false)} />}
      </td>
    </tr>
  );
}

/**
 * Mints a single-use token and shows the install command once. Without `rekeyAgent` it enrols a NEW agent; with it,
 * the same command re-issues that agent's certificate instead, so its identity and instance bindings survive (the
 * recovery for an expired certificate) and its current certificate stops working on redemption.
 */
function MintTokenDialog({ onClose, rekeyAgent }: { onClose: () => void; rekeyAgent?: Agent }) {
  const toast = useToast();
  const enrol = useMintEnrollToken();
  const rekey = useMintRekeyToken();
  const mint = rekeyAgent ? rekey : enrol;
  const [result, setResult] = useState<MintEnrollTokenResult | null>(null);

  function generate() {
    const done = {
      onSuccess: setResult,
      onError: (err: unknown) => toast(err instanceof ApiError ? err.message : 'Could not mint a token.', 'error'),
    };
    if (rekeyAgent) {
      rekey.mutate(rekeyAgent.id, done);
    } else {
      enrol.mutate(undefined, done);
    }
  }

  const installCommand = result
    ? `wirehq-agent enroll \\\n  --token ${result.token} \\\n  --server https://<your-wirehq-host>:28443`
    : '';

  return (
    <Dialog
      open
      onClose={onClose}
      title={rekeyAgent ? `Re-key "${rekeyAgent.name}"` : 'Enroll an agent'}
      description={
        rekeyAgent
          ? 'Mint a single-use re-key token, then run the command on that agent\'s host. The agent keeps its identity and instance bindings. The token is shown only once.'
          : 'Mint a single-use token, then run the install command on the host. The token is shown only once.'
      }
      footer={
        result ? (
          <Button onClick={onClose}>Done</Button>
        ) : (
          <>
            <Button variant="secondary" onClick={onClose}>Cancel</Button>
            <Button onClick={generate} disabled={mint.isPending}>
              {mint.isPending ? 'Generating…' : 'Generate token'}
            </Button>
          </>
        )
      }
    >
      {result ? (
        <div className="space-y-4">
          <div>
            <div className="mb-1 text-sm font-medium text-ink-700 dark:text-ink-200">
              {rekeyAgent ? 'Run on the agent’s host' : 'Install command'}
            </div>
            <CodeBlock content={installCommand} />
          </div>
          <p className="text-xs text-ink-500 dark:text-ink-400">
            Expires {new Date(result.expiresAtUtc).toLocaleString()}. Copy it now — for security, the token cannot be shown again.
          </p>
        </div>
      ) : (
        rekeyAgent ? (
          <div className="space-y-2 text-sm text-ink-500 dark:text-ink-400">
            <p>
              Use this when the agent&apos;s certificate has expired, or to cut off a host you no longer trust: once the
              token is redeemed, every certificate the agent held before stops working.
            </p>
            <p>
              Run it on the agent&apos;s host <strong>without deleting its state directory</strong> — that is where the
              agent keeps its WireGuard interface keys. For the gateway container, set{' '}
              <code className="font-mono text-xs">WIREHQ_REKEY_TOKEN</code> and restart it.
            </p>
          </div>
        ) : (
          <p className="text-sm text-ink-500 dark:text-ink-400">
            The agent makes an outbound-only mTLS connection to WireHQ and pulls signed deployment jobs — no inbound ports or stored credentials.
          </p>
        )
      )}
    </Dialog>
  );
}

/** Derives a connectivity health signal from an agent's status + last heartbeat (default interval ~30s). */
function connectivity(agent: Agent): { label: string; dotClass: string } {
  if (agent.status === 'Revoked' || agent.status === 'Disabled') {
    return { label: agent.status, dotClass: 'bg-ink-300 dark:bg-ink-700' };
  }
  if (!agent.lastSeenAtUtc) {
    return { label: 'Never seen', dotClass: 'bg-ink-300 dark:bg-ink-700' };
  }
  const ageSeconds = (Date.now() - new Date(agent.lastSeenAtUtc).getTime()) / 1000;
  if (ageSeconds < 120) return { label: 'Online', dotClass: 'bg-success-500' };
  if (ageSeconds < 900) return { label: 'Idle', dotClass: 'bg-warning-500' };
  return { label: 'Offline', dotClass: 'bg-danger-500' };
}

/**
 * Client-certificate health. The agent renews once two-thirds of its cert's lifetime has passed (~15 days before
 * expiry on the 45-day leaf), so a cert inside 10 days means it has missed renewals for days — usually an agent
 * binary too old to rotate, or a host that has been offline. An expired cert cannot renew itself; Re-key restores it.
 */
function certificateHealth(agent: Agent): { label: string; title: string; tone?: 'warning' | 'danger' } {
  if (agent.status === 'Revoked' || !agent.certificateNotAfterUtc) {
    return { label: '—', title: '' };
  }
  const expires = new Date(agent.certificateNotAfterUtc);
  const days = (expires.getTime() - Date.now()) / 86_400_000;
  const when = expires.toLocaleString();
  if (days <= 0) {
    return {
      label: 'Expired',
      tone: 'danger',
      title: `Expired ${when}. The agent can no longer connect — use Re-key to restore it with its instances intact.`,
    };
  }
  if (days < 10) {
    return {
      label: `${Math.ceil(days)}d left`,
      tone: 'warning',
      title: `Expires ${when} and has not renewed. Upgrade the agent binary (renewal needs agent 0.2.0+) and check the host is online.`,
    };
  }
  return { label: `${Math.floor(days)}d`, title: `Expires ${when}; the agent renews it automatically.` };
}

function lastSeenTitle(agent: Agent): string {
  const when = agent.lastSeenAtUtc ? new Date(agent.lastSeenAtUtc).toLocaleString() : 'never';
  return `${connectivity(agent).label} · last seen ${when}`;
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
