import type { LucideIcon } from 'lucide-react';
import {
  BarChart3,
  Bell,
  Brush,
  Database,
  FileDown,
  FolderKey,
  Gauge,
  LayoutDashboard,
  LifeBuoy,
  LogIn,
  MessageSquare,
  Palette,
  Plug,
  RefreshCw,
  Rocket,
  ScrollText,
  Send,
  ShieldCheck,
  UploadCloud,
  UserCog,
  Users2,
} from 'lucide-react';
// Per-module detail-page screenshots — real captures of each finished module's in-app surface. Vite resolves each
// import to a hashed asset URL. Modules without a screenshot simply omit the `screenshots` field (no rail rendered).
import teamManagementShot from '@/assets/marketplace/team-management/1.png';
import fleetDashboardShot from '@/assets/marketplace/fleet-dashboard/1.png';
import customRolesShot from '@/assets/marketplace/custom-roles/1.png';
import auditExportShot from '@/assets/marketplace/audit-export/1.png';
import apiExtensionsShot from '@/assets/marketplace/api-extensions/1.png';
import brandingShot from '@/assets/marketplace/customisation-rebranding/1.png';
import teamsConnectorShot from '@/assets/marketplace/teams-connector/1.png';

/**
 * The Marketplace module catalog — the display source for BOTH the public marketing pages and the Community
 * Edition's Modules page (which is why it lives in lib/, not a feature dir: it survives the CE strip; docs/18
 * §5/§10). Entries carrying `delivery: 'gate-only'` are the modules a CE install can actually activate today;
 * their `slug` + `feature` MUST match the backend `ModuleCatalog` (the activation source of truth, docs/29 §5) —
 * keep the two in sync when adding a gate-only module. `delivery: 'code-delivered'` marks modules whose code is
 * stripped from the CE (activatable only once the phase-4 module runtime ships). The remaining entries are still
 * marketing placeholders (illustrative names/prices) with no backend module yet; a future SaaS catalog API
 * replaces this static file in phase 6.
 */

export type ModuleCategory =
  | 'Deployment & Fleet'
  | 'Branding & White-label'
  | 'Identity & Access'
  | 'Reporting & Analytics'
  | 'Integrations'
  | 'Notifications'
  | 'Operations & Resilience';

export type ModuleAvailability = 'available' | 'coming-soon';

/**
 * How a module unlocks on a Community Edition install — mirrors the backend `ModuleCatalog`, the source of truth
 * (docs/29 §5). `gate-only`: the capability code already ships in the (kept-core) CE, so activating a licence
 * just flips its entitlement (usable today). `code-delivered`: the code is stripped from the CE, so it needs the
 * phase-4 module runtime — shown "coming soon", not activatable yet. Absent: a marketing-only placeholder with
 * no backend feature mapping.
 */
export type ModuleDelivery = 'gate-only' | 'code-delivered';

export interface MarketplaceModule {
  slug: string;
  name: string;
  category: ModuleCategory;
  icon: LucideIcon;
  /** One-liner for cards. */
  tagline: string;
  /** Longer copy for the module detail page. */
  description: string;
  /** Bullet highlights on the detail page. */
  highlights: string[];
  /** Detail-page screenshots (imported asset URLs from `@/assets/marketplace/<slug>/`); absent → no screenshot rail. */
  screenshots?: string[];
  /** One-off price in GBP (the operator-set backend price overrides this via the prices API). */
  price: number;
  availability: ModuleAvailability;
  /** Shown in the marketing page's Featured section. */
  featured?: boolean;
  /** The plan feature key this module unlocks (matches the backend `ModuleCatalog`); absent = marketing-only. */
  feature?: string;
  /** How the module unlocks on the CE; absent = a marketing placeholder with no activation path yet. */
  delivery?: ModuleDelivery;
}

export const MODULE_CATEGORIES: ModuleCategory[] = [
  'Deployment & Fleet',
  'Branding & White-label',
  'Identity & Access',
  'Reporting & Analytics',
  'Integrations',
  'Notifications',
  'Operations & Resilience',
];

export function formatModulePrice(price: number): string {
  return `£${price}`;
}

