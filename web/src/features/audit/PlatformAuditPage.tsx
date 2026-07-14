import { useState } from 'react';
import { ScrollText, ShieldAlert } from 'lucide-react';
import { PageHeader } from '@/components/layout/AppShell';
import { Input } from '@/components/ui/input';
import { EmptyState } from '@/components/data/EmptyState';
import { usePlatformAuditLogs } from './api';
import { AuditFilters } from './AuditFilters';
import { AuditTable } from './AuditTable';
import { LoadMore } from './LoadMore';
import { emptyAuditFilters, type AuditFilterValues } from './types';

/**
 * The Super-Admin cross-tenant audit search (docs/15 §10): read any/all tenants' audit without impersonation.
 * Every search is itself audited server-side (audit-the-auditor) — surfaced here so operators know.
 */
export function PlatformAuditPage() {
  const [filters, setFilters] = useState<AuditFilterValues>(emptyAuditFilters);
  const [orgId, setOrgId] = useState('');
  const query = usePlatformAuditLogs(filters, orgId.trim() || undefined);

  const items = query.data?.pages.flatMap((p) => p.items) ?? [];

  return (
    <>
      <PageHeader title="Audit Search" subtitle="Search the audit log across every tenant — no impersonation required." />

      <div className="mb-4 flex items-start gap-2 rounded-lg border border-gold-200 bg-gold-50 px-4 py-2.5 text-sm text-gold-800 dark:border-gold-400/20 dark:bg-gold-400/10 dark:text-gold-300">
        <ShieldAlert className="mt-0.5 size-4 shrink-0" />
        <span>Every search you run here is itself recorded in the audit log (who searched whose audit, with which filters).</span>
      </div>

      <AuditFilters value={filters} onApply={setFilters}>
        <Input
          className="w-64"
          placeholder="Tenant id (optional)"
          value={orgId}
          onChange={(e) => setOrgId(e.target.value)}
          aria-label="Filter to one tenant by organization id"
        />
      </AuditFilters>

      {query.isLoading ? (
        <div className="space-y-px rounded-lg border p-2 dark:border-ink-800">
          {Array.from({ length: 8 }).map((_, i) => (
            <div key={i} className="h-10 animate-pulse rounded bg-ink-100 dark:bg-ink-800" />
          ))}
        </div>
      ) : items.length === 0 ? (
        <EmptyState icon={ScrollText} title="No matching audit events" description="Search across all tenants, or narrow to one by its organization id. Adjust the filters to widen your search." />
      ) : (
        <>
          <AuditTable items={items} showTenant />
          <LoadMore query={query} />
        </>
      )}
    </>
  );
}
