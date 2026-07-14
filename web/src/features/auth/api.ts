import { api } from '@/lib/api/client';
import type { CurrentUser } from '@/stores/auth-store';

export interface AuthTokenResponse {
  accessToken: string;
  expiresIn: number;
  mfaRequired: boolean;
}

export interface RegisterInput {
  email: string;
  password: string;
  firstName: string;
  lastName: string;
  acceptTerms: boolean;
  /** Optional — a personal workspace is auto-created when omitted. */
  organizationName?: string;
  turnstileToken?: string;
}

/** Public security config the auth pages read to decide whether to render the Turnstile widget. */
export interface SecurityConfig {
  turnstileEnabled: boolean;
  turnstileSiteKey: string | null;
  /** False on invite-only (self-hosted) installs — the auth pages hide signup; the API enforces it. */
  registrationEnabled: boolean;
  /** True on a fresh, ownerless self-hosted instance — the auth pages route to /setup instead. */
  setupRequired: boolean;
}

/** Browser first-run setup (self-hosted): the first user claims the instance and becomes the Owner. */
export interface SetupInput {
  email: string;
  firstName: string;
  lastName: string;
  password: string;
  /** Optional — the instance is named "WireHQ" when omitted. */
  organizationName?: string;
}

export const authApi = {
  securityConfig: () => api.get<SecurityConfig>('/api/v1/auth/security-config'),

  setup: (input: SetupInput) =>
    api.post<{ userId: string; organizationId: string; organizationSlug: string }>(
      '/api/v1/auth/setup',
      input,
    ),

  login: (email: string, password: string, turnstileToken?: string) =>
    api.post<AuthTokenResponse>('/api/v1/auth/login', { email, password, turnstileToken }),

  register: (input: RegisterInput) =>
    api.post<{ userId: string; organizationId: string; organizationSlug: string }>(
      '/api/v1/auth/register',
      input,
    ),

  me: () => api.get<CurrentUser>('/api/v1/auth/me'),

  logout: () => api.post<void>('/api/v1/auth/logout'),

  verifyMfa: (code: string) =>
    api.post<AuthTokenResponse>('/api/v1/auth/mfa/verify', { code }),

  forgotPassword: (email: string, turnstileToken?: string) =>
    api.post<void>('/api/v1/auth/forgot-password', { email, turnstileToken }),

  resetPassword: (token: string, newPassword: string, turnstileToken?: string) =>
    api.post<void>('/api/v1/auth/reset-password', { token, newPassword, turnstileToken }),

  verifyEmail: (token: string) => api.post<void>('/api/v1/auth/verify-email', { token }),

  resendVerification: () => api.post<void>('/api/v1/auth/resend-verification'),
};
