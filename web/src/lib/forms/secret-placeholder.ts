/**
 * Placeholder for a write-only secret input (site-wide convention).
 *
 * When a value is already stored we show a masked hint so the field reads as "configured"; when nothing is stored
 * the field is blank. The mask is placeholder-only — submitting the field empty keeps the stored value (the
 * "leave blank to keep" behaviour), so the mask can never become the submitted secret.
 */
export const SECRET_MASK = '••••••••';

export function secretPlaceholder(configured: boolean): string {
  return configured ? SECRET_MASK : '';
}
