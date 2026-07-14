import { useMutation, useQuery, useQueryClient } from '@tanstack/react-query';
import { api } from '@/lib/api/client';
import type { PagedList, UserListItem } from '@/lib/api/types';

export function useUsers(search: string, page = 1) {
  return useQuery({
    queryKey: ['users', { search, page }],
    queryFn: () =>
      api.get<PagedList<UserListItem>>(
        `/api/v1/users?search=${encodeURIComponent(search)}&page=${page}&pageSize=25`,
      ),
  });
}

export function useInviteUser() {
  const queryClient = useQueryClient();
  return useMutation({
    mutationFn: (input: { email: string; name?: string; roleId?: string }) =>
      api.post('/api/v1/users/invitations', {
        email: input.email,
        name: input.name,
        roleIds: input.roleId ? [input.roleId] : undefined,
      }),
    onSuccess: () => queryClient.invalidateQueries({ queryKey: ['users'] }),
  });
}
