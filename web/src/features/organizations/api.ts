import { useMutation, useQuery, useQueryClient } from '@tanstack/react-query';
import { api } from '@/lib/api/client';
import type { Organization, OrganizationUpdate } from '@/lib/api/types';

export function useCurrentOrganization() {
  return useQuery({
    queryKey: ['organization', 'current'],
    queryFn: () => api.get<Organization>('/api/v1/organizations/current'),
  });
}

export function useUpdateOrganization() {
  const queryClient = useQueryClient();
  return useMutation({
    mutationFn: (input: OrganizationUpdate) => api.patch<void>('/api/v1/organizations/current', input),
    onSuccess: () => queryClient.invalidateQueries({ queryKey: ['organization', 'current'] }),
  });
}

// NB: the billing-profile hooks live in BillingProfileSection.tsx (self-contained; the Community
// Edition strip removes that file — docs/17 §5).
