import { createContext, useContext, useEffect, useState, type ReactNode } from 'react';
import { useQuery } from '@tanstack/react-query';
import { config } from '@/app/config';
import { fetchBrandConfig, type BrandConfig } from './brand-api';

// Applies the operator's brand at runtime (docs/34 §4.5, the down-scoped v1): the product name (exposed via useBrand,
// used by the Logo + document.title), the logo/favicon images, and the single brand accent wired to the --ring CSS
// variable via the CSSOM (setProperty validates the value, so an invalid colour can never break out). To blunt the
// pre-login flash (FOUC) for repeat visitors it seeds from a localStorage cache, then refreshes from the API. The
// fuller colour-scale recolour is deliberately deferred (BR-10 down-scoped v1).

const STORAGE_KEY = 'wirehq.brand';
const FALLBACK_NAME = 'WireHQ';

export interface Brand {
  /** The resolved product name — the operator's, or the shipped default. */
  productName: string;
  brandColor: string | null;
  logoLightUrl: string | null;
  logoDarkUrl: string | null;
  faviconUrl: string | null;
}

const DEFAULT_BRAND: Brand = {
  productName: FALLBACK_NAME,
  brandColor: null,
  logoLightUrl: null,
  logoDarkUrl: null,
  faviconUrl: null,
};

const BrandContext = createContext<Brand>(DEFAULT_BRAND);

/** The active brand (operator overrides or the shipped WireHQ default). */
// eslint-disable-next-line react-refresh/only-export-components -- the context hook is colocated with its provider.
export function useBrand(): Brand {
  return useContext(BrandContext);
}

function resolve(config_: BrandConfig): Brand {
  const asset = (url: string | null) => (url ? `${config.apiBaseUrl}${url}` : null);
  return {
    productName: config_.productName?.trim() || FALLBACK_NAME,
    brandColor: config_.brandColor,
    logoLightUrl: asset(config_.logoLightUrl),
    logoDarkUrl: asset(config_.logoDarkUrl),
    faviconUrl: asset(config_.faviconUrl),
  };
}

function readCache(): Brand {
  try {
    const raw = localStorage.getItem(STORAGE_KEY);
    return raw ? { ...DEFAULT_BRAND, ...(JSON.parse(raw) as Partial<Brand>) } : DEFAULT_BRAND;
  } catch {
    return DEFAULT_BRAND;
  }
}

function apply(brand: Brand): void {
  const root = document.documentElement;

  // The single accent (down-scoped v1) — CSSOM setProperty validates the value, so a bad colour is a no-op.
  if (brand.brandColor) {
    root.style.setProperty('--ring', brand.brandColor);
    root.style.setProperty('--brand', brand.brandColor);
  } else {
    root.style.removeProperty('--ring');
    root.style.removeProperty('--brand');
  }

  if (brand.faviconUrl) {
    let link = document.querySelector<HTMLLinkElement>('link[rel~="icon"]');
    if (!link) {
      link = document.createElement('link');
      link.rel = 'icon';
      document.head.appendChild(link);
    }
    link.href = brand.faviconUrl;
  }

  if (brand.productName && brand.productName !== FALLBACK_NAME) {
    document.title = brand.productName;
  }
}

export function BrandProvider({ children }: { children: ReactNode }) {
  // Seed synchronously from cache (minimises the pre-login WireHQ flash on repeat loads), then refresh.
  const [brand, setBrand] = useState<Brand>(() => {
    const cached = readCache();
    apply(cached);
    return cached;
  });

  const { data } = useQuery({
    queryKey: ['branding', 'config'],
    queryFn: fetchBrandConfig,
    staleTime: 60_000,
  });

  useEffect(() => {
    if (!data) {
      return;
    }
    const resolved = resolve(data);
    setBrand(resolved);
    apply(resolved);
    try {
      localStorage.setItem(STORAGE_KEY, JSON.stringify(resolved));
    } catch {
      // A private-mode / storage-full browser just loses the FOUC optimisation — not fatal.
    }
  }, [data]);

  return <BrandContext.Provider value={brand}>{children}</BrandContext.Provider>;
}
