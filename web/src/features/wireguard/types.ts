// Mirrors the WireGuard module's API DTOs (camelCase JSON).

export interface WireGuardOverview {
  instances: number;
  networks: number;
  peers: number;
  activePeers: number;
  recentHandshakes: number;
  totalRxBytes: number;
  totalTxBytes: number;
}

export interface NetworkListItem {
  id: string;
  name: string;
  cidr: string;
  instanceCount: number;
}

export interface NetworkDetail {
  id: string;
  name: string;
  cidr: string;
  dns: string[];
  defaultAllowedIps: string[];
  instanceCount: number;
  createdAtUtc: string;
}

export interface InstanceListItem {
  id: string;
  name: string;
  slug: string;
  providerType: string;
  listenPort: number;
  interfaceAddress: string;
  status: string;
  peerCount: number;
  createdAtUtc: string;
}

export interface InstanceDetail {
  id: string;
  name: string;
  slug: string;
  description: string | null;
  networkId: string;
  providerType: string;
  listenPort: number;
  interfaceAddress: string;
  publicKey: string;
  dns: string[];
  endpointHost: string | null;
  mtu: number;
  status: string;
  peerCount: number;
  canControl: boolean;
  hasLiveStatus: boolean;
  createdAtUtc: string;
}

export interface PeerListItem {
  id: string;
  name: string;
  email: string | null;
  deviceType: string | null;
  status: string;
  assignedAddress: string;
  publicKey: string;
  lastHandshakeAtUtc: string | null;
  rxBytes: number;
  txBytes: number;
}

export interface CreatePeerResponse {
  id: string;
  publicKey: string;
  assignedAddress: string;
  config: string | null;
  qrCodePngBase64: string | null;
}

export interface RotatePeerKeysResponse {
  publicKey: string;
  config: string | null;
  qrCodePngBase64: string | null;
}

export interface ConfigVersionItem {
  version: number;
  format: string;
  checksum: string;
  createdAtUtc: string;
  createdBy: string | null;
  note: string | null;
}

// ---- Bulk enrollment ----

export type EnrollmentOutcome = 'Create' | 'Skip' | 'Error';

export interface EnrollmentPreviewRow {
  rowNumber: number;
  name: string | null;
  email: string | null;
  department: string | null;
  deviceType: string | null;
  assignedAddress: string | null;
  allowedIps: string[];
  outcome: EnrollmentOutcome;
  reason: string | null;
}

export interface EnrollmentPreviewResult {
  totalRows: number;
  createRows: number;
  skipRows: number;
  errorRows: number;
  rows: EnrollmentPreviewRow[];
}

export interface EnrollmentResultRow {
  rowNumber: number;
  name: string | null;
  email: string | null;
  outcome: string;
  assignedAddress: string | null;
  peerId: string | null;
  reason: string | null;
}

export interface EnrollmentResult {
  batchId: string;
  totalRows: number;
  created: number;
  skipped: number;
  failed: number;
  results: EnrollmentResultRow[];
}

// ---- Remote orchestration ----

export interface SshTarget {
  id: string;
  name: string;
  host: string;
  port: number;
  username: string;
  authKind: 'PrivateKey' | 'Password';
  hostKeyFingerprint: string | null;
  createdAtUtc: string;
}

export interface SshTargetTestResult {
  reachable: boolean;
  wireGuardPresent: boolean;
  hostKeyFingerprint: string | null;
  error: string | null;
}

export type AgentStatus = 'Pending' | 'Active' | 'Disabled' | 'Revoked';

export interface Agent {
  id: string;
  name: string;
  status: AgentStatus;
  platform: string | null;
  version: string | null;
  certificateFingerprint: string;
  enrolledAtUtc: string;
  lastSeenAtUtc: string | null;
  managedInstances: number;
  instancesWithDrift: number;
}

export interface MintEnrollTokenResult {
  id: string;
  token: string;
  expiresAtUtc: string;
}

export type DeploymentTargetKind = 'Local' | 'Ssh' | 'Agent';

export interface FleetSummary {
  totalInstances: number;
  running: number;
  degraded: number;
  drifted: number;
  localTargets: number;
  sshTargets: number;
  agentTargets: number;
  agentsTotal: number;
  agentsOnline: number;
  peersTotal: number;
  peersConnected: number;
}

export interface FleetInstance {
  instanceId: string;
  name: string;
  slug: string;
  networkName: string | null;
  targetKind: DeploymentTargetKind;
  targetName: string | null;
  status: string;
  hasDrift: boolean;
  observedAtUtc: string | null;
  peersTotal: number;
  peersConnected: number;
  rxBytes: number;
  txBytes: number;
  agentLastSeenAtUtc: string | null;
}

export interface Fleet {
  summary: FleetSummary;
  instances: FleetInstance[];
}

export interface InstanceTarget {
  instanceId: string;
  kind: DeploymentTargetKind;
  sshTargetId: string | null;
  sshTargetName: string | null;
  agentId: string | null;
  agentName: string | null;
  keyCustody: 'WireHqManaged' | 'AgentManaged';
  /** AgentManaged + the agent hasn't reported its interface key yet (the first deploy adopts it). */
  agentKeyPending: boolean;
  /** When on, a detected config drift auto-enqueues a redeploy to re-converge. */
  autoReconverge: boolean;
  interfaceName: string;
  hasDrift: boolean;
  driftObservedAtUtc: string | null;
}

export interface PeerTelemetry {
  publicKey: string;
  lastHandshakeAtUtc: string | null;
  rxBytes: number;
  txBytes: number;
  endpoint: string | null;
}

export interface InstanceStatus {
  instanceId: string;
  hasLiveStatus: boolean;
  state: string;
  listenPort: number | null;
  observedAtUtc: string;
  hasDrift: boolean;
  driftDetail: string | null;
  peers: PeerTelemetry[];
}

export type DeploymentStatus = 'Pending' | 'Dispatched' | 'Applying' | 'Succeeded' | 'Failed' | 'RolledBack';

export interface DeploymentSummary {
  id: string;
  type: string;
  status: DeploymentStatus;
  attempts: number;
  createdAtUtc: string;
  completedAtUtc: string | null;
  error: string | null;
}

export interface DeploymentEventItem {
  phase: string;
  detail: string | null;
  atUtc: string;
}

export interface DeploymentDetail {
  id: string;
  instanceId: string;
  type: string;
  status: DeploymentStatus;
  attempts: number;
  desiredConfigVersion: number | null;
  error: string | null;
  createdAtUtc: string;
  dispatchedAtUtc: string | null;
  completedAtUtc: string | null;
  events: DeploymentEventItem[];
}
