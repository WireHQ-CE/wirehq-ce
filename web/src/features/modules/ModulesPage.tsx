import { useState, type FormEvent } from 'react';
import {
  ArrowUpRight,
  CheckCircle2,
  KeyRound,
  Package,
  PackageCheck,
  PackageOpen,
  Trash2,
} from 'lucide-react';
import { PageHeader } from '@/components/layout/AppShell';
import { StatCard } from '@/components/data/StatCard';
import { Badge } from '@/components/ui/badge';
import { Button } from '@/components/ui/button';
import { Card, CardContent, CardHeader, CardTitle } from '@/components/ui/card';
import { Input, Field } from '@/components/ui/input';
import { useToast } from '@/components/ui/toast';
import { ApiError } from '@/lib/api/client';
import { useAuthStore } from '@/stores/auth-store';
import { useAuth } from '@/features/auth/use-auth';
import { getModule, MARKETPLACE_MODULES, formatModulePrice } from '@/lib/marketplace/catalog';
import { useMarketplaceModules } from '@/lib/marketplace/modules-api';
import { useActivatedModules, useActivateModule, useDeactivateModule, type ActivatedModule } from './api';

/**
 * The Modules page — the Community Edition's Marketplace module-activation console
 * (docs/29-ce-marketplace-modules.md M-9). A self-hoster pastes the licence key from a purchase, activates it,
 * and the capability lights up through the entitlement union (the relevant nav item appears once /me
 * re-resolves). Gated on `marketplace.modules.manage`. This is CE-only surface: the page is routed only by the
 * CE overlay, so the SaaS build never reaches the /api/v1/modules endpoint it calls.
 */

const MARKETPLACE_URL = 'https://wirehq.net/marketplace';

function humanize(slug: string): string {
  const words = slug.replace(/-/g, ' ').trim();
  return words.charAt(0).toUpperCase() + words.slice(1);
}

/** Prefer the display catalogue's name/icon; fall back to a humanised slug for backend-only modules. */
function displayFor(slug: string): { name: string; icon: typeof Package } {
  const entry = getModule(slug);
  return { name: entry?.name ?? humanize(slug), icon: entry?.icon ?? Package };
}

function statusBadge(module: ActivatedModule) {
  if (module.status === 'Revoked') {
    return <Badge tone="danger" dot>Revoked</Badge>;
  }
  if (module.graceEndsUtc && new Date(module.graceEndsUtc).getTime() < Date.now()) {
    return <Badge tone="warning" dot>Lapsed</Badge>;
  }
  return <Badge tone="success" dot>Active</Badge>;
}

