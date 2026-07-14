import { useMutation, useQuery, useQueryClient } from '@tanstack/react-query';
import { api } from '@/lib/api/client';

// The Community Edition Marketplace module-activation console (docs/29-ce-marketplace-modules.md M-9). Hits the
// CE-only /api/v1/modules endpoint — this file lives in web/src (survives the CE strip) but is only routed by
// the CE overlay, so it never calls the endpoint in the SaaS build.

/** One module activated on this install and its state. */
export interface ActivatedModule {
  slug: string;
  status: 'Active' | 'Revoked';
  activatedAtUtc: string;
  /** The offline-grace boundary (Wave 3); a past value means lapsed. Null after a local activation. */
  graceEndsUtc: string | null;
  /** The plan feature keys this module unlocks (from the backend catalogue). */
  features: string[];
}

/** The activate response — the module the licence key unlocked. */
export interface ActivateModuleResult {
  moduleSlug: string;
}

const modulesKey = ['modules'] as const;

export function useActivatedModules() {
  return useQuery({
    queryKey: modulesKey,
    queryFn: () => api.get<ActivatedModule[]>('/api/v1/modules'),
  });
}

export function useActivateModule() {
  const qc = useQueryClient();
  return useMutation({
    mutationFn: (licenceKey: string) =>
      api.post<ActivateModuleResult>('/api/v1/modules/activate', { licenceKey }),
    onSuccess: () => qc.invalidateQueries({ queryKey: modulesKey }),
  });
}

export function useDeactivateModule() {
  const qc = useQueryClient();
  return useMutation({
    mutationFn: (slug: string) => api.post<void>(`/api/v1/modules/${slug}/deactivate`),
    onSuccess: () => qc.invalidateQueries({ queryKey: modulesKey }),
  });
}
