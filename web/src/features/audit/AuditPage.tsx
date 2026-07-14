import { useState } from 'react';
import { Download, ScrollText } from 'lucide-react';
import { PageHeader } from '@/components/layout/AppShell';
import { Button } from '@/components/ui/button';
import { useToast } from '@/components/ui/toast';
import { EmptyState } from '@/components/data/EmptyState';
import { useAuthStore } from '@/stores/auth-store';
import { downloadAuditExport, useAuditLogs } from './api';
import { AuditFilters } from './AuditFilters';
import { AuditTable } from './AuditTable';
import { LoadMore } from './LoadMore';
import { emptyAuditFilters, type AuditFilterValues } from './types';

/** The customer audit console: filterable, with a changes diff viewer, export, and cursor "Load more". */
export function AuditPage() {
  const [filters, setFilters] = useState<AuditFilterValues>(emptyAuditFilters);
  const query = useAuditLogs(filters);
  const canExport = useAuthStore((s) => s.user?.entitlements?.features.includes('audit.export') ?? false);
  const toast = useToast();
  const [exporting, setExporting] = useState<'csv' | 'json' | null>(null);

  const items = query.data?.pages.flatMap((p) => p.items) ?? [];

  async function exportAs(format: 'csv' | 'json') {
    setExporting(format);
    try {
      await downloadAuditExport(filters, format);
    } catch {
      toast('Could not export the audit log.', 'error');
    } finally {
      setExporting(null);
    }
  }

  return (
    <>
      <PageHeader title="Audit Logs" subtitle="An immutable record of every security-relevant action." />

      <AuditFilters value={filters} onApply={setFilters}>
        {canExport && (
          <>
            <Button variant="secondary" size="sm" disabled={exporting !== null} onClick={() => void exportAs('csv')}>
              <Download /> {exporting === 'csv' ? 'Exporting…' : 'CSV'}
            </Button>
            <Button variant="secondary" size="sm" disabled={exporting !== null} onClick={() => void exportAs('json')}>
              <Download /> {exporting === 'json' ? 'Exporting…' : 'JSON'}
            </Button>
          </>
        )}
      </AuditFilters>

      {query.isLoading ? (
        <div className="space-y-px rounded-lg border p-2 dark:border-ink-800">
          {Array.from({ length: 8 }).map((_, i) => (
            <div key={i} className="h-10 animate-pulse rounded bg-ink-100 dark:bg-ink-800" />
          ))}
        </div>
      ) : items.length === 0 ? (
        <EmptyState icon={ScrollText} title="No matching audit events" description="Sign-ins, invites, and changes appear here. Adjust the filters to widen your search." />
      ) : (
        <>
          <AuditTable items={items} />
          <LoadMore query={query} />
        </>
      )}
    </>
  );
}
