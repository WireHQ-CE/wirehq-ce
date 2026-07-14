import { useMutation, useQuery, useQueryClient } from '@tanstack/react-query';
import { api } from '@/lib/api/client';

// Roles + the permission catalog for the Settings → Roles console (docs/25-custom-roles.md). Kept-core: custom
// roles ship in every edition, entitlement-gated by rbac.custom_roles (the CE defaults orgs to Enterprise).

/** A role in the active organization (list item — for role pickers and the roles console). */
export interface OrgRole {
  id: string;
  name: string;
  description: string | null;
  isSystem: boolean;
}

/** A role's detail — its granted permission ids, for the editor. */
export interface RoleDetail extends OrgRole {
  permissionIds: string[];
}

/** A permission from the global catalog, grouped for the picker. */
export interface PermissionItem {
  id: string;
  key: string;
  group: string;
  description: string;
}

export interface UpsertRoleInput {
  name: string;
  description: string | null;
  permissionIds: string[];
}

const rolesKey = ['roles'] as const;

export function useOrgRoles() {
  return useQuery({
    queryKey: rolesKey,
    queryFn: () => api.get<OrgRole[]>('/api/v1/roles'),
  });
}

export function useRole(id: string | null) {
  return useQuery({
    queryKey: ['roles', id] as const,
    queryFn: () => api.get<RoleDetail>(`/api/v1/roles/${id}`),
    enabled: !!id,
  });
}

export function usePermissionCatalog() {
  return useQuery({
    queryKey: ['permissions'] as const,
    queryFn: () => api.get<PermissionItem[]>('/api/v1/roles/permissions'),
  });
}

function useRolesMutation<TArgs>(fn: (args: TArgs) => Promise<unknown>) {
  const qc = useQueryClient();
  return useMutation({ mutationFn: fn, onSuccess: () => qc.invalidateQueries({ queryKey: rolesKey }) });
}

export const useCreateRole = () =>
  useRolesMutation((input: UpsertRoleInput) => api.post<{ id: string }>('/api/v1/roles', input));

export const useUpdateRole = () =>
  useRolesMutation(({ id, ...input }: UpsertRoleInput & { id: string }) => api.put<void>(`/api/v1/roles/${id}`, input));

export const useDeleteRole = () =>
  useRolesMutation((id: string) => api.delete<void>(`/api/v1/roles/${id}`));
