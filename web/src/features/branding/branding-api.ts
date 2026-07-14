import { useMutation, useQuery, useQueryClient } from '@tanstack/react-query';
import { api } from '@/lib/api/client';

// The operator branding console hooks (docs/34). Super-Admin read/write of the install-global brand + logo/favicon
// uploads, over the authenticated /api/v1/branding endpoints. Invalidates the public brand config on every change so
// the shell re-applies the new brand immediately.

export interface BrandingSettings {
  productName: string | null;
  brandColor: string | null;
  logoLightAssetId: string | null;
  logoDarkAssetId: string | null;
  faviconAssetId: string | null;
  brandRevision: number;
}

export type BrandAssetKind = 'LogoLight' | 'LogoDark' | 'Favicon';

const settingsKey = ['branding', 'settings'] as const;
const publicKey = ['branding', 'config'] as const;

export function useBrandingSettings() {
  return useQuery({
    queryKey: settingsKey,
    queryFn: () => api.get<BrandingSettings>('/api/v1/branding/settings'),
  });
}

export function useUpdateBranding() {
  const qc = useQueryClient();
  return useMutation({
    mutationFn: (body: { productName: string | null; brandColor: string | null }) =>
      api.put<BrandingSettings>('/api/v1/branding/settings', body),
    onSuccess: () => invalidate(qc),
  });
}

export function useUploadBrandAsset() {
  const qc = useQueryClient();
  return useMutation({
    mutationFn: ({ kind, file }: { kind: BrandAssetKind; file: File }) => {
      const form = new FormData();
      form.append('file', file);
      return api.upload<{ assetId: string }>(`/api/v1/branding/assets/${kind}`, form);
    },
    onSuccess: () => invalidate(qc),
  });
}

export function useRemoveBrandAsset() {
  const qc = useQueryClient();
  return useMutation({
    mutationFn: (kind: BrandAssetKind) => api.delete<void>(`/api/v1/branding/assets/${kind}`),
    onSuccess: () => invalidate(qc),
  });
}

function invalidate(qc: ReturnType<typeof useQueryClient>) {
  void qc.invalidateQueries({ queryKey: settingsKey });
  void qc.invalidateQueries({ queryKey: publicKey });
}
