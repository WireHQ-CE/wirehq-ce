import { useMutation, useQuery, useQueryClient } from '@tanstack/react-query';
import { api } from '@/lib/api/client';

// Webhook endpoints + delivery history for the Settings → Webhooks console (docs/26-api-keys-webhooks.md §8).
// Kept-core: webhooks ship in every edition, entitlement-gated by api.keys (the CE defaults orgs to Enterprise).

/** A webhook endpoint in the active organization (list item). The signing secret is NEVER returned here. */
export interface Webhook {
  id: string;
  url: string;
  description: string | null;
  eventTypes: string[];
  status: 'Active' | 'Disabled';
  createdAtUtc: string;
}

/** A subscribable event pattern from the catalog, grouped for the picker. */
export interface WebhookEventType {
  pattern: string;
  group: string;
  description: string;
}

/** One delivery attempt — the customer-visible outbox history. */
export interface WebhookDelivery {
  id: string;
  endpointId: string;
  eventType: string;
  status: 'Pending' | 'Delivered' | 'Failed';
  attempts: number;
  lastResponseCode: number | null;
  createdAtUtc: string;
  deliveredAtUtc: string | null;
  nextAttemptAtUtc: string | null;
}

export interface UpsertWebhookInput {
  url: string;
  description: string | null;
  eventTypes: string[];
}

/** The create/rotate response — the plaintext signing secret, returned ONCE (only the ciphertext is stored). */
export interface WebhookSecretResult {
  signingSecret: string;
}

const webhooksKey = ['webhooks'] as const;

export function useWebhooks() {
  return useQuery({
    queryKey: webhooksKey,
    queryFn: () => api.get<Webhook[]>('/api/v1/webhooks'),
  });
}

/** The subscribable event-type catalog for the endpoint editor. */
export function useWebhookEventTypes() {
  return useQuery({
    queryKey: ['webhook-event-types'] as const,
    queryFn: () => api.get<WebhookEventType[]>('/api/v1/webhooks/event-types'),
  });
}

/** Recent delivery history for one endpoint. */
export function useWebhookDeliveries(endpointId: string | null) {
  return useQuery({
    queryKey: ['webhook-deliveries', endpointId] as const,
    queryFn: () => api.get<WebhookDelivery[]>(`/api/v1/webhooks/deliveries?endpointId=${endpointId}`),
    enabled: !!endpointId,
  });
}

function useWebhooksMutation<TArgs, TResult>(fn: (args: TArgs) => Promise<TResult>) {
  const qc = useQueryClient();
  return useMutation({ mutationFn: fn, onSuccess: () => qc.invalidateQueries({ queryKey: webhooksKey }) });
}

export const useCreateWebhook = () =>
  useWebhooksMutation((input: UpsertWebhookInput) => api.post<WebhookSecretResult>('/api/v1/webhooks', input));

export const useUpdateWebhook = () =>
  useWebhooksMutation(({ id, ...input }: UpsertWebhookInput & { id: string }) => api.put<void>(`/api/v1/webhooks/${id}`, input));

export const useSetWebhookStatus = () =>
  useWebhooksMutation(({ id, enabled }: { id: string; enabled: boolean }) => api.post<void>(`/api/v1/webhooks/${id}/status`, { enabled }));

export const useRotateWebhookSecret = () =>
  useWebhooksMutation((id: string) => api.post<WebhookSecretResult>(`/api/v1/webhooks/${id}/rotate-secret`));

export const useTestWebhook = () =>
  useWebhooksMutation((id: string) => api.post<void>(`/api/v1/webhooks/${id}/test`));

export const useDeleteWebhook = () =>
  useWebhooksMutation((id: string) => api.delete<void>(`/api/v1/webhooks/${id}`));
