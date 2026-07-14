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
}

export const notificationsApi = {
  get: () => api.get<NotificationPreferences>('/api/v1/account/notifications'),
  update: (prefs: NotificationPreferences) => api.put<void>('/api/v1/account/notifications', prefs),
};

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