export function ModulesPage() {
  const hasPermission = useAuthStore((s) => s.hasPermission);
  const hasFeature = useAuthStore((s) => s.hasFeature);
  const canManage = hasPermission('marketplace.modules.manage');

  const toast = useToast();
  const { refresh } = useAuth();
  const activated = useActivatedModules();
  const manifests = useMarketplaceModules();
  const activate = useActivateModule();
  const deactivate = useDeactivateModule();
  const manifestMap = manifests.data ?? {};

  const [licenceKey, setLicenceKey] = useState('');
  const [pendingSlug, setPendingSlug] = useState<string | null>(null);

  const modules = activated.data ?? [];
  const activatedSlugs = new Set(modules.map((m) => m.slug));
  const activeCount = modules.filter(
    (m) => m.status === 'Active' && (!m.graceEndsUtc || new Date(m.graceEndsUtc).getTime() >= Date.now()),
  ).length;

  const fail = (e: unknown, fallback: string) => toast(e instanceof ApiError ? e.message : fallback, 'error');

  function onActivate(e: FormEvent) {
    e.preventDefault();
    const key = licenceKey.trim();
    if (!key) {
      toast('Paste the licence key from your purchase email.', 'error');
      return;
    }
    activate.mutate(key, {
      onSuccess: async (res) => {
        toast(`${displayFor(res.moduleSlug).name} activated.`);
        setLicenceKey('');
        // Re-resolve /me so the newly-unlocked feature (and its nav item) appears without a reload.
        await refresh();
      },
      onError: (err) => fail(err, 'That licence key could not be activated.'),
    });
  }

  function onDeactivate(module: ActivatedModule) {
    const { name } = displayFor(module.slug);
    if (!window.confirm(`Deactivate ${name}? Its capability will lock until you re-activate the licence.`)) {
      return;
    }
    setPendingSlug(module.slug);
    deactivate.mutate(module.slug, {
      onSuccess: async () => {
        toast(`${name} deactivated.`);
        await refresh();
      },
      onError: (err) => fail(err, 'Could not deactivate the module.'),
      onSettled: () => setPendingSlug(null),
    });
  }

  if (!canManage) {
    return (
      <>
        <PageHeader title="Modules" subtitle="Activate purchased Marketplace modules with a licence key." />
        <Card>
          <CardContent className="py-8 text-sm text-ink-500 dark:text-ink-400">
            You need the <span className="font-medium">Manage modules</span> permission to activate modules.
            Ask an administrator.
          </CardContent>
        </Card>
      </>
    );
  }

  return (
    <>
      <PageHeader
        title="Modules"
        subtitle="Extend your instance with one-off Marketplace modules — buy once, activate with a licence key."
        action={
          <a href={MARKETPLACE_URL} target="_blank" rel="noreferrer">
            <Button>
              Browse the Marketplace <ArrowUpRight className="size-4" />
            </Button>
          </a>
        }
      />

      <div className="grid grid-cols-1 gap-4 sm:grid-cols-2">
        <StatCard label="Activated modules" value={modules.length} icon={Package} />
        <StatCard label="Active now" value={activeCount} hint="granting their capability" icon={PackageCheck} />
      </div>

      {/* Activate */}
      <Card className="mt-8">
        <CardHeader>
          <CardTitle>Activate a module</CardTitle>
        </CardHeader>
        <CardContent>
          <p className="text-sm text-ink-500 dark:text-ink-400">
            Your licence key arrives by email after purchase. Paste it below — the key identifies which module to
            unlock, and the capability appears in your menu straight away.
          </p>
          <form onSubmit={onActivate} className="mt-4 flex flex-col gap-3 sm:flex-row sm:items-end">
            <div className="flex-1">
              <Field label="Licence key" htmlFor="licence-key">
                <Input
                  id="licence-key"
                  placeholder="v4.public.…"
                  className="font-mono"
                  value={licenceKey}
                  onChange={(e) => setLicenceKey(e.target.value)}
                  autoComplete="off"
                />
              </Field>
            </div>
            <Button type="submit" disabled={activate.isPending}>
              <KeyRound className="size-4" /> {activate.isPending ? 'Activating…' : 'Activate'}
            </Button>
          </form>
        </CardContent>
      </Card>

      {/* Activated modules */}
      <h2 className="mt-8 text-lg font-semibold text-ink-900 dark:text-ink-50">Activated modules</h2>
      {activated.isLoading ? (
        <Card className="mt-3">
          <CardContent className="py-8 text-sm text-ink-400">Loading…</CardContent>
        </Card>
      ) : modules.length === 0 ? (
        <Card className="mt-3">
          <CardContent className="flex flex-col items-center py-10 text-center">
            <PackageOpen className="size-8 text-ink-300 dark:text-ink-600" />
            <p className="mt-3 font-medium text-ink-700 dark:text-ink-200">No modules activated yet</p>
            <p className="mt-1 max-w-md text-sm text-ink-500 dark:text-ink-400">
              Buy a module in the Marketplace and activate its licence key above — it will appear here and its
              features unlock immediately.
            </p>
          </CardContent>
        </Card>
      ) : (
        <div className="mt-3 divide-y rounded-xl border dark:divide-ink-800 dark:border-ink-800">
          {modules.map((m) => {
            const { name, icon: Icon } = displayFor(m.slug);
            const mf = manifestMap[m.slug];
            const unlocked = m.features.length > 0 && m.features.every((f) => hasFeature(f));
            return (
              <div key={m.slug} className="flex items-center justify-between gap-4 p-4">
                <div className="flex items-start gap-3">
                  <div className="flex size-10 shrink-0 items-center justify-center rounded-lg bg-ink-100 dark:bg-ink-800">
                    <Icon className="size-5 text-gold-500 dark:text-gold-400" />
                  </div>
                  <div>
                    <div className="flex items-center gap-2">
                      <h3 className="font-medium text-ink-900 dark:text-ink-50">{name}</h3>
                      {statusBadge(m)}
                    </div>
                    <p className="mt-0.5 font-mono text-xs text-ink-400">
                      {m.slug}
                      {mf && <span className="text-ink-500"> · v{mf.version}</span>}
                    </p>
                    {unlocked && (
                      <p className="mt-1 inline-flex items-center gap-1 text-xs text-success-700 dark:text-success-500">
                        <CheckCircle2 className="size-3.5" /> Capability unlocked
                      </p>
                    )}
                  </div>
                </div>
                <Button
                  variant="ghost"
                  size="sm"
                  onClick={() => onDeactivate(m)}
                  disabled={deactivate.isPending && pendingSlug === m.slug}
                >
                  <Trash2 className="size-4" /> Deactivate
                </Button>
              </div>
            );
          })}
        </div>
      )}

      {/* Browse the catalogue */}
      <h2 className="mt-8 text-lg font-semibold text-ink-900 dark:text-ink-50">Available modules</h2>
      <p className="mt-1 text-sm text-ink-500 dark:text-ink-400">
        The Marketplace catalogue — each module is a one-off purchase for this instance.
      </p>
      <div className="mt-3 grid grid-cols-1 gap-4 sm:grid-cols-2 lg:grid-cols-3">
        {MARKETPLACE_MODULES.map((m) => {
          const isActivated = activatedSlugs.has(m.slug);
          const mf = manifestMap[m.slug];
          return (
            <Card key={m.slug}>
              <CardContent className="flex h-full flex-col pt-6">
                <div className="flex items-start justify-between">
                  <m.icon className="size-5 text-gold-500 dark:text-gold-400" />
                  {isActivated ? (
                    <Badge tone="success" dot>Activated</Badge>
                  ) : m.delivery === 'gate-only' ? (
                    // Purchasable but NOT activated on this instance — an affordance, not a status.
                    // (The green "Activated" badge above is the only one that reflects real activation.)
                    <Badge tone="gold">Available</Badge>
                  ) : (
                    <Badge tone="neutral">Coming soon</Badge>
                  )}
                </div>
                <h3 className="mt-3 font-medium text-ink-900 dark:text-ink-50">{m.name}</h3>
                {mf && (
                  <p className="mt-0.5 text-xs text-ink-400">
                    v{mf.version} · {mf.tier}
                  </p>
                )}
                <p className="mt-1 flex-1 text-sm text-ink-500 dark:text-ink-400">{m.tagline}</p>
                <div className="mt-4 flex items-center justify-between">
                  <span className="text-sm font-semibold text-ink-900 dark:text-ink-50">
                    {formatModulePrice(m.price)} <span className="font-normal text-ink-400">one-off</span>
                  </span>
                  <a href={`${MARKETPLACE_URL}/${m.slug}`} target="_blank" rel="noreferrer">
                    <Button variant="secondary" size="sm">
                      View <ArrowUpRight className="size-3.5" />
                    </Button>
                  </a>
                </div>
              </CardContent>
            </Card>
          );
        })}
      </div>
    </>
  );
}
