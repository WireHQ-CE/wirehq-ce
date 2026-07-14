/**
 * The build's edition — config-as-code (docs/17 §6). This is the Community Edition's copy of the
 * one-constant file; the private SaaS source ships `'saas'` here.
 */
export const EDITION: 'saas' | 'community' = 'community';

/** The tagline rendered under the logo lockup. */
export const EDITION_TAGLINE: string | null = (EDITION as string) === 'community' ? 'Community Edition' : null;
