import { useMutation, useQuery, useQueryClient } from '@tanstack/react-query';
import { api } from '@/lib/api/client';
import type {
  AddTeamMemberInput,
  AddTeamMemberResult,
  CreateTeamInput,
  TeamDetail,
  TeamListItem,
  UpdateTeamInput,
} from './types';

const base = '/api/v1/teams';

export const teamKeys = {
  all: ['teams'] as const,
  team: (id: string) => ['team', id] as const,
};

// ---- Queries ----

export function useTeams(search?: string) {
  return useQuery({
    queryKey: [...teamKeys.all, search ?? ''] as const,
    queryFn: () => api.get<TeamListItem[]>(`${base}${search ? `?search=${encodeURIComponent(search)}` : ''}`),
  });
}

export function useTeam(id: string) {
  return useQuery({ queryKey: teamKeys.team(id), queryFn: () => api.get<TeamDetail>(`${base}/${id}`) });
}

// ---- Mutations ----

export function useCreateTeam() {
  const qc = useQueryClient();
  return useMutation({
    mutationFn: (input: CreateTeamInput) => api.post<{ id: string }>(base, input),
    onSuccess: () => void qc.invalidateQueries({ queryKey: teamKeys.all }),
  });
}

export function useUpdateTeam(id: string) {
  const qc = useQueryClient();
  return useMutation({
    mutationFn: (input: UpdateTeamInput) => api.patch(`${base}/${id}`, input),
    onSuccess: () => {
      void qc.invalidateQueries({ queryKey: teamKeys.team(id) });
      void qc.invalidateQueries({ queryKey: teamKeys.all });
    },
  });
}

export function useDeleteTeam() {
  const qc = useQueryClient();
  return useMutation({
    mutationFn: (id: string) => api.delete(`${base}/${id}`),
    onSuccess: () => void qc.invalidateQueries({ queryKey: teamKeys.all }),
  });
}

export function useAddTeamMember(id: string) {
  const qc = useQueryClient();
  return useMutation({
    mutationFn: (input: AddTeamMemberInput) => api.post<AddTeamMemberResult>(`${base}/${id}/members`, input),
    onSuccess: () => {
      void qc.invalidateQueries({ queryKey: teamKeys.team(id) });
      void qc.invalidateQueries({ queryKey: teamKeys.all });
      // A new colleague invited via the team is a new org member — refresh the Users list too.
      void qc.invalidateQueries({ queryKey: ['users'] });
    },
  });
}

export function useRemoveTeamMember(id: string) {
  const qc = useQueryClient();
  return useMutation({
    mutationFn: (membershipId: string) => api.delete(`${base}/${id}/members/${membershipId}`),
    onSuccess: () => {
      void qc.invalidateQueries({ queryKey: teamKeys.team(id) });
      void qc.invalidateQueries({ queryKey: teamKeys.all });
    },
  });
}
