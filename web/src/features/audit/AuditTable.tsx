import { useState } from 'react';
import { ChevronDown, ChevronRight } from 'lucide-react';
import { Badge } from '@/components/ui/badge';
import type { AuditLogItem, PlatformAuditLogItem } from '@/lib/api/types';
import { ChangesDiff } from './ChangesDiff';

type Row = AuditLogItem | PlatformAuditLogItem;

function isPlatform(row: Row): row is PlatformAuditLogItem {
  return 'organizationName' in row;
}

/** A shared audit table with expandable rows. With `showTenant`, adds the owning-tenant column (platform view). */
export function AuditTable({ items, showTenant = false }: { items: Row[]; showTenant?: boolean }) {
  const columns = showTenant ? 7 : 6;
  return (
    <div className="overflow-hidden rounded-lg border bg-ink-0 dark:border-ink-800 dark:bg-ink-900">
      <table className="w-full text-sm">
        <thead>
          <tr className="border-b text-left text-xs uppercase tracking-wide text-ink-500 dark:border-ink-800">
            <th className="px-5 py-3 font-medium">Action</th>
            {showTenant && <th className="px-5 py-3 font-medium">Tenant</th>}
            <th className="px-5 py-3 font-medium">Actor</th>
            <th className="px-5 py-3 font-medium">Outcome</th>
            <th className="px-5 py-3 font-medium">Target</th>
            <th className="px-5 py-3 font-medium">Reference</th>
            <th className="px-5 py-3 font-medium">When</th>
          </tr>
        </thead>
        <tbody>
          {items.map((row) => (
            <AuditRow key={row.id} row={row} showTenant={showTenant} columns={columns} />
          ))}
        </tbody>
      </table>
    </div>
  );
}

function AuditRow({ row, showTenant, columns }: { row: Row; showTenant: boolean; columns: number }) {
  const [open, setOpen] = useState(false);
  const Chevron = open ? ChevronDown : ChevronRight;

  return (
    <>
      <tr
        className="cursor-pointer border-b last:border-0 hover:bg-ink-50 dark:border-ink-800 dark:hover:bg-ink-850"
        onClick={() => setOpen((o) => !o)}
      >
        <td className="px-5 py-3 font-mono text-ink-800 dark:text-ink-100">
          <span className="inline-flex items-center gap-1.5">
            <Chevron className="size-3.5 text-ink-400" />
            {row.action}
          </span>
        </td>
        {showTenant && (
          <td className="px-5 py-3 text-ink-600 dark:text-ink-300">
            {isPlatform(row) ? (row.organizationName ?? <span className="text-ink-400">platform</span>) : null}
          </td>
        )}
        <td className="px-5 py-3 text-ink-600 dark:text-ink-300">
          {row.actorEmail ?? (row.actorType === 'anonymous' ? 'anonymous' : '—')}
          {row.actorType === 'impersonation' && (
            <span className="ml-1 text-xs text-gold-600 dark:text-gold-400">(impersonated)</span>
          )}
        </td>
        <td className="px-5 py-3">
          <Badge tone={row.outcome === 'Success' ? 'success' : 'danger'} dot>{row.outcome}</Badge>
        </td>
        <td className="px-5 py-3 text-ink-500">{row.targetType ?? '—'}</td>
        <td className="px-5 py-3">
          {row.correlationId ? (
            <button
              type="button"
              onClick={(e) => {
                e.stopPropagation();
                void navigator.clipboard?.writeText(row.correlationId!);
              }}
              title={`Copy reference ${row.correlationId}`}
              className="font-mono text-xs text-ink-400 transition-colors hover:text-ink-700 dark:hover:text-ink-200"
            >
              {row.correlationId.slice(0, 8)}…
            </button>
          ) : (
            <span className="text-ink-400">—</span>
          )}
        </td>
        <td className="px-5 py-3 tabular text-ink-500">{new Date(row.occurredAtUtc).toLocaleString()}</td>
      </tr>
      {open && (
        <tr className="border-b bg-ink-50/60 dark:border-ink-800 dark:bg-ink-850/40">
          <td colSpan={columns} className="px-5 py-4">
            <dl className="mb-3 grid grid-cols-2 gap-x-6 gap-y-1 text-xs text-ink-500 md:grid-cols-4">
              {row.targetId && <Detail label="Target id" value={row.targetId} mono />}
              {row.ipAddress && <Detail label="IP address" value={row.ipAddress} mono />}
              {row.correlationId && <Detail label="Reference" value={row.correlationId} mono />}
              <Detail label="Actor type" value={row.actorType} />
            </dl>
            <ChangesDiff changes={row.changes} />
          </td>
        </tr>
      )}
    </>
  );
}

function Detail({ label, value, mono }: { label: string; value: string; mono?: boolean }) {
  return (
    <div>
      <dt className="uppercase tracking-wide text-ink-400">{label}</dt>
      <dd className={mono ? 'font-mono text-ink-600 dark:text-ink-300' : 'text-ink-600 dark:text-ink-300'}>{value}</dd>
    </div>
  );
}
