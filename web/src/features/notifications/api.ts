import { useMutation, useQuery, useQueryClient } from '@tanstack/react-query';
import { api } from '@/lib/api/client';

// Notification rules + delivery history for the Settings → Notifications console (docs/35-notifications.md §4.4).
// Kept-core: the dispatch spine ships in every edition. Wave 1 delivers the free-core Email channel.

/** A notification routing rule in the active organization. */
export interface NotificationRule {
  id: string;
  name: string;
  eventPattern: string;
  channel: 'Email' | 'Chat' | 'Sms';
  audience: 'OptedInUsers' | 'Role';
  audienceRef: string | null;
  enabled: boolean;
  createdAtUtc: string;
}

/** A curated event pattern for the rule editor, grouped for the picker. */
export interface NotificationEventType {
  pattern: string;
  label: string;
  group: string;
}

/** One delivery — the org-visible outbox history. */
export interface NotificationDelivery {
  id: string;
  channel: string;
  recipient: string;
  subject: string;
  status: 'Pending' | 'Delivered' | 'Failed' | 'Cancelled';
  attempts: number;
  lastError: string | null;
  createdAtUtc: string;
  deliveredAtUtc: string | null;
}

export interface UpsertRuleInput {
  name: string;
  eventPattern: string;
  channelKind: 'Email' | 'Chat';
  audience: 'OptedInUsers' | 'Role';
  audienceRef: string | null;
}

/** Per-channel config status — never the destination URL (a bearer secret). */
export interface NotificationChannelConfig {
  channel: string;
  provider: string;
  configured: boolean;
  enabled: boolean;
}

export interface SetChatDestinationInput {
  provider: 'Slack' | 'Teams';
  destinationUrl: string;
}

const rulesKey = ['notification-rules'] as const;

export function useNotificationRules() {
  return useQuery({ queryKey: rulesKey, queryFn: () => api.get<NotificationRule[]>('/api/v1/notifications/rules') });
}

export function useNotificationEventTypes() {
  return useQuery({
    queryKey: ['notification-event-types'] as const,
    queryFn: () => api.get<NotificationEventType[]>('/api/v1/notifications/event-types'),
  });
}

export function useNotificationDeliveries() {
  return useQuery({
    queryKey: ['notification-deliveries'] as const,
    queryFn: () => api.get<NotificationDelivery[]>('/api/v1/notifications/deliveries'),
  });
}

export function useNotificationChannelConfigs() {
  return useQuery({
    queryKey: ['notification-channel-configs'] as const,
    queryFn: () => api.get<NotificationChannelConfig[]>('/api/v1/notifications/channel-configs'),
  });
}

function useRulesMutation<TArgs, TResult>(fn: (args: TArgs) => Promise<TResult>) {
  const qc = useQueryClient();
  return useMutation({
    mutationFn: fn,
    onSuccess: () => {
      qc.invalidateQueries({ queryKey: rulesKey });
      qc.invalidateQueries({ queryKey: ['notification-deliveries'] });
    },
  });
}

export const useCreateNotificationRule = () =>
  useRulesMutation((input: UpsertRuleInput) => api.post<string>('/api/v1/notifications/rules', input));

export const useUpdateNotificationRule = () =>
  useRulesMutation(({ id, ...input }: UpsertRuleInput & { id: string }) => api.put<void>(`/api/v1/notifications/rules/${id}`, input));

export const useSetNotificationRuleStatus = () =>
  useRulesMutation(({ id, enabled }: { id: string; enabled: boolean }) => api.post<void>(`/api/v1/notifications/rules/${id}/status`, { enabled }));

export const useTestNotificationRule = () =>
  useRulesMutation((id: string) => api.post<void>(`/api/v1/notifications/rules/${id}/test`));

export const useDeleteNotificationRule = () =>
  useRulesMutation((id: string) => api.delete<void>(`/api/v1/notifications/rules/${id}`));

export function useSetChatDestination() {
  const qc = useQueryClient();
  return useMutation({
    mutationFn: (input: SetChatDestinationInput) => api.put<void>('/api/v1/notifications/channel-configs/chat', input),
    onSuccess: () => qc.invalidateQueries({ queryKey: ['notification-channel-configs'] }),
  });
}
