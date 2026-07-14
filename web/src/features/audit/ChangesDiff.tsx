/**
 * Renders an audit row's `changes` diff (the EF before/after capture, docs/15 §5) as a readable
 * before → after table. The stored shape is a JSON array of
 * `{ entity, id, operation, changes: { Prop: { old, new } } }`; we degrade gracefully to raw text if it
 * doesn't parse.
 */
interface ChangeEntry {
  entity?: string;
  id?: string;
  operation?: string;
  changes?: Record<string, { old?: unknown; new?: unknown }>;
}

function format(value: unknown): string {
  if (value === null || value === undefined) return '—';
  if (typeof value === 'object') return JSON.stringify(value);
  return String(value);
}

export function ChangesDiff({ changes }: { changes: string | null }) {
  if (!changes) {
    return <p className="text-xs text-ink-400">No field changes recorded for this event.</p>;
  }

  let entries: ChangeEntry[];
  try {
    const parsed = JSON.parse(changes);
    entries = Array.isArray(parsed) ? parsed : [parsed];
  } catch {
    return <pre className="overflow-x-auto rounded bg-ink-100 p-3 text-xs text-ink-600 dark:bg-ink-800 dark:text-ink-300">{changes}</pre>;
  }

  return (
    <div className="space-y-3">
      {entries.map((entry, i) => (
        <div key={i} className="rounded-md border dark:border-ink-800">
          <div className="flex items-center gap-2 border-b px-3 py-1.5 text-xs dark:border-ink-800">
            <span className="font-medium uppercase tracking-wide text-ink-500">{entry.operation ?? 'changed'}</span>
            <span className="font-mono text-ink-700 dark:text-ink-200">{entry.entity ?? 'entity'}</span>
            {entry.id && <span className="font-mono text-ink-400">{String(entry.id).slice(0, 8)}…</span>}
          </div>
          {entry.changes && Object.keys(entry.changes).length > 0 ? (
            <table className="w-full text-xs">
              <tbody>
                {Object.entries(entry.changes).map(([prop, val]) => (
                  <tr key={prop} className="border-b last:border-0 dark:border-ink-800">
                    <td className="w-1/4 px-3 py-1.5 font-mono text-ink-500">{prop}</td>
                    <td className="px-3 py-1.5 font-mono text-danger-600 line-through dark:text-danger-500">{format(val?.old)}</td>
                    <td className="px-3 py-1.5 font-mono text-success-700 dark:text-success-500">{format(val?.new)}</td>
                  </tr>
                ))}
              </tbody>
            </table>
          ) : (
            <p className="px-3 py-1.5 text-xs text-ink-400">No field-level detail.</p>
          )}
        </div>
      ))}
    </div>
  );
}
