import { useMutation, useQuery, useQueryClient } from '@tanstack/react-query';
import { api } from '@/lib/api/client';

export interface OnboardingState {
  status: 'Pending' | 'Completed' | 'Skipped';
  shouldShow: boolean;
  companyName: string | null;
  companyWebsite: string | null;
  industry: string | null;
  teamSize: string | null;
  vpnUsers: string | null;
  currentVpnSolution: string | null;
  useCase: string;
}

export interface SaveOnboardingInput {
  companyName?: string | null;
  companyWebsite?: string | null;
  industry?: string | null;
  teamSize?: string | null;
  vpnUsers?: string | null;
  currentVpnSolution?: string | null;
  useCase?: string | null;
}

export function useOnboarding() {
  return useQuery({
    queryKey: ['onboarding'],
    queryFn: () => api.get<OnboardingState>('/api/v1/onboarding'),
  });
}

export function useSaveOnboarding() {
  const qc = useQueryClient();
  return useMutation({
    mutationFn: (input: SaveOnboardingInput) => api.put('/api/v1/onboarding', input),
    onSuccess: () => invalidate(qc),
  });
}

export function useSkipOnboarding() {
  const qc = useQueryClient();
  return useMutation({
    mutationFn: () => api.post('/api/v1/onboarding/skip'),
    onSuccess: () => invalidate(qc),
  });
}

function invalidate(qc: ReturnType<typeof useQueryClient>) {
  void qc.invalidateQueries({ queryKey: ['onboarding'] });
  // /me carries onboardingPending (drives the first-login redirect) — refresh it too.
  void qc.invalidateQueries({ queryKey: ['me'] });
}
