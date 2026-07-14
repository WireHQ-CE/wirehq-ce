import { useEffect, useState, type FormEvent } from 'react';
import { useQueryClient } from '@tanstack/react-query';
import { RefreshCw, Rocket, Server, Settings2 } from 'lucide-react';
import { Card, CardContent, CardHeader, CardTitle } from '@/components/ui/card';
import { Button } from '@/components/ui/button';
import { Badge } from '@/components/ui/badge';
import { Field } from '@/components/ui/input';
import { Input } from '@/components/ui/input';
import { Select } from '@/components/ui/select';
import { Toggle } from '@/components/ui/toggle';
import { Dialog } from '@/components/ui/dialog';
import { useToast } from '@/components/ui/toast';
import { ApiError } from '@/lib/api/client';
import { useAuthStore } from '@/stores/auth-store';
import { noFormErrors, toFormErrors, type FormErrors } from '@/lib/api/form-errors';
import {
  orchKeys,
  useAgents,
  useBindInstanceTarget,
  useDeployment,
  useDeployments,
  useInstanceTarget,
  useRefreshInstanceStatus,
  useRequestDeployment,
  useSshTargets,
} from './api';
import type { DeploymentStatus, DeploymentSummary } from './types';

const statusTone: Record<DeploymentStatus, 'success' | 'warning' | 'danger' | 'info' | 'neutral'> = {
  Pending: 'neutral',
  Dispatched: 'info',
  Applying: 'info',
  Succeeded: 'success',
  Failed: 'danger',
  RolledBack: 'danger',
};

/**
 * The instance's deployment surface: where it deploys (Local or an SSH target), an explicit Deploy
 * button that pushes the current desired config, and the deployment history. (Explicit-deploy model:
 * changes stage in the model; the operator decides when to push.)
 */
