import { useQuery } from '@tanstack/react-query';
import { api } from '@/lib/api/client';
import { EDITION } from '@/lib/edition';
import { useAuthStore } from '@/stores/auth-store';

// The install's update situation (docs/30). Only fetched on the Community Edition and only for an operator who
// can actually run the upgrade (org.settings.update) — SaaS and regular members never call the endpoint.

export type UpdateState = 'Unknown' | 'UpToDate' | 'UpdateAvailable';
export type UpdateSeverity = 'None' | 'Low' | 'Medium' | 'High' | 'Critical';

export interface UpdateStatus {
  state: UpdateState;
  currentVersion: string;
  latestVersion: string | null;
  security: boolean;
  severity: UpdateSeverity;
  unsupported: boolean;
  requiresMigration: boolean;
  summary: string | null;
  releaseUrl: string | null;
  checkedAtUtc: string | null;
}

export function useUpdateStatus() {
  const canManage = useAuthStore((s) => s.hasPermission('org.settings.update'));
  return useQuery({
    queryKey: ['update-status'],
    queryFn: () => api.get<UpdateStatus>('/api/v1/updates/status'),
    enabled: EDITION === 'community' && canManage,
    staleTime: 60 * 60 * 1000, // an hour — the backend polls daily
    refetchOnWindowFocus: false,
  });
}

/** A newer version the operator should act on (drives the banner + indicator). */
export function isUpdateAvailable(status: UpdateStatus | undefined): status is UpdateStatus {
  return status?.state === 'UpdateAvailable';
}

/** Loud + non-dismissible: a security release, or a run below the minimum supported version. */
export function isLoudUpdate(status: UpdateStatus): boolean {
  return status.security || status.unsupported;
}
