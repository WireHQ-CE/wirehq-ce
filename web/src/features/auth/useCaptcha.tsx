import { useCallback, useState } from 'react';
import { useQuery } from '@tanstack/react-query';
import { TurnstileWidget } from '@/components/ui/TurnstileWidget';
import { authApi } from './api';

/** Public security config (whether the CAPTCHA is on, and the site key). Cached for the session. */
export function useSecurityConfig() {
  return useQuery({
    queryKey: ['security-config'],
    queryFn: () => authApi.securityConfig(),
    staleTime: 5 * 60 * 1000,
  });
}

/**
 * Drop-in CAPTCHA for an auth form. Renders nothing when Turnstile is disabled. Usage:
 * `const captcha = useCaptcha();` → render `{captcha.widget}`, disable submit while
 * `captcha.required && !captcha.token`, send `captcha.token`, and call `captcha.reset()` on error.
 */
export function useCaptcha() {
  const { data } = useSecurityConfig();
  const [token, setToken] = useState<string | null>(null);
  const [resetKey, setResetKey] = useState(0);

  const siteKey = data?.turnstileEnabled ? data.turnstileSiteKey : null;
  const required = Boolean(siteKey);

  const reset = useCallback(() => {
    setToken(null);
    setResetKey((k) => k + 1);
  }, []);

  const widget = siteKey ? (
    <TurnstileWidget
      key={resetKey}
      siteKey={siteKey}
      onVerify={setToken}
      onExpire={() => setToken(null)}
    />
  ) : null;

  return { required, token: token ?? undefined, widget, reset };
}
