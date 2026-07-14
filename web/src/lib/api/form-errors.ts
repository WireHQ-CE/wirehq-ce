import { ApiError } from './client';

/**
 * Form error state split into per-field messages (keyed by the camelCased command property — e.g.
 * `cidr`, `interfaceAddress`, `email`) and a `general` message for failures that aren't tied to a field
 * (conflicts, forbidden, network errors). Drives inline `<Field error=…>` display plus a fallback line.
 */
export interface FormErrors {
  fields: Record<string, string>;
  general: string | null;
}

export const noFormErrors: FormErrors = { fields: {}, general: null };

/**
 * Maps a thrown request error to {@link FormErrors}. Validation failures (RFC 9457 `errors` map) become
 * per-field messages; everything else falls back to the error's message or the supplied fallback.
 */
export function toFormErrors(err: unknown, fallback = 'Something went wrong. Please try again.'): FormErrors {
  if (err instanceof ApiError) {
    if (err.errors && Object.keys(err.errors).length > 0) {
      const fields: Record<string, string> = {};
      for (const [key, messages] of Object.entries(err.errors)) {
        if (messages?.length) fields[key] = messages.join(' ');
      }
      return { fields, general: null };
    }
    return { fields: {}, general: err.message };
  }
  return { fields: {}, general: fallback };
}
