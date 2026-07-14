import { NavLink } from 'react-router-dom';
import { Bell, Building2, KeySquare, LayoutDashboard, Network, Package, Palette, ScrollText, Settings, ShieldCheck, UserCog, Users2, Webhook } from 'lucide-react';
import { Logo } from '@/components/brand/Logo';
import { cn } from '@/lib/utils/cn';
import { useAuthStore } from '@/stores/auth-store';

interface NavItem {
  to: string;
  label: string;
  icon: typeof Settings;
  end?: boolean;
  permission?: string;
  /** Hide unless the install's effective plan includes this feature — i.e. a module unlocking it is activated
   * (docs/29 M-10). Mirrors the SaaS Sidebar so a lean CE shows only free-core items and an activated module's
   * nav appears. The Modules console itself is deliberately NOT feature-gated (it is how you activate modules). */
  feature?: string;
}

// Org-scoped navigation. Community Edition has no platform/super-admin tier and no billing, so the nav is
// just the WireGuard management surface for the self-hosted org. Feature-gated items appear once a Marketplace
// module unlocks their capability (docs/29 M-10).
const orgNav: NavItem[] = [
  { to: '/app', label: 'Dashboard', icon: LayoutDashboard, end: true },
  { to: '/app/organization', label: 'Organization', icon: Building2 },
  { to: '/app/teams', label: 'Teams', icon: Users2, permission: 'identity.teams.read', feature: 'teams' },
  { to: '/app/users', label: 'Users', icon: Users2 },
  { to: '/app/wireguard', label: 'WireGuard', icon: Network, permission: 'wg.instances.read' },
  { to: '/app/audit', label: 'Audit Logs', icon: ScrollText },
  { to: '/app/modules', label: 'Modules', icon: Package, permission: 'marketplace.modules.manage' },
];

const settingsItems: NavItem[] = [
  { to: '/app/settings', label: 'Settings', icon: Settings, end: true },
  { to: '/app/settings/security', label: 'Security', icon: ShieldCheck },
  { to: '/app/settings/roles', label: 'Roles', icon: UserCog, permission: 'identity.roles.manage', feature: 'rbac.custom_roles' },
  { to: '/app/settings/api-keys', label: 'API keys', icon: KeySquare, permission: 'api.keys.manage', feature: 'api.keys' },
  { to: '/app/settings/webhooks', label: 'Webhooks', icon: Webhook, permission: 'api.keys.manage', feature: 'api.keys' },
  { to: '/app/settings/notification-rules', label: 'Notification rules', icon: Bell, permission: 'notifications.manage' },
  { to: '/app/settings/notifications', label: 'Notifications', icon: Bell },
  { to: '/app/settings/branding', label: 'Branding', icon: Palette, permission: 'branding.manage', feature: 'branding.basic' },
];

function Item({ to, label, icon: Icon, end }: NavItem) {
  return (
    <NavLink
      to={to}
      end={end}
      className={({ isActive }) =>
        cn(
          'flex items-center gap-2.5 rounded-md px-2.5 py-2 text-sm font-medium transition-colors',
          isActive
            ? 'bg-gold-400/10 text-gold-700 dark:text-gold-400'
            : 'text-ink-600 hover:bg-ink-100 dark:text-ink-300 dark:hover:bg-ink-800',
        )
      }
    >
      <Icon className="size-4 shrink-0" />
      {label}
    </NavLink>
  );
}

function SectionLabel({ children }: { children: React.ReactNode }) {
  return <div className="px-2.5 pb-1 pt-4 text-xs font-medium uppercase tracking-wide text-ink-400">{children}</div>;
}

export function Sidebar() {
  // Subscribe to `user` (a fresh reference on every /me re-resolution) rather than the store's hasPermission /
  // hasFeature functions (stable references that never trigger a re-render) — so activating a module and
  // refreshing entitlements makes its nav item appear immediately, without a reload (docs/29 M-10).
  const user = useAuthStore((s) => s.user);

  const entitled = (item: NavItem) =>
    (!item.permission || (user?.permissions.includes(item.permission) ?? false)) &&
    (!item.feature || (user?.entitlements?.features.includes(item.feature) ?? false));
  const items = orgNav.filter(entitled);

  return (
    <aside className="flex w-60 shrink-0 flex-col border-r bg-ink-0 dark:bg-ink-950 dark:border-ink-800">
      <div className="flex h-14 items-center px-4">
        <Logo />
      </div>
      <nav className="flex-1 space-y-1 px-3 py-2">
        {items.map((item) => (
          <Item key={item.to} {...item} />
        ))}
        <SectionLabel>Account</SectionLabel>
        {settingsItems.filter(entitled).map((item) => (
          <Item key={item.to} {...item} />
        ))}
      </nav>
    </aside>
  );
}
