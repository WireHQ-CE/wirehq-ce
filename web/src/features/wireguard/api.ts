import { useMutation, useQuery, useQueryClient } from '@tanstack/react-query';
import { api } from '@/lib/api/client';
import type {
  Agent,
  ConfigVersionItem,
  CreatePeerResponse,
  DeploymentDetail,
  DeploymentSummary,
  EnrollmentPreviewResult,
  EnrollmentResult,
  Fleet,
  InstanceDetail,
  InstanceListItem,
  InstanceStatus,
  InstanceTarget,
  MintEnrollTokenResult,
  NetworkListItem,
  PeerListItem,
  RotatePeerKeysResponse,
  SshTarget,
  SshTargetTestResult,
  WireGuardOverview,
} from './types';

const base = '/api/v1/wireguard';

export const wgKeys = {
  overview: ['wg', 'overview'] as const,
  networks: ['wg', 'networks'] as const,
  instances: ['wg', 'instances'] as const,
  instance: (id: string) => ['wg', 'instance', id] as const,
};

// ---- Queries ----

export function useWireGuardOverview() {
  return useQuery({ queryKey: wgKeys.overview, queryFn: () => api.get<WireGuardOverview>(`${base}/overview`) });
}

export function useNetworks() {
  return useQuery({ queryKey: wgKeys.networks, queryFn: () => api.get<NetworkListItem[]>(`${base}/networks`) });
}

export function useInstances() {
  return useQuery({ queryKey: wgKeys.instances, queryFn: () => api.get<InstanceListItem[]>(`${base}/instances`) });
}

export function useInstance(id: string) {
  return useQuery({ queryKey: wgKeys.instance(id), queryFn: () => api.get<InstanceDetail>(`${base}/instances/${id}`) });
}

// ---- Networks ----

export interface CreateNetworkInput {
  name: string;
  cidr: string;
  dns?: string[];
}

export function useCreateNetwork() {
  const qc = useQueryClient();
  return useMutation({
    mutationFn: (input: CreateNetworkInput) => api.post(`${base}/networks`, input),
    onSuccess: () => {
      void qc.invalidateQueries({ queryKey: wgKeys.networks });
      void qc.invalidateQueries({ queryKey: wgKeys.overview });
    },
  });
}

export function useDeleteNetwork() {
  const qc = useQueryClient();
  return useMutation({
    mutationFn: (id: string) => api.delete(`${base}/networks/${id}`),
    onSuccess: () => {
      void qc.invalidateQueries({ queryKey: wgKeys.networks });
      void qc.invalidateQueries({ queryKey: wgKeys.overview });
    },
  });
}

// ---- Instances ----

export interface CreateInstanceInput {
  networkId: string;
  name: string;
  listenPort: number;
  interfaceAddress: string;
  endpointHost?: string;
  dns?: string[];
  mtu?: number;
}

export function useCreateInstance() {
  const qc = useQueryClient();
  return useMutation({
    mutationFn: (input: CreateInstanceInput) => api.post<{ id: string }>(`${base}/instances`, input),
    onSuccess: () => {
      void qc.invalidateQueries({ queryKey: wgKeys.instances });
      void qc.invalidateQueries({ queryKey: wgKeys.overview });
    },
  });
}

export interface UpdateInstanceInput {
  name?: string;
  description?: string;
  endpointHost?: string;
  dns?: string[];
  mtu?: number;
}

export function useUpdateInstance(id: string) {
  const qc = useQueryClient();
  return useMutation({
    mutationFn: (input: UpdateInstanceInput) => api.patch(`${base}/instances/${id}`, input),
    onSuccess: () => {
      void qc.invalidateQueries({ queryKey: wgKeys.instance(id) });
      void qc.invalidateQueries({ queryKey: wgKeys.instances });
    },
  });
}

export function useDeleteInstance() {
  const qc = useQueryClient();
  return useMutation({
    mutationFn: (id: string) => api.delete(`${base}/instances/${id}`),
    onSuccess: () => {
      void qc.invalidateQueries({ queryKey: wgKeys.instances });
      void qc.invalidateQueries({ queryKey: wgKeys.overview });
    },
  });
}

export function useControlInstance(id: string) {
  const qc = useQueryClient();
  return useMutation({
    mutationFn: (action: 'start' | 'stop' | 'restart') => api.post(`${base}/instances/${id}/control`, { action }),
    onSuccess: () => void qc.invalidateQueries({ queryKey: wgKeys.instance(id) }),
  });
}

// ---- Peers ----

export const peerKeys = {
  list: (instanceId: string) => ['wg', 'peers', instanceId] as const,
  versions: (peerId: string) => ['wg', 'peer-versions', peerId] as const,
};

function invalidatePeers(qc: ReturnType<typeof useQueryClient>, instanceId: string) {
  void qc.invalidateQueries({ queryKey: peerKeys.list(instanceId) });
  void qc.invalidateQueries({ queryKey: wgKeys.instance(instanceId) });
  void qc.invalidateQueries({ queryKey: wgKeys.instances });
  void qc.invalidateQueries({ queryKey: wgKeys.overview });
}

