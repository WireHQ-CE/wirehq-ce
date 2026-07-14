import { useEffect, useRef } from 'react';

interface TurnstileOptions {
  sitekey: string;
  theme?: 'light' | 'dark' | 'auto';
  callback?: (token: string) => void;
  'expired-callback'?: () => void;
  'error-callback'?: () => void;
}

interface TurnstileApi {
  render: (el: HTMLElement, opts: TurnstileOptions) => string;
  remove: (id: string) => void;
  reset: (id?: string) => void;
}

declare global {
  interface Window {
    turnstile?: TurnstileApi;
  }
}

const SCRIPT_SRC = 'https://challenges.cloudflare.com/turnstile/v0/api.js?render=explicit';
let scriptPromise: Promise<void> | null = null;

/** Load the Cloudflare Turnstile script exactly once, shared across all widget instances. */
function loadTurnstile(): Promise<void> {
  if (window.turnstile) return Promise.resolve();
  scriptPromise ??= new Promise<void>((resolve, reject) => {
    const script = document.createElement('script');
    script.src = SCRIPT_SRC;
    script.async = true;
    script.defer = true;
    script.onload = () => resolve();
    script.onerror = () => {
      scriptPromise = null;
      reject(new Error('Failed to load Cloudflare Turnstile.'));
    };
    document.head.appendChild(script);
  });
  return scriptPromise;
}

/**
 * Renders a Cloudflare Turnstile widget and reports the solved token. Tokens are single-use — pass a
 * fresh `key` (e.g. bump a counter on a failed submit) to force a clean re-challenge.
 */
export function TurnstileWidget({
  siteKey,
  onVerify,
  onExpire,
}: {
  siteKey: string;
  onVerify: (token: string) => void;
  onExpire?: () => void;
}) {
  const containerRef = useRef<HTMLDivElement>(null);
  const widgetId = useRef<string | null>(null);
  // Keep the latest callbacks without re-rendering the widget on every parent render.
  const callbacks = useRef({ onVerify, onExpire });
  callbacks.current = { onVerify, onExpire };

  useEffect(() => {
    let cancelled = false;

    loadTurnstile()
      .then(() => {
        if (cancelled || !containerRef.current || !window.turnstile) return;
        widgetId.current = window.turnstile.render(containerRef.current, {
          sitekey: siteKey,
          theme: 'dark',
          callback: (token) => callbacks.current.onVerify(token),
          'expired-callback': () => callbacks.current.onExpire?.(),
          'error-callback': () => callbacks.current.onExpire?.(),
        });
      })
      .catch(() => {
        /* script load failure — the widget simply won't appear */
      });

    return () => {
      cancelled = true;
      if (widgetId.current && window.turnstile) {
        try {
          window.turnstile.remove(widgetId.current);
        } catch {
          /* widget already gone */
        }
        widgetId.current = null;
      }
    };
  }, [siteKey]);

  return <div ref={containerRef} className="mt-1 flex justify-center" />;
}
