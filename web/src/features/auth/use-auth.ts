import { useCallback } from 'react';
import { useQueryClient } from '@tanstack/react-query';
import { api } from '@/lib/api/client';
import { useAuthStore } from '@/stores/auth-store';
import { authApi, type RegisterInput } from './api';

/** Imperative auth actions. Server state (the user) is mirrored into the auth store for guards. */
export function useAuth() {
  const queryClient = useQueryClient();
  const { setAccessToken, setUser, reset, status, user } = useAuthStore();

  const loadCurrentUser = useCallback(async () => {
    const me = await authApi.me();
    setUser(me);
    return me;
  }, [setUser]);

  /** Resolve the session on app load: the API client refreshes from the cookie if needed. */
  const bootstrap = useCallback(async () => {
    try {
      await loadCurrentUser();
    } catch {
      reset();
    }
  }, [loadCurrentUser, reset]);

  const login = useCallback(
    async (email: string, password: string, turnstileToken?: string) => {
      const res = await authApi.login(email, password, turnstileToken);
      setAccessToken(res.accessToken);
      if (res.mfaRequired) {
        return { mfaRequired: true as const };
      }
      await loadCurrentUser();
      return { mfaRequired: false as const };
    },
    [setAccessToken, loadCurrentUser],
  );

  const verifyMfa = useCallback(
    async (code: string) => {
      const res = await authApi.verifyMfa(code);
      setAccessToken(res.accessToken);
      await loadCurrentUser();
    },
    [setAccessToken, loadCurrentUser],
  );

  const register = useCallback((input: RegisterInput) => authApi.register(input), []);

  /** Start acting as a customer's admin: swap to the (time-boxed) impersonation token, drop cached data,
   * reload /me. A reason is required and audited (ADR-032). */
  const impersonate = useCallback(
    async (organizationId: string, reason: string) => {
      const res = await api.post<{ accessToken: string }>(
        `/api/v1/platform/customers/${organizationId}/impersonate`, { reason });
      setAccessToken(res.accessToken);
      queryClient.clear();
      await loadCurrentUser();
    },
    [setAccessToken, loadCurrentUser, queryClient],
  );

  /** End impersonation: restore the operator's own platform session. */
  const stopImpersonating = useCallback(async () => {
    const res = await api.post<{ accessToken: string }>('/api/v1/platform/impersonation/exit');
    setAccessToken(res.accessToken);
    queryClient.clear();
    await loadCurrentUser();
  }, [setAccessToken, loadCurrentUser, queryClient]);

  const logout = useCallback(async () => {
    try {
      await authApi.logout();
    } finally {
      reset();
      queryClient.clear();
    }
  }, [reset, queryClient]);

  return { status, user, bootstrap, refresh: loadCurrentUser, login, verifyMfa, register, logout, impersonate, stopImpersonating };
}
