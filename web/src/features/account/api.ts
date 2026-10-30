import { useMutation, useQuery, useQueryClient } from '@tanstack/react-query';
import { api } from '@/lib/api/client';

export interface EnrollMfaResponse {
  secret: string;
  otpAuthUri: string;
  qrCodePngBase64: string;
}

export interface ConfirmMfaResponse {
  recoveryCodes: string[];
}

export interface SessionItem {
  id: string;
  ipAddress: string | null;
  userAgent: string | null;
  createdAtUtc: string;
  lastSeenAtUtc: string;
  isCurrent: boolean;
}

export const accountApi = {
  enrollMfa: () => api.post<EnrollMfaResponse>('/api/v1/account/mfa/enroll'),
  confirmMfa: (code: string) => api.post<ConfirmMfaResponse>('/api/v1/account/mfa/confirm', { code }),
  disableMfa: (password: string) => api.post<void>('/api/v1/account/mfa/disable', { password }),
  changePassword: (currentPassword: string, newPassword: string) =>
    api.post<void>('/api/v1/account/password', { currentPassword, newPassword }),
  updateProfile: (input: UpdateProfileInput) => api.patch<void>('/api/v1/account/profile', input),
  uploadAvatar: (file: File) => {
    const form = new FormData();
    form.append('file', file);
    return api.upload<void>('/api/v1/account/avatar', form);
  },
  removeAvatar: () => api.delete<void>('/api/v1/account/avatar'),
};

export interface NotificationPreferences {
  securityAlerts: boolean;
  vpnStatusAlerts: boolean;
  productAnnouncements: boolean;
  billingNotifications: boolean;
  marketingEmails: boolean;
  serviceStatusAlerts: boolean;
  /**
   * SMS alerts consent (Notifications Wave 4 — docs/36). Read here, but WRITTEN through the dedicated
   * `PUT /account/notifications/sms-consent` endpoint (it records an immutable consent event) — never this PUT.
   */
  smsAlerts: boolean;
}

export const notificationsApi = {
  get: () => api.get<NotificationPreferences>('/api/v1/account/notifications'),
  // `smsAlerts` is consent — it is set via `useSetSmsConsent`, not this PUT, so it is omitted from the payload.
  update: (prefs: NotificationPreferences) =>
    api.put<void>('/api/v1/account/notifications', {
      securityAlerts: prefs.securityAlerts,
      vpnStatusAlerts: prefs.vpnStatusAlerts,
      productAnnouncements: prefs.productAnnouncements,
      billingNotifications: prefs.billingNotifications,
      marketingEmails: prefs.marketingEmails,
      serviceStatusAlerts: prefs.serviceStatusAlerts,
    }),
};

// --- SMS: phone verification + alerts consent (Notifications Wave 4 — docs/36 §10 Slice B) ------------------
// Gated on `notifications.sms`; the module is dark by a code-level send-gate, so the OTP path is non-oracular
// (Start returns 204 on both the happy and every silent path — see docs/36 §5).

/** The signed-in user's verified-phone + org SMS-provider status (never a code or credential). */
export interface PhoneStatus {
  phoneVerified: boolean;
  phoneMasked: string | null;
  /** Whether the org has an SMS provider configured — verification can't be started without one. */
  providerConfigured: boolean;
  /** The exact opt-in disclosure to display at the consent step (versioned for the audit record). */
  disclosureText: string;
  disclosureVersion: string;
}

const phoneStatusKey = ['account-phone'] as const;

export function usePhoneStatus(enabled = true) {
  return useQuery({
    queryKey: phoneStatusKey,
    queryFn: () => api.get<PhoneStatus>('/api/v1/account/phone'),
    enabled,
  });
}

/** Begin verification. 204 on the happy AND every silent path (non-oracular); 400 only on format/cooldown/not-configured. */
export function useStartPhoneVerification() {
  return useMutation({
    mutationFn: (phoneNumber: string) => api.post<void>('/api/v1/account/phone/verify/start', { phoneNumber }),
  });
}

/** Confirm with the 6-digit code. 204 on success; 400 (generic) on any failure. */
export function useConfirmPhoneVerification() {
  const qc = useQueryClient();
  return useMutation({
    mutationFn: (code: string) => api.post<void>('/api/v1/account/phone/verify/confirm', { code }),
    onSuccess: () => qc.invalidateQueries({ queryKey: phoneStatusKey }),
  });
}

/** Set (or clear) SMS-alerts consent — writes an immutable consent event server-side. */
export function useSetSmsConsent() {
  return useMutation({
    mutationFn: (enabled: boolean) => api.put<void>('/api/v1/account/notifications/sms-consent', { enabled }),
  });
}

export interface UpdateProfileInput {
  firstName: string;
  lastName: string;
  username?: string | null;
  jobTitle?: string | null;
  phone?: string | null;
  timezone?: string | null;
  language?: string | null;
}

export function useSessions() {
  return useQuery({
    queryKey: ['sessions'],
    queryFn: () => api.get<SessionItem[]>('/api/v1/sessions'),
  });
}

export function useRevokeSession() {
  const queryClient = useQueryClient();
  return useMutation({
    mutationFn: (sessionId: string) => api.delete<void>(`/api/v1/sessions/${sessionId}`),
    onSuccess: () => queryClient.invalidateQueries({ queryKey: ['sessions'] }),
  });
}

export function useRevokeAllSessions() {
  const queryClient = useQueryClient();
  return useMutation({
    mutationFn: () => api.post<void>('/api/v1/sessions/revoke-all'),
    onSuccess: () => queryClient.invalidateQueries({ queryKey: ['sessions'] }),
  });
}
