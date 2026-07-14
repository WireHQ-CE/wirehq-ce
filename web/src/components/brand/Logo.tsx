import { cn } from '@/lib/utils/cn';
import { EDITION_TAGLINE } from '@/lib/edition';
import { useBrand } from '@/features/branding/BrandProvider';
import iconUrl from '@/assets/brand/icon.svg';
import logoLightUrl from '@/assets/brand/logo-light.svg';
import logoDarkUrl from '@/assets/brand/logo-dark.svg';

/** The WireHQ icon mark (gold gradient glyph). Theme-independent — used as a standalone badge/spinner. */
export function Mark({ className }: { className?: string }) {
  return <img src={iconUrl} alt="" aria-hidden className={cn('h-7 w-auto', className)} />;
}

/**
 * Full logo lockup (icon + wordmark). Renders the **operator's** brand logo when one is configured (docs/34) — a
 * light + dark variant, each falling back to the shipped WireHQ mark — otherwise the designer's two pre-coloured
 * WireHQ variants, swapped by theme so the artwork always reads correctly. The alt text is the operator's product
 * name. Only the displayed variant is announced to screen readers; editioned builds add a small tagline beneath.
 */
export function Logo({ className }: { className?: string }) {
  const { productName, logoLightUrl: brandLight, logoDarkUrl: brandDark } = useBrand();
  const img = cn('h-8 w-auto', className);
  const lockup = (
    <span className="inline-flex">
      <img src={brandLight ?? logoLightUrl} alt={productName} className={cn(img, 'object-contain object-left dark:hidden')} />
      <img src={brandDark ?? logoDarkUrl} alt={productName} className={cn(img, 'hidden object-contain object-left dark:block')} />
    </span>
  );

  if (!EDITION_TAGLINE) {
    return lockup;
  }

  return (
    <span className="inline-flex flex-col items-start">
      {lockup}
      <span className="mt-0.5 text-[10px] font-semibold uppercase tracking-[0.18em] text-gold-500">
        {EDITION_TAGLINE}
      </span>
    </span>
  );
}
