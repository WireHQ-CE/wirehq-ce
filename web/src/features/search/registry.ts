import type { LucideIcon } from 'lucide-react';
import {
  Activity,
  Bell,
  Building2,
  CreditCard,
  FileText,
  Gauge,
  KeyRound,
  LayoutDashboard,
  Mail,
  Network,
  Package,
  QrCode,
  ScrollText,
  Settings,
  ShieldCheck,
  UserPlus,
  Users2,
} from 'lucide-react';
import { EDITION } from '@/lib/edition';
import { useAuthStore } from '@/stores/auth-store';

/**
 * The command-palette registry: every reachable page, setting and high-intent action, with the
 * synonyms people actually type ("MFA", "2FA" → Security). Entries carry the SAME gates the
 * sidebar uses — permission, entitlement feature, platform role — plus the build edition, so a
 * user can only ever see (and jump to) what their own account is allowed to reach. Nothing here
 * queries any data: search over this registry is per-user by construction.
 */

export type SearchSection = 'Navigate' | 'Account' | 'Platform' | 'Actions';

export interface SearchEntry {
  id: string;
  title: string;
  /** Short context line under the title. */
  hint?: string;
  section: SearchSection;
  path: string;
  icon: LucideIcon;
  /** Synonyms and phrases matched in addition to the title. */
  keywords: string[];
  /** Org-permission gate (the Sidebar convention). */
  permission?: string;
  /** Plan-entitlement gate. */
  feature?: string;
  /** Requires a platform role (Super Admin / Support) — never true for CE users. */
  platform?: boolean;
  /** Restrict to specific builds (e.g. Plan & usage is SaaS-only; Modules is CE-only). */
  editions?: ('saas' | 'community')[];
}