export function usePeers(instanceId: string) {
  return useQuery({
    queryKey: peerKeys.list(instanceId),
    queryFn: () => api.get<PeerListItem[]>(`${base}/instances/${instanceId}/peers`),
  });
}

export interface CreatePeerInput {
  name: string;
  email?: string;
  deviceType?: string;
  generateKeypair?: boolean;
  publicKey?: string;
  usePresharedKey?: boolean;
  assignedAddress?: string;
  allowedIps?: string[];
  persistentKeepalive?: number;
}

export function useCreatePeer(instanceId: string) {
  const qc = useQueryClient();
  return useMutation({
    mutationFn: (input: CreatePeerInput) => api.post<CreatePeerResponse>(`${base}/instances/${instanceId}/peers`, input),
    onSuccess: () => invalidatePeers(qc, instanceId),
  });
}

export interface UpdatePeerInput {
  name?: string;
  deviceType?: string;
  allowedIps?: string[];
  persistentKeepalive?: number;
}

export function useUpdatePeer(instanceId: string) {
  const qc = useQueryClient();
  return useMutation({
    mutationFn: ({ peerId, input }: { peerId: string; input: UpdatePeerInput }) => api.patch(`${base}/peers/${peerId}`, input),
    onSuccess: () => invalidatePeers(qc, instanceId),
  });
}

export function useSetPeerEnabled(instanceId: string) {
  const qc = useQueryClient();
  return useMutation({
    mutationFn: ({ peerId, enabled }: { peerId: string; enabled: boolean }) =>
      api.post(`${base}/peers/${peerId}/${enabled ? 'enable' : 'disable'}`),
    onSuccess: () => invalidatePeers(qc, instanceId),
  });
}

export function useDeletePeer(instanceId: string) {
  const qc = useQueryClient();
  return useMutation({
    mutationFn: (peerId: string) => api.delete(`${base}/peers/${peerId}`),
    onSuccess: () => invalidatePeers(qc, instanceId),
  });
}

export function useRotatePeerKeys(instanceId: string) {
  const qc = useQueryClient();
  return useMutation({
    mutationFn: (peerId: string) => api.post<RotatePeerKeysResponse>(`${base}/peers/${peerId}/keys/rotate`),
    onSuccess: () => invalidatePeers(qc, instanceId),
  });
}

export function usePeerConfigVersions(peerId: string, enabled: boolean) {
  return useQuery({
    queryKey: peerKeys.versions(peerId),
    queryFn: () => api.get<ConfigVersionItem[]>(`${base}/peers/${peerId}/config/versions`),
    enabled,
  });
}

// ---- Bulk enrollment ----

function enrollmentForm(file: File) {
  const form = new FormData();
  form.append('file', file);
  return form;
}

/** Dry-run a CSV: returns the per-row preview (Create/Skip/Error) with no writes. */
export function useValidateEnrollment(instanceId: string) {
  return useMutation({
    mutationFn: (file: File) =>
      api.upload<EnrollmentPreviewResult>(`${base}/instances/${instanceId}/enrollments/validate`, enrollmentForm(file)),
  });
}

/** Import the CSV: creates the peers in one batch and returns the outcome summary. */
export function useExecuteEnrollment(instanceId: string) {
  const qc = useQueryClient();
  return useMutation({
    mutationFn: (file: File) =>
      api.upload<EnrollmentResult>(`${base}/instances/${instanceId}/enrollments/execute`, enrollmentForm(file)),
    onSuccess: () => invalidatePeers(qc, instanceId),
  });
}

/** Authenticated download of the batch's config ZIP (.conf + QR per peer + manifest). */
export function enrollmentPackageUrl(batchId: string) {
  return `${base}/enrollments/${batchId}/package`;
}

// ---- Remote orchestration: SSH targets, binding, deployments ----

const terminal = new Set(['Succeeded', 'Failed', 'RolledBack']);

export const orchKeys = {
  sshTargets: ['wg', 'ssh-targets'] as const,
  instanceTarget: (instanceId: string) => ['wg', 'instance-target', instanceId] as const,
  deployments: (instanceId: string) => ['wg', 'deployments', instanceId] as const,
  deployment: (jobId: string) => ['wg', 'deployment', jobId] as const,
  fleet: ['wg', 'fleet'] as const,
};

/** The fleet overview — a live operational view, so it refetches periodically. */
export function useFleet() {
  return useQuery({
    queryKey: orchKeys.fleet,
    queryFn: () => api.get<Fleet>(`${base}/fleet`),
    refetchInterval: 15_000,
  });
}

export interface SshTargetInput {
  name: string;
  host: string;
  port?: number;
  username: string;
  authKind: 'PrivateKey' | 'Password';
  credential: string;
  hostKeyFingerprint?: string;
}

export interface UpdateSshTargetInput {
  name?: string;
  host?: string;
  port?: number;
  username?: string;
  hostKeyFingerprint?: string;
  authKind?: 'PrivateKey' | 'Password';
  credential?: string;
}

