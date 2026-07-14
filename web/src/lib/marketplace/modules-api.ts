import { useQuery } from '@tanstack/react-query';
import { config } from '@/app/config';

// The public Marketplace module manifest (docs/33 §5.2, ADR-048) — version, tier, status, docs/changelog anchors and
// delivery for the backed modules. Anonymous GET, served by the kept-core `GET /api/v1/marketplace/modules` in EVERY
// edition, so both the SaaS marketplace pages and the CE Modules console read one source of truth. Like the prices
// hook it uses a plain fetch (no JWT) and falls back to `{}` on any error, so a page never breaks if it's unreachable.

const MODULES_PATH = '/api/v1/marketplace/modules';

export interface MarketplaceModuleManifest {
  slug: string;
  name: string;
  category: string;
  version: string;
  summary: string;
  docsAnchor: string;
  changelogAnchor: string;
  minCeVersion: string;
  /** 'Pro' | 'Enterprise'. */
  tier: string;
  /** 'Available' | 'ComingSoon'. */
  status: string;
  /** 'GateOnly' | 'CodeDelivered' | null (not-yet-built entry). */
  delivery: string | null;
}

export type ModuleManifestMap = Record<string, MarketplaceModuleManifest>;

async function fetchMarketplaceModules(): Promise<ModuleManifestMap> {
  const response = await fetch(`${config.apiBaseUrl}${MODULES_PATH}`);
  if (!response.ok) {
    return {};
  }
  const rows = (await response.json()) as MarketplaceModuleManifest[];
  const map: ModuleManifestMap = {};
  for (const row of rows) {
    map[row.slug] = row;
  }
  return map;
}

/** The backed Marketplace module manifests, keyed by slug. Falls back to `{}` on any error. */
export function useMarketplaceModules() {
  return useQuery({
    queryKey: ['marketplace', 'module-manifests'],
    queryFn: fetchMarketplaceModules,
    staleTime: 60_000,
  });
}
