import { useInfiniteQuery } from '@tanstack/react-query';
import { api } from '@/lib/api/client';
import { download } from '@/features/wireguard/download';
import type { AuditLogItem, CursorPage, PlatformAuditLogItem } from '@/lib/api/types';
import type { AuditFilterValues } from './types';

const PAGE_SIZE = 50;

/** Build a query string from a filter set, dropping empty values. */
function buildParams(filters: AuditFilterValues, extra: Record<string, string> = {}): URLSearchParams {
  const params = new URLSearchParams();
  for (const [key, value] of Object.entries({ ...filters, ...extra })) {
    if (value && value.trim()) {
      params.set(key, value.trim());
    }
  }
  return params;
}

/** The tenant's own audit feed — keyset-paginated (TanStack infinite query over the cursor). */
export function useAuditLogs(filters: AuditFilterValues) {
  return useInfiniteQuery({
    queryKey: ['audit-logs', filters],
    initialPageParam: undefined as string | undefined,
    queryFn: ({ pageParam }) => {
      const params = buildParams(filters, { pageSize: String(PAGE_SIZE) });
      if (pageParam) params.set('cursor', pageParam);
      return api.get<CursorPage<AuditLogItem>>(`/api/v1/audit-logs?${params}`);
    },
    getNextPageParam: (last) => last.nextCursor ?? undefined,
  });
}

/** The Super-Admin cross-tenant audit search — keyset-paginated. Each read is itself audited server-side. */
export function usePlatformAuditLogs(filters: AuditFilterValues, organizationId?: string) {
  return useInfiniteQuery({
    queryKey: ['platform-audit', organizationId ?? '', filters],
    initialPageParam: undefined as string | undefined,
    queryFn: ({ pageParam }) => {
      const extra: Record<string, string> = { pageSize: String(PAGE_SIZE) };
      if (organizationId) extra.organizationId = organizationId;
      const params = buildParams(filters, extra);
      if (pageParam) params.set('cursor', pageParam);
      return api.get<CursorPage<PlatformAuditLogItem>>(`/api/v1/platform/audit?${params}`);
    },
    getNextPageParam: (last) => last.nextCursor ?? undefined,
  });
}

/** Download the (filtered) audit feed as a CSV or JSON file. Enterprise-only (audit.export entitlement). */
export async function downloadAuditExport(filters: AuditFilterValues, format: 'csv' | 'json') {
  const params = buildParams(filters, { format });
  const blob = await api.blob(`/api/v1/audit-logs/export?${params}`);
  const stamp = new Date().toISOString().slice(0, 19).replace(/[:T]/g, '-');
  download(`audit-logs-${stamp}.${format}`, blob);
}
