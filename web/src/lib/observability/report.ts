import type { ApiError } from '@/lib/api/client';

/**
 * Client-error reporting seam (Community Edition — telemetry-free).
 *
 * Captures the request correlation id and routes client-side errors to a single, structured
 * reporting function that logs to the console. The Community Edition ships no telemetry backend, so
 * this is the one place a self-hoster could later wire their own sink; the rest of the app already
 * reports through it. The correlation reference is what a user quotes to support.
 */

/** The `X-Correlation-Id` of the most recent API response (the W3C trace id). */
let lastCorrelationId: string | undefined;

/** Record the correlation id echoed by an API response. Called by the api client. */
export function setLastCorrelationId(id: string | null | undefined): void {
  if (id) lastCorrelationId = id;
}

/** The correlation reference to quote to support — newest first: the error's own, else the last seen. */
export function getCorrelationRef(error?: unknown): string | undefined {
  const fromError = (error as ApiError | undefined)?.correlationId;
  return fromError ?? lastCorrelationId;
}

/** Build version, injected at build time from web/package.json (see vite.config.ts). */
const buildVersion: string = typeof __APP_VERSION__ === 'string' ? __APP_VERSION__ : 'dev';

export interface ClientErrorContext {
  /** Where the error came from, e.g. `error-boundary`, `route`, `window.onerror`, `query`, `mutation`. */
  source: string;
  /** Extra structured detail (route, query key, etc.). */
  [key: string]: unknown;
}

/** The single client-error sink. Structured, logged to the console (no telemetry backend in CE). */
export function reportClientError(error: unknown, context: ClientErrorContext): void {
  const correlationRef = getCorrelationRef(error);
  const record = {
    correlationRef,
    route: typeof window !== 'undefined' ? window.location.pathname : undefined,
    buildVersion,
    userAgent: typeof navigator !== 'undefined' ? navigator.userAgent : undefined,
    message: error instanceof Error ? error.message : String(error),
    ...context,
  };

  console.error('[client-error]', record, error);
}

let installed = false;

/**
 * Install global last-resort capture for errors that escape React + TanStack Query: uncaught
 * exceptions (`window.onerror`) and unhandled promise rejections (`window.onunhandledrejection`).
 * Idempotent — safe under React StrictMode's double-invoke.
 */
export function installGlobalErrorHandlers(): void {
  if (installed || typeof window === 'undefined') return;
  installed = true;

  window.addEventListener('error', (event) => {
    reportClientError(event.error ?? event.message, { source: 'window.onerror' });
  });

  window.addEventListener('unhandledrejection', (event) => {
    reportClientError(event.reason, { source: 'window.onunhandledrejection' });
  });
}