export function DeploymentPanel({ instanceId, canManage }: { instanceId: string; canManage: boolean }) {
  const qc = useQueryClient();
  const toast = useToast();
  const target = useInstanceTarget(instanceId);
  const deployments = useDeployments(instanceId);
  const deploy = useRequestDeployment(instanceId);
  const refresh = useRefreshInstanceStatus(instanceId);

  const [binding, setBinding] = useState(false);
  const [activeJobId, setActiveJobId] = useState<string | null>(null);
  const [viewing, setViewing] = useState<string | null>(null);

  const active = useDeployment(activeJobId);

  // When the in-flight deployment finishes, surface the result and refresh history.
  useEffect(() => {
    const status = active.data?.status;
    if (!status || !['Succeeded', 'Failed', 'RolledBack'].includes(status)) return;
    if (status === 'Succeeded') toast('Deployment succeeded.');
    else toast(active.data?.error ?? 'Deployment failed.', 'error');
    void qc.invalidateQueries({ queryKey: orchKeys.deployments(instanceId) });
    setActiveJobId(null);
  }, [active.data?.status, active.data?.error, qc, instanceId, toast]);

  function runDeploy() {
    deploy.mutate(undefined, {
      onSuccess: (res) => setActiveJobId(res.jobId),
      onError: (err) => toast(err instanceof ApiError ? err.message : 'Could not start the deployment.', 'error'),
    });
  }

  function runRefresh() {
    refresh.mutate(undefined, {
      onSuccess: (s) => toast(`Live status: ${s.state}.`),
      onError: (err) => toast(err instanceof ApiError ? err.message : 'Could not read live status.', 'error'),
    });
  }

  const t = target.data;
  const isSsh = t?.kind === 'Ssh';
  const isAgent = t?.kind === 'Agent';
  const deploying = deploy.isPending || !!activeJobId;

  return (
    <Card className="mb-6 overflow-hidden">
      <CardHeader>
        <CardTitle>Deployment</CardTitle>
        <div className="flex items-center gap-2">
          {isSsh && (
            <Button variant="secondary" size="sm" onClick={runRefresh} disabled={refresh.isPending}>
              <RefreshCw /> {refresh.isPending ? 'Refreshing…' : 'Refresh status'}
            </Button>
          )}
          {canManage && (
            <>
              <Button variant="secondary" size="sm" onClick={() => setBinding(true)}><Settings2 /> Change target</Button>
              <Button size="sm" onClick={runDeploy} disabled={deploying}>
                <Rocket /> {deploying ? 'Deploying…' : 'Deploy'}
              </Button>
            </>
          )}
        </div>
      </CardHeader>

      <CardContent className="space-y-4 pt-0">
        <div className="flex flex-wrap items-center gap-x-6 gap-y-2 text-sm">
          <div className="flex items-center gap-2">
            <Server className="size-4 text-ink-400" />
            <span className="text-ink-500">Target</span>
            {isSsh ? (
              <span className="font-medium text-ink-800 dark:text-ink-100">SSH · {t?.sshTargetName ?? '—'} <span className="font-mono text-ink-400">({t?.interfaceName})</span></span>
            ) : isAgent ? (
              <span className="font-medium text-ink-800 dark:text-ink-100">
                Agent · {t?.agentName ?? '—'} <span className="font-mono text-ink-400">({t?.interfaceName})</span>
                {t?.keyCustody === 'AgentManaged' && <span className="ml-1 text-ink-400">· agent-managed key</span>}
              </span>
            ) : (
              <span className="font-medium text-ink-800 dark:text-ink-100">Local <span className="text-ink-400">(config-only)</span></span>
            )}
          </div>
          {activeJobId && active.data && (
            <Badge tone={statusTone[active.data.status]} dot>{active.data.status}…</Badge>
          )}
          {isSsh && t?.hasDrift && <Badge tone="warning" dot>Config drift</Badge>}
          {isAgent && t?.agentKeyPending && <Badge tone="warning" dot>Agent key pending</Badge>}
          {(isSsh || isAgent) && t?.autoReconverge && <Badge tone="info" dot>Auto re-converge</Badge>}
        </div>

        {!isSsh && !isAgent && (
          <p className="rounded-md border border-ink-200 bg-ink-50 px-3 py-2 text-xs text-ink-500 dark:border-ink-800 dark:bg-ink-900">
            This instance is config-only — deploys are recorded but nothing is pushed. Bind it to an SSH target or an agent to deploy to a real host.
          </p>
        )}

        {isAgent && (
          <p className="rounded-md border border-ink-200 bg-ink-50 px-3 py-2 text-xs text-ink-500 dark:border-ink-800 dark:bg-ink-900">
            Bound to an agent — a Deploy is queued and applied by the agent on its next outbound poll (no inbound access needed).
            {t?.agentKeyPending && ' This instance uses an agent-managed key — the agent generates it locally and reports the public key on the first successful deploy.'}
          </p>
        )}

        {isSsh && t?.hasDrift && (
          <p className="rounded-md border border-warning-700/20 bg-warning-50 px-3 py-2 text-xs text-warning-700 dark:bg-warning-700/20 dark:text-warning-500">
            <strong>Config drift detected.</strong> The config deployed on the host differs from WireHQ's desired config
            {t.driftObservedAtUtc ? ` (observed ${new Date(t.driftObservedAtUtc).toLocaleString()})` : ''}.
            {t.autoReconverge ? ' Auto re-converge is on — a redeploy is queued automatically.' : <> Click <strong>Deploy</strong> to re-converge.</>}
          </p>
        )}

        <DeploymentHistory deployments={deployments.data} onView={setViewing} />
      </CardContent>

      {binding && t && <BindTargetDialog instanceId={instanceId} current={t} onClose={() => setBinding(false)} />}
      {viewing && <DeploymentDetailDialog jobId={viewing} onClose={() => setViewing(null)} />}
    </Card>
  );
}

function DeploymentHistory({ deployments, onView }: { deployments?: DeploymentSummary[]; onView: (id: string) => void }) {
  if (!deployments || deployments.length === 0) {
    return <p className="text-sm text-ink-400">No deployments yet.</p>;
  }

  return (
    <div className="overflow-hidden rounded-md border dark:border-ink-800">
      <table className="w-full text-sm">
        <thead>
          <tr className="border-b text-left text-xs uppercase tracking-wide text-ink-500 dark:border-ink-800">
            <th className="px-4 py-2 font-medium">Status</th>
            <th className="px-4 py-2 font-medium">When</th>
            <th className="px-4 py-2 font-medium">Detail</th>
            <th className="px-4 py-2" />
          </tr>
        </thead>
        <tbody>
          {deployments.slice(0, 8).map((d) => (
            <tr key={d.id} className="border-b last:border-0 dark:border-ink-800">
              <td className="px-4 py-2"><Badge tone={statusTone[d.status]} dot>{d.status}</Badge></td>
              <td className="px-4 py-2 text-ink-500">{new Date(d.createdAtUtc).toLocaleString()}</td>
              <td className="px-4 py-2 max-w-[16rem] truncate text-ink-400" title={d.error ?? ''}>{d.error ?? '—'}</td>
              <td className="px-4 py-2 text-right">
                <Button variant="ghost" size="sm" onClick={() => onView(d.id)}>View</Button>
              </td>
            </tr>
          ))}
        </tbody>
      </table>
    </div>
  );
}

