import { useMutation, useQuery, useQueryClient } from '@tanstack/react-query';
import { api } from '@/lib/api/client';

// API keys + the grantable-scope catalog for the Settings → API keys console (docs/26-api-keys-webhooks.md §6).
// Kept-core: API keys ship in every edition, entitlement-gated by api.keys (the CE defaults orgs to Enterprise).

/** An API key in the active organization (list item). The plaintext secret is NEVER returned here — only its
 * display prefix + metadata. `status` is the server-computed effective status, folding in expiry. */
export interface ApiKey {
  id: string;
  name: string;
  keyPrefix: string;
  scopes: string[];
  status: 'Active' | 'Expired' | 'Revoked';
  expiresAtUtc: string | null;
  lastUsedAtUtc: string | null;
  createdAtUtc: string;
}

/** A grantable scope — a permission key from the RBAC catalog, grouped for the picker. */
export interface ApiKeyScopeOption {
  key: string;
  group: string;
  description: string;
}

export interface CreateApiKeyInput {
  name: string;
  scopes: string[];
  expiresAtUtc: string | null;
}

/** The create response — the plaintext secret is returned ONCE (only its hash is stored). */
export interface CreateApiKeyResult {
  id: string;
  key: string;
}

const apiKeysKey = ['api-keys'] as const;

export function useApiKeys() {
  return useQuery({
    queryKey: apiKeysKey,
    queryFn: () => api.get<ApiKey[]>('/api/v1/api-keys'),
  });
}

/** The grantable-scope catalog for the create dialog's picker. */
export function useApiKeyScopes() {
  return useQuery({
    queryKey: ['api-key-scopes'] as const,
    queryFn: () => api.get<ApiKeyScopeOption[]>('/api/v1/api-keys/scopes'),
  });
}

function useApiKeysMutation<TArgs, TResult>(fn: (args: TArgs) => Promise<TResult>) {
  const qc = useQueryClient();
  return useMutation({ mutationFn: fn, onSuccess: () => qc.invalidateQueries({ queryKey: apiKeysKey }) });
}

export const useCreateApiKey = () =>
  useApiKeysMutation((input: CreateApiKeyInput) => api.post<CreateApiKeyResult>('/api/v1/api-keys', input));

export const useRevokeApiKey = () =>
  useApiKeysMutation((id: string) => api.delete<void>(`/api/v1/api-keys/${id}`));