const REGISTRY: SearchEntry[] = [
  // ── Navigate ────────────────────────────────────────────────────────────────────────────────
  {
    id: 'nav-dashboard',
    title: 'Dashboard',
    section: 'Navigate',
    path: '/app',
    icon: LayoutDashboard,
    keywords: ['home', 'overview', 'fleet activity'],
  },
  {
    id: 'nav-organization',
    title: 'Organization',
    hint: 'Profile, branding and organization settings',
    section: 'Navigate',
    path: '/app/organization',
    icon: Building2,
    keywords: ['org', 'company', 'workspace', 'organisation'],
  },
  {
    id: 'nav-teams',
    title: 'Teams',
    section: 'Navigate',
    path: '/app/teams',
    icon: Users2,
    keywords: ['groups', 'team members'],
    permission: 'identity.teams.read',
    feature: 'teams',
  },
  {
    id: 'nav-users',
    title: 'Users',
    hint: 'Members, roles and invitations',
    section: 'Navigate',
    path: '/app/users',
    icon: Users2,
    keywords: ['members', 'people', 'accounts', 'roles'],
  },
  {
    id: 'nav-wireguard',
    title: 'WireGuard',
    hint: 'Networks, gateways, peers and deployment',
    section: 'Navigate',
    path: '/app/wireguard',
    icon: Network,
    keywords: ['vpn', 'networks', 'instances', 'gateways', 'peers', 'devices', 'tunnels', 'agents', 'deploy'],
    permission: 'wg.instances.read',
  },
  {
    id: 'nav-audit',
    title: 'Audit Logs',
    section: 'Navigate',
    path: '/app/audit',
    icon: ScrollText,
    keywords: ['activity', 'history', 'events', 'who did what', 'log'],
  },
  {
    id: 'nav-status',
    title: 'Status & health',
    section: 'Navigate',
    path: '/app/status',
    icon: Gauge,
    keywords: ['health', 'uptime', 'service status', 'availability'],
    editions: ['saas'],
  },
  {
    id: 'nav-modules',
    title: 'Modules',
    hint: 'Marketplace modules and licences',
    section: 'Navigate',
    path: '/app/modules',
    icon: Package,
    keywords: ['marketplace', 'licence', 'license', 'extensions', 'add-ons', 'activation'],
    editions: ['community'],
  },

  // ── Account ─────────────────────────────────────────────────────────────────────────────────
  {
    id: 'account-settings',
    title: 'Profile settings',
    hint: 'Name, avatar, timezone and language',
    section: 'Account',
    path: '/app/settings',
    icon: Settings,
    keywords: ['profile', 'avatar', 'my account', 'timezone', 'language', 'preferences'],
  },
  {
    id: 'account-security',
    title: 'Security',
    hint: 'Two-factor authentication, password and sessions',
    section: 'Account',
    path: '/app/settings/security',
    icon: ShieldCheck,
    keywords: ['mfa', '2fa', 'two-factor', 'two factor', 'authenticator', 'password', 'sessions', 'sign out devices', 'recovery codes'],
  },
  {
    id: 'account-notifications',
    title: 'Notification preferences',
    section: 'Account',
    path: '/app/settings/notifications',
    icon: Bell,
    keywords: ['email preferences', 'alerts', 'digests'],
  },
  {
    id: 'account-plan',
    title: 'Plan & usage',
    hint: 'Subscription, limits and billing',
    section: 'Account',
    path: '/app/settings/plan',
    icon: CreditCard,
    keywords: ['billing', 'subscription', 'upgrade', 'invoice', 'payment', 'pro', 'trial', 'limits', 'quota'],
    editions: ['saas'],
  },

  // ── Platform (Super Admin / Support only) ───────────────────────────────────────────────────
  {
    id: 'platform-customers',
    title: 'Customers',
    section: 'Platform',
    path: '/app/platform/customers',
    icon: Building2,
    keywords: ['tenants', 'organizations', 'impersonate'],
    platform: true,
    editions: ['saas'],
  },
  {
    id: 'platform-audit',
    title: 'Audit Search',
    hint: 'Cross-tenant audit (audited)',
    section: 'Platform',
    path: '/app/platform/audit',
    icon: ScrollText,
    keywords: ['cross-tenant audit', 'platform audit'],
    platform: true,
    editions: ['saas'],
  },
  {
    id: 'platform-diagnostics',
    title: 'Diagnostics',
    hint: 'Correlation lookup and tenant health',
    section: 'Platform',
    path: '/app/platform/diagnostics',
    icon: Activity,
    keywords: ['correlation id', 'trace', 'support', 'tenant health'],
    platform: true,
    editions: ['saas'],
  },
  {
    id: 'platform-plans',
    title: 'Plans',
    section: 'Platform',
    path: '/app/platform/plans',
    icon: CreditCard,
    keywords: ['pricing', 'editions', 'stripe prices'],
    platform: true,
    editions: ['saas'],
  },
  {
    id: 'platform-content',
    title: 'CMS',
    section: 'Platform',
    path: '/app/platform/content',
    icon: FileText,
    keywords: ['content', 'pages', 'marketing site', 'navigation'],
    platform: true,
    editions: ['saas'],
  },
  {
    id: 'platform-settings',
    title: 'Platform settings',
    hint: 'SMTP, bot protection and billing keys',
    section: 'Platform',
    path: '/app/platform/settings',
    icon: ShieldCheck,
    keywords: ['smtp', 'email server', 'turnstile', 'captcha', 'stripe keys'],
    platform: true,
    editions: ['saas'],
  },

  // ── Actions (high-intent phrasings of the same destinations) ───────────────────────────────
  {
    id: 'action-enable-mfa',
    title: 'Enable two-factor authentication',
    hint: 'Security → Two-factor (MFA)',
    section: 'Actions',
    path: '/app/settings/security',
    icon: KeyRound,
    keywords: ['mfa', '2fa', 'enable mfa', 'set up authenticator', 'totp'],
  },
  {
    id: 'action-change-password',
    title: 'Change password',
    hint: 'Security → Password',
    section: 'Actions',
    path: '/app/settings/security',
    icon: KeyRound,
    keywords: ['password', 'reset password', 'update password'],
  },
  {
    id: 'action-invite-user',
    title: 'Invite a user',
    hint: 'Users → Invite',
    section: 'Actions',
    path: '/app/users',
    icon: UserPlus,
    keywords: ['invite', 'add user', 'add member', 'new user'],
  },
  {
    id: 'action-create-network',
    title: 'Create a WireGuard network',
    hint: 'WireGuard → Networks',
    section: 'Actions',
    path: '/app/wireguard',
    icon: Network,
    keywords: ['new network', 'add network', 'create vpn'],
    permission: 'wg.instances.read',
  },
  {
    id: 'action-peer-config',
    title: 'Get a peer config / QR code',
    hint: 'WireGuard → Peers',
    section: 'Actions',
    path: '/app/wireguard',
    icon: QrCode,
    keywords: ['qr', 'client config', 'peer config', 'download config', 'mobile'],
    permission: 'wg.instances.read',
  },
  {
    id: 'action-email-prefs',
    title: 'Manage email notifications',
    hint: 'Account → Notifications',
    section: 'Actions',
    path: '/app/settings/notifications',
    icon: Mail,
    keywords: ['unsubscribe', 'email alerts', 'notification settings'],
  },
];

/** The registry filtered to what THIS user's account can see — permission, feature, platform role
 * and build edition, evaluated exactly like the sidebar. */
export function useSearchEntries(): SearchEntry[] {
  const user = useAuthStore((s) => s.user);
  const hasPermission = useAuthStore((s) => s.hasPermission);
  const hasFeature = useAuthStore((s) => s.hasFeature);

  return REGISTRY.filter((e) => {
    if (e.editions && !e.editions.includes(EDITION)) return false;
    if (e.platform && !user?.platformRole) return false;
    if (e.permission && !hasPermission(e.permission)) return false;
    if (e.feature && !hasFeature(e.feature)) return false;
    return true;
  });
}