export const MARKETPLACE_MODULES: MarketplaceModule[] = [
  // --- Gate-only modules: activatable on a CE install today (slug + feature match the backend ModuleCatalog). ---
  {
    slug: 'team-management',
    name: 'Teams',
    category: 'Identity & Access',
    icon: Users2,
    tagline: 'Group members into teams and manage access by team.',
    description:
      'Organise your members into teams and manage who can reach what by team rather than one user at a time — the self-hosted counterpart to the Pro plan capability on WireHQ Cloud.',
    highlights: ['Create and manage teams', 'Assign members to teams', 'Team-scoped access management'],
    screenshots: [teamManagementShot],
    price: 99,
    availability: 'available',
    feature: 'teams',
    delivery: 'gate-only',
  },
  {
    slug: 'fleet-dashboard',
    name: 'Fleet Dashboard',
    category: 'Deployment & Fleet',
    icon: Gauge,
    tagline: 'A live overview of every gateway, agent and peer across your fleet.',
    description:
      'See your whole estate at a glance: gateway and agent connectivity, peer counts, handshake recency and deployment health across every network — the self-hosted counterpart to the Pro fleet view.',
    highlights: ['Fleet-wide status overview', 'Gateway + agent connectivity', 'Peer and handshake insight'],
    screenshots: [fleetDashboardShot],
    price: 129,
    availability: 'available',
    feature: 'fleet.dashboard',
    delivery: 'gate-only',
  },
  {
    slug: 'auto-reconverge',
    name: 'Drift Auto-reconverge',
    category: 'Operations & Resilience',
    icon: RefreshCw,
    tagline: 'Automatically re-apply configuration when a gateway drifts.',
    description:
      'When WireHQ detects that a gateway has drifted from its intended configuration, this module re-applies the correct config automatically — keeping your fleet converged without manual intervention.',
    highlights: ['Automatic drift correction', 'Per-gateway reconverge', 'Keeps the fleet in its intended state'],
    price: 99,
    availability: 'available',
    feature: 'drift.auto_reconverge',
    delivery: 'gate-only',
  },
  {
    slug: 'bulk-enrolment',
    name: 'Bulk Enrolment',
    category: 'Deployment & Fleet',
    icon: UploadCloud,
    tagline: 'Onboard many peers at once from a CSV or template.',
    description:
      'Bring peers online in batches instead of one at a time: bulk-create peers from a list, generate their configs, and hand them out — ideal for onboarding a site or a device fleet.',
    highlights: ['Batch peer creation', 'Templated configuration', 'Faster large-scale onboarding'],
    price: 79,
    availability: 'available',
    feature: 'bulk_enrollment',
    delivery: 'gate-only',
  },
  {
    slug: 'custom-roles',
    name: 'Custom Roles',
    category: 'Identity & Access',
    icon: UserCog,
    tagline: 'Define your own roles with exactly the permissions you choose.',
    description:
      'Go beyond the built-in Owner/Admin/Member roles: create custom roles with a precise permission set, so members get exactly the access they need — the self-hosted counterpart to the Enterprise capability.',
    highlights: ['Author custom roles', 'Fine-grained permission sets', 'Privilege-escalation guarded'],
    screenshots: [customRolesShot],
    price: 149,
    availability: 'available',
    feature: 'rbac.custom_roles',
    delivery: 'gate-only',
  },
  {
    slug: 'audit-export',
    name: 'Audit Export',
    category: 'Reporting & Analytics',
    icon: FileDown,
    tagline: 'Export your tamper-evident audit log as CSV, JSON, or SIEM-format OCSF/CEF.',
    description:
      'Take your audit history out of WireHQ for archival, reporting or compliance workflows: export the tamper-evident log as CSV, JSON, or a SIEM-format OCSF/CEF file for ingestion by your security tooling.',
    highlights: ['CSV, JSON + OCSF/CEF export', 'Filtered by time range', 'Compliance-friendly archival'],
    screenshots: [auditExportShot],
    price: 99,
    availability: 'available',
    feature: 'audit.export',
    delivery: 'gate-only',
  },
  // --- Marketing catalogue (illustrative; no backend module yet unless flagged code-delivered above). ---
  {
    slug: 'remote-deployment',
    name: 'Remote Deployment',
    category: 'Deployment & Fleet',
    icon: Rocket,
    tagline: 'Push configs to your gateways — SSH Targets and outbound Agents in one module.',
    description:
      'Turn your instance from a config generator into a deployment engine. SSH Targets push configs to your own hosts with a backup → apply → verify → rollback pipeline; outbound mTLS Agents (including the Gateway container) let boxes behind NAT pull their config with no inbound access at all. One module, both transports. In WireHQ Cloud this capability ships with the Pro and Enterprise plans — this is its self-hosted, one-off counterpart.',
    highlights: [
      'SSH Targets: backup → apply → verify → rollback deploys',
      'Outbound mTLS Agents — no inbound firewall holes',
      'The Gateway container (tunnels in Docker, no host WireGuard)',
      'Config-drift detection across your fleet',
    ],
    price: 199,
    availability: 'coming-soon',
    featured: true,
  },
  {
    slug: 'customisation-rebranding',
    name: 'Customisation & Rebranding',
    category: 'Branding & White-label',
    icon: Palette,
    tagline: 'Your logo, your colours, your product name — across the whole control plane.',
    description:
      'Replace the WireHQ brand with your own throughout the app: your logo, an accent colour, your product name and your favicon. Built for MSPs and IT teams who run the Community Edition as part of their own service.',
    highlights: [
      'Upload your own logo (light + dark lockups)',
      'A brand accent colour across the UI',
      'Custom product name in the header and tab title',
      'Your own favicon',
    ],
    screenshots: [brandingShot],
    price: 79,
    availability: 'available',
    featured: true,
    feature: 'branding.basic',
    delivery: 'gate-only',
  },
  {
    slug: 'advanced-branding-pack',
    name: 'Advanced Branding Pack',
    category: 'Branding & White-label',
    icon: Brush,
    tagline: 'Deep theming: typography, spacing tokens, favicons, PWA icons and print styles.',
    description:
      'Everything in Customisation & Rebranding, plus deep control of the design system: typography, spacing and radius tokens, favicon/PWA assets, QR-export framing and printable config sheets that carry your brand.',
    highlights: [
      'Design-token editor (type scale, radii, spacing)',
      'Favicon + PWA icon set management',
      'Branded QR/config exports and print styles',
    ],
    price: 129,
    availability: 'coming-soon',
  },
  {
    slug: 'white-label-login',
    name: 'White Label Login',
    category: 'Branding & White-label',
    icon: LogIn,
    tagline: 'A fully unbranded sign-in experience on your own domain.',
    description:
      'Remove every trace of WireHQ from the authentication surface: custom sign-in page, your own support links, custom domain guidance and unbranded password-reset and invitation flows.',
    highlights: [
      'Unbranded sign-in, reset and invite pages',
      'Custom support/help links',
      'Works with the reverse-proxy deployment mode',
    ],
    price: 99,
    availability: 'coming-soon',
  },
  {
    slug: 'saml-authentication',
    name: 'SAML Authentication',
    category: 'Identity & Access',
    icon: ShieldCheck,
    tagline: 'Bring your own IdP: SAML 2.0 single sign-on for your self-hosted instance.',
    description:
      'SP-initiated SAML 2.0 sign-on against your identity provider — Okta, Entra ID, Google Workspace, Keycloak and friends. Your instance, your IdP, no cloud dependency. (The managed, fleet-wide identity suite remains a WireHQ Enterprise capability — this module is its self-hosted, bring-your-own-IdP counterpart.)',
    highlights: [
      'SP-initiated SAML 2.0 (metadata exchange)',
      'Just-in-time user provisioning on first sign-on',
      'Attribute → role mapping',
      'Break-glass local sign-in for recovery',
    ],
    price: 249,
    availability: 'coming-soon',
    feature: 'identity.sso',
    delivery: 'code-delivered',
  },
  {
    slug: 'ldap-integration',
    name: 'LDAP Integration',
    category: 'Identity & Access',
    icon: FolderKey,
    tagline: 'Authenticate against Active Directory or OpenLDAP.',
    description:
      'Bind authentication and user lookup against your directory: Active Directory, Samba AD or OpenLDAP. Group-to-role mapping keeps directory membership authoritative.',
    highlights: [
      'LDAP/LDAPS bind authentication',
      'Directory group → WireHQ role mapping',
      'Nested-group resolution',
      'Connection test tooling',
    ],
    price: 199,
    availability: 'coming-soon',
    feature: 'identity.ldap',
    delivery: 'code-delivered',
  },
  {
    slug: 'access-policies',
    name: 'Access Policies',
    category: 'Identity & Access',
    icon: ShieldCheck,
    tagline: 'Declarative who-can-reach-what, compiled to per-peer WireGuard AllowedIPs.',
    description:
      'Define access as policy — which people, teams or roles can reach which peers, teams, roles or networks — and WireHQ compiles it to per-peer AllowedIPs over your existing deploy pipeline, with change review and simulation. The self-hosted counterpart to the Enterprise capability.',
    highlights: [
      'Who-can-reach-what policy authoring',
      'Compiled to per-peer WireGuard AllowedIPs',
      'Change review + who-can-reach simulation',
    ],
    price: 199,
    availability: 'coming-soon',
    feature: 'policy.access',
    delivery: 'code-delivered',
  },
  {
    slug: 'advanced-reporting',
    name: 'Advanced Reporting',
    category: 'Reporting & Analytics',
    icon: BarChart3,
    tagline: 'Scheduled, exportable reports on peers, usage and fleet health.',
    description:
      'Turn the data your instance already has into reports you can hand to a customer or a manager: peer inventory, connection history, deployment outcomes and drift — scheduled and exported as PDF/CSV.',
    highlights: [
      'Report builder over peers, gateways and deployments',
      'Scheduled email delivery',
      'PDF + CSV export',
    ],
    price: 149,
    availability: 'coming-soon',
  },
  {
    slug: 'advanced-dashboard-widgets',
    name: 'Advanced Dashboard Widgets',
    category: 'Reporting & Analytics',
    icon: LayoutDashboard,
    tagline: 'A configurable dashboard: handshake heatmaps, throughput, top talkers.',
    description:
      'Extend the dashboard with rearrangeable widgets: handshake recency heatmaps, per-gateway throughput, top peers, deployment success trends and drift timelines.',
    highlights: ['Drag-and-drop widget layout', 'Per-user dashboard preferences', 'New fleet + traffic widgets'],
    price: 79,
    availability: 'coming-soon',
  },
  {
    slug: 'audit-logs-plus',
    name: 'Audit Logs+',
    category: 'Reporting & Analytics',
    icon: ScrollText,
    tagline: 'Extended audit retention, saved searches and export tooling.',
    description:
      'Build on the built-in tamper-evident audit log with longer retention windows, saved searches, scheduled export and archive tooling for compliance workflows.',
    highlights: ['Extended retention management', 'Saved audit searches', 'Scheduled CSV/JSON export'],
    price: 99,
    availability: 'coming-soon',
  },
  {
    slug: 'siem-export',
    name: 'SIEM Export',
    category: 'Reporting & Analytics',
    icon: Send,
    tagline: 'Continuously stream audit events to your SIEM over Syslog/HTTPS.',
    description:
      'Live, push-based delivery of the audit stream to your security tooling as events happen — over Syslog or HTTPS, in OCSF (JSON lines) or CEF. (One-off SIEM-format OCSF/CEF export is already included in the Audit Export module; this adds the continuous transport.)',
    highlights: ['Continuous Syslog/HTTPS delivery', 'OCSF + CEF formats', 'Field mapping documentation'],
    price: 199,
    availability: 'coming-soon',
  },
  {
    slug: 'api-extensions',
    name: 'API Extensions',
    category: 'Integrations',
    icon: Plug,
    tagline: 'Long-lived API keys, webhooks and automation endpoints.',
    description:
      'First-class automation for your instance: scoped API keys, outbound webhooks on lifecycle events (peer added, deploy finished, drift detected) and extra endpoints designed for scripting.',
    highlights: ['Scoped, revocable API keys', 'Outbound webhooks with signing', 'Automation-friendly endpoints'],
    screenshots: [apiExtensionsShot],
    price: 149,
    availability: 'available',
    featured: true,
    feature: 'api.keys',
    delivery: 'gate-only',
  },
  {
    slug: 'teams-connector',
    name: 'Chat Alerts (Teams & Slack)',
    category: 'Notifications',
    icon: MessageSquare,
    tagline: 'Route notification rules to a Microsoft Teams or Slack channel.',
    description:
      'Add Chat as a delivery channel for your notification rules: choose the events you care about and WireHQ posts a message to your Teams or Slack channel through an incoming webhook. One-off, per-event alerts carrying a redacted summary.',
    highlights: ['Microsoft Teams + Slack incoming webhooks', 'Per-event routing rules', 'Redacted summaries — no sensitive detail'],
    screenshots: [teamsConnectorShot],
    price: 99,
    availability: 'available',
    feature: 'notifications.chat',
    delivery: 'gate-only',
  },
  {
    slug: 'sms-integration',
    name: 'SMS Integration',
    category: 'Notifications',
    icon: MessageSquare,
    tagline: 'SMS delivery for critical alerts via your own provider account.',
    description:
      'Send critical alerts over SMS using your own Twilio/Vonage account — your credentials, your bill, no WireHQ middleman. (SMS-based MFA is a separate authentication feature, not part of this module.)',
    highlights: ['Bring-your-own SMS provider', 'Critical-alert routing', 'Opt-out/STOP compliance'],
    price: 79,
    availability: 'coming-soon',
  },
  {
    slug: 'advanced-notifications',
    name: 'Advanced Notifications',
    category: 'Notifications',
    icon: Bell,
    // Copy advertises ONLY shipped highlights — all Wave-3 slices are now live: multi-pattern + email-beyond-quota +
    // digests + quiet hours + escalation (docs/35 B-8/N-14; advertised one at a time as each shipped).
    tagline: 'Route, batch, quiet, and escalate your notifications.',
    description:
      'Take control of the notification stream: a single routing rule can match several event patterns at once, create email rules beyond the free-tier limit, coalesce events into daily or weekly digests, hold non-urgent alerts during quiet hours, and escalate to a backup channel or on-call role when no one acknowledges.',
    highlights: [
      'Multi-pattern routing rules',
      'More email rules — beyond the free quota',
      'Daily / weekly digests',
      'Quiet hours (defer, don’t drop)',
      'Escalation chains with acknowledgement',
    ],
    price: 69,
    availability: 'available',
    feature: 'notifications.routing',
    delivery: 'gate-only',
  },
  {
    slug: 'backup-manager',
    name: 'Backup Manager',
    category: 'Operations & Resilience',
    icon: Database,
    tagline: 'Scheduled, encrypted backups of your whole instance — verified restores.',
    description:
      'Scheduled encrypted backups of the database and configuration to local disk or S3-compatible storage, with retention policies and one-command verified restore.',
    highlights: ['Scheduled encrypted backups', 'Local + S3-compatible targets', 'Verified restore tooling'],
    price: 129,
    availability: 'coming-soon',
  },
  {
    slug: 'disaster-recovery-toolkit',
    name: 'Disaster Recovery Toolkit',
    category: 'Operations & Resilience',
    icon: LifeBuoy,
    tagline: 'Warm-standby replication and a rehearsed failover runbook.',
    description:
      'Everything Backup Manager does, plus warm-standby replication to a second host, failover tooling and a rehearsable recovery runbook — for installs where the VPN is critical infrastructure.',
    highlights: ['Warm-standby replication', 'Guided failover + failback', 'Recovery rehearsal mode'],
    price: 249,
    availability: 'coming-soon',
  },
];

export function getModule(slug: string): MarketplaceModule | undefined {
  return MARKETPLACE_MODULES.find((m) => m.slug === slug);
}

export function modulesByCategory(): { category: ModuleCategory; modules: MarketplaceModule[] }[] {
  return MODULE_CATEGORIES.map((category) => ({
    category,
    modules: MARKETPLACE_MODULES.filter((m) => m.category === category),
  })).filter((g) => g.modules.length > 0);
}