export function useSshTargets() {
  return useQuery({ queryKey: orchKeys.sshTargets, queryFn: () => api.get<SshTarget[]>(`${base}/ssh-targets`) });
}

export function useCreateSshTarget() {
  const qc = useQueryClient();
  return useMutation({
    mutationFn: (input: SshTargetInput) => api.post<{ id: string }>(`${base}/ssh-targets`, input),
    onSuccess: () => void qc.invalidateQueries({ queryKey: orchKeys.sshTargets }),
  });
}

export function useUpdateSshTarget() {
  const qc = useQueryClient();
  return useMutation({
    mutationFn: ({ id, input }: { id: string; input: UpdateSshTargetInput }) => api.patch(`${base}/ssh-targets/${id}`, input),
    onSuccess: () => void qc.invalidateQueries({ queryKey: orchKeys.sshTargets }),
  });
}

export function useDeleteSshTarget() {
  const qc = useQueryClient();
  return useMutation({
    mutationFn: (id: string) => api.delete(`${base}/ssh-targets/${id}`),
    onSuccess: () => void qc.invalidateQueries({ queryKey: orchKeys.sshTargets }),
  });
}

/** Probe a target (connect, report reachability + wg presence + the host-key fingerprint). */
export function useTestSshTarget() {
  return useMutation({ mutationFn: (id: string) => api.post<SshTargetTestResult>(`${base}/ssh-targets/${id}/test`) });
}

export function useInstanceTarget(instanceId: string) {
  return useQuery({
    queryKey: orchKeys.instanceTarget(instanceId),
    queryFn: () => api.get<InstanceTarget>(`${base}/instances/${instanceId}/target`),
  });
}

export interface BindTargetInput {
  kind: 'Local' | 'Ssh' | 'Agent';
  sshTargetId?: string;
  agentId?: string;
  keyCustody?: 'WireHqManaged' | 'AgentManaged';
  interfaceName?: string;
  autoReconverge?: boolean;
}

export function useBindInstanceTarget(instanceId: string) {
  const qc = useQueryClient();
  return useMutation({
    mutationFn: (input: BindTargetInput) => api.put(`${base}/instances/${instanceId}/target`, input),
    onSuccess: () => void qc.invalidateQueries({ queryKey: orchKeys.instanceTarget(instanceId) }),
  });
}

export function useDeployments(instanceId: string) {
  return useQuery({
    queryKey: orchKeys.deployments(instanceId),
    queryFn: () => api.get<DeploymentSummary[]>(`${base}/instances/${instanceId}/deployments`),
  });
}

/** Polls a single deployment until it reaches a terminal state. */
export function useDeployment(jobId: string | null) {
  return useQuery({
    queryKey: orchKeys.deployment(jobId ?? ''),
    queryFn: () => api.get<DeploymentDetail>(`${base}/deployments/${jobId}`),
    enabled: !!jobId,
    refetchInterval: (query) => (query.state.data && terminal.has(query.state.data.status) ? false : 1500),
  });
}

export function useRequestDeployment(instanceId: string) {
  const qc = useQueryClient();
  return useMutation({
    mutationFn: () => api.post<{ jobId: string; status: string }>(`${base}/instances/${instanceId}/deploy`),
    onSuccess: () => void qc.invalidateQueries({ queryKey: orchKeys.deployments(instanceId) }),
  });
}

/** Pulls live status now (parses wg show over SSH, persists telemetry) and repaints the peer list. */
export function useRefreshInstanceStatus(instanceId: string) {
  const qc = useQueryClient();
  return useMutation({
    mutationFn: () => api.post<InstanceStatus>(`${base}/instances/${instanceId}/status`),
    onSuccess: () => {
      void qc.invalidateQueries({ queryKey: peerKeys.list(instanceId) });
      void qc.invalidateQueries({ queryKey: wgKeys.instance(instanceId) });
      void qc.invalidateQueries({ queryKey: orchKeys.instanceTarget(instanceId) });
    },
  });
}

// ---- Agents (outbound-only mTLS data plane: enrolment + lifecycle) ----

const agentsBase = '/api/v1/agents';
export const agentKeys = { list: ['wg', 'agents'] as const };

export function useAgents() {
  return useQuery({ queryKey: agentKeys.list, queryFn: () => api.get<Agent[]>(agentsBase) });
}

/** Mints a single-use enrolment token; the clear token is returned exactly once. */
export function useMintEnrollToken() {
  return useMutation({
    mutationFn: (ttlHours?: number) => api.post<MintEnrollTokenResult>(`${agentsBase}/enroll-tokens`, { ttlHours }),
  });
}

function useAgentLifecycle(action: 'disable' | 'reactivate' | 'revoke') {
  const qc = useQueryClient();
  return useMutation({
    mutationFn: (id: string) => api.post(`${agentsBase}/${id}/${action}`),
    onSuccess: () => void qc.invalidateQueries({ queryKey: agentKeys.list }),
  });
}

export const useDisableAgent = () => useAgentLifecycle('disable');
export const useReactivateAgent = () => useAgentLifecycle('reactivate');
export const useRevokeAgent = () => useAgentLifecycle('revoke');