function BindTargetDialog({
  instanceId,
  current,
  onClose,
}: {
  instanceId: string;
  current: {
    kind: string;
    sshTargetId: string | null;
    agentId: string | null;
    keyCustody: 'WireHqManaged' | 'AgentManaged';
    autoReconverge: boolean;
    interfaceName: string;
  };
  onClose: () => void;
}) {
  const toast = useToast();
  const targets = useSshTargets();
  const agents = useAgents();
  const bind = useBindInstanceTarget(instanceId);
  const [kind, setKind] = useState<'Local' | 'Ssh' | 'Agent'>(
    current.kind === 'Ssh' ? 'Ssh' : current.kind === 'Agent' ? 'Agent' : 'Local',
  );
  const [sshTargetId, setSshTargetId] = useState(current.sshTargetId ?? '');
  const [agentId, setAgentId] = useState(current.agentId ?? '');
  const [keyCustody, setKeyCustody] = useState<'WireHqManaged' | 'AgentManaged'>(current.keyCustody ?? 'WireHqManaged');
  const [autoReconverge, setAutoReconverge] = useState(current.autoReconverge ?? false);
  const [interfaceName, setInterfaceName] = useState(current.interfaceName || 'wg0');
  const [errors, setErrors] = useState<FormErrors>(noFormErrors);
  const canAutoReconverge = useAuthStore((s) => s.hasFeature('drift.auto_reconverge'));

  const remote = kind === 'Ssh' || kind === 'Agent';

  function submit(e: FormEvent) {
    e.preventDefault();
    setErrors(noFormErrors);
    bind.mutate(
      {
        kind,
        sshTargetId: kind === 'Ssh' ? sshTargetId : undefined,
        agentId: kind === 'Agent' ? agentId : undefined,
        keyCustody: kind === 'Agent' ? keyCustody : undefined,
        interfaceName: remote ? interfaceName.trim() || 'wg0' : undefined,
        autoReconverge: remote ? autoReconverge : undefined,
      },
      {
        onSuccess: () => { toast('Deployment target updated.'); onClose(); },
        onError: (err) => setErrors(toFormErrors(err, 'Could not update the target.')),
      },
    );
  }

  const noTargets = targets.data && targets.data.length === 0;
  const activeAgents = agents.data?.filter((a) => a.status === 'Active') ?? [];
  const noAgents = agents.data && activeAgents.length === 0;

  return (
    <Dialog
      open
      onClose={onClose}
      title="Deployment target"
      description="Where this gateway's config is deployed. Local keeps it config-only; SSH pushes to a remote host; an agent pulls it over an outbound-only connection."
      footer={
        <>
          <Button variant="secondary" onClick={onClose}>Cancel</Button>
          <Button
            type="submit"
            form="bind-target"
            disabled={bind.isPending || (kind === 'Ssh' && !sshTargetId) || (kind === 'Agent' && !agentId)}
          >
            {bind.isPending ? 'Saving…' : 'Save'}
          </Button>
        </>
      }
    >
      <form id="bind-target" onSubmit={submit} className="space-y-4">
        <Field label="Target type" htmlFor="bt-kind" error={errors.fields.kind}>
          <Select id="bt-kind" value={kind} onChange={(e) => setKind(e.target.value as 'Local' | 'Ssh' | 'Agent')}>
            <option value="Local">Local (config-only)</option>
            <option value="Ssh">SSH host</option>
            <option value="Agent">Agent (outbound-only)</option>
          </Select>
        </Field>
        {kind === 'Agent' && (
          noAgents ? (
            <p className="text-sm text-ink-500">No active agents — enroll one under WireGuard → Agents first.</p>
          ) : (
            <>
              <Field label="Agent" htmlFor="bt-agent" error={errors.fields.agentId}>
                <Select id="bt-agent" required value={agentId} onChange={(e) => setAgentId(e.target.value)}>
                  <option value="" disabled>Select an agent…</option>
                  {activeAgents.map((a) => (
                    <option key={a.id} value={a.id}>{a.name}{a.platform ? ` (${a.platform})` : ''}</option>
                  ))}
                </Select>
              </Field>
              <Field label="Interface name" htmlFor="bt-agent-if" error={errors.fields.interfaceName}>
                <Input id="bt-agent-if" value={interfaceName} onChange={(e) => setInterfaceName(e.target.value)} placeholder="wg0" />
              </Field>
              <Field label="Key custody" htmlFor="bt-custody" error={errors.fields.keyCustody}>
                <Select id="bt-custody" value={keyCustody} onChange={(e) => setKeyCustody(e.target.value as 'WireHqManaged' | 'AgentManaged')}>
                  <option value="WireHqManaged">WireHQ-managed (server config + QR export available)</option>
                  <option value="AgentManaged">Agent-managed (key never leaves the host)</option>
                </Select>
              </Field>
              <p className="text-xs text-ink-500">
                {keyCustody === 'AgentManaged'
                  ? 'The agent generates the interface key locally and reports only the public key on the first deploy. WireHQ cannot export the full server config for this instance.'
                  : 'WireHQ generates and holds the interface key (encrypted), so full server-config and QR exports stay available.'}
              </p>
            </>
          )
        )}
        {kind === 'Ssh' && (
          noTargets ? (
            <p className="text-sm text-ink-500">No SSH targets yet — register one under WireGuard → Targets first.</p>
          ) : (
            <>
              <Field label="SSH target" htmlFor="bt-ssh" error={errors.fields.sshTargetId}>
                <Select id="bt-ssh" required value={sshTargetId} onChange={(e) => setSshTargetId(e.target.value)}>
                  <option value="" disabled>Select a target…</option>
                  {targets.data?.map((t) => (
                    <option key={t.id} value={t.id}>{t.name} ({t.username}@{t.host})</option>
                  ))}
                </Select>
              </Field>
              <Field label="Interface name" htmlFor="bt-if" error={errors.fields.interfaceName}>
                <Input id="bt-if" value={interfaceName} onChange={(e) => setInterfaceName(e.target.value)} placeholder="wg0" />
              </Field>
            </>
          )
        )}
        {remote && ((kind === 'Ssh' && !noTargets) || (kind === 'Agent' && !noAgents)) && (
          <div className="flex items-center justify-between gap-4 rounded-md border border-ink-200 px-3 py-2.5 dark:border-ink-800">
            <div>
              <label htmlFor="bt-autoreconverge" className="text-sm font-medium text-ink-700 dark:text-ink-200">Auto re-converge on drift</label>
              <p className="text-xs text-ink-500">
                {canAutoReconverge
                  ? "Automatically redeploy when the deployed config drifts from WireHQ's desired state."
                  : 'Automatically redeploy on drift — included in Pro and Enterprise plans.'}
              </p>
            </div>
            <Toggle id="bt-autoreconverge" checked={canAutoReconverge && autoReconverge} onChange={setAutoReconverge} disabled={!canAutoReconverge} aria-label="Auto re-converge on drift" />
          </div>
        )}
        {errors.general && <p className="text-sm text-danger-600 dark:text-danger-500">{errors.general}</p>}
      </form>
    </Dialog>
  );
}

function DeploymentDetailDialog({ jobId, onClose }: { jobId: string; onClose: () => void }) {
  const { data } = useDeployment(jobId);

  return (
    <Dialog
      open
      onClose={onClose}
      title="Deployment"
      description={data ? `${data.status}${data.error ? ` — ${data.error}` : ''}` : 'Loading…'}
      footer={<Button onClick={onClose}>Close</Button>}
    >
      {!data ? (
        <div className="h-24 animate-pulse rounded bg-ink-100 dark:bg-ink-800" />
      ) : (
        <ol className="space-y-3">
          {data.events.map((e, i) => (
            <li key={i} className="flex gap-3">
              <div className="mt-1 size-2 shrink-0 rounded-full bg-gold-400" />
              <div>
                <div className="text-sm font-medium capitalize text-ink-800 dark:text-ink-100">{e.phase.replace(/_/g, ' ')}</div>
                {e.detail && <div className="text-xs text-ink-500">{e.detail}</div>}
                <div className="text-xs text-ink-400">{new Date(e.atUtc).toLocaleString()}</div>
              </div>
            </li>
          ))}
        </ol>
      )}
    </Dialog>
  );
}
