import { config } from '@/app/config';

// The public brand config (docs/34) — what the shell fetches pre-login to render the operator's brand. Anonymous GET
// (like the marketplace prices), fail-soft to the shipped WireHQ brand so the app always renders. On the SaaS build
// the endpoint returns the default (branding is install-global, a CE capability), so this is a harmless no-op there.

export interface BrandConfig {
  productName: string | null;
  brandColor: string | null;
  logoLightUrl: string | null;
  logoDarkUrl: string | null;
  faviconUrl: string | null;
  brandRevision: number;
}

export const DEFAULT_BRAND_CONFIG: BrandConfig = {
  productName: null,
  brandColor: null,
  logoLightUrl: null,
  logoDarkUrl: null,
  faviconUrl: null,
  brandRevision: 0,
};

export async function fetchBrandConfig(): Promise<BrandConfig> {
  try {
    const response = await fetch(`${config.apiBaseUrl}/api/v1/branding`);
    if (!response.ok) {
      return DEFAULT_BRAND_CONFIG;
    }
    return (await response.json()) as BrandConfig;
  } catch {
    return DEFAULT_BRAND_CONFIG;
  }
}
