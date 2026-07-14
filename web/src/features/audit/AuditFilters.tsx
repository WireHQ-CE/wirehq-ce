import { useState } from 'react';
import { Search, X } from 'lucide-react';
import { Button } from '@/components/ui/button';
import { Input } from '@/components/ui/input';
import { Select } from '@/components/ui/select';
import type { AuditFilterValues } from './types';

/**
 * The shared audit filter bar (used by the customer console and the Super-Admin search). Holds a local draft
 * and only lifts it on Apply, so typing doesn't refetch on every keystroke.
 */
export function AuditFilters({
  value,
  onApply,
  children,
}: {
  value: AuditFilterValues;
  onApply: (next: AuditFilterValues) => void;
  children?: React.ReactNode;
}) {
  const [draft, setDraft] = useState<AuditFilterValues>(value);

  function set<K extends keyof AuditFilterValues>(key: K, v: string) {
    setDraft((d) => ({ ...d, [key]: v || undefined }));
  }

  const hasFilters = Object.values(draft).some(Boolean);

  return (
    <form
      onSubmit={(e) => {
        e.preventDefault();
        onApply(draft);
      }}
      className="mb-4 space-y-3 rounded-lg border bg-ink-0 p-4 dark:border-ink-800 dark:bg-ink-900"
    >
      <div className="flex items-center gap-2">
        <Search className="size-4 shrink-0 text-ink-400" />
        <Input
          placeholder="Search action, actor, target…"
          value={draft.q ?? ''}
          onChange={(e) => set('q', e.target.value)}
        />
      </div>
      <div className="grid grid-cols-2 gap-3 md:grid-cols-3 lg:grid-cols-4">
        <Input placeholder="Action (wg.network.created)" value={draft.action ?? ''} onChange={(e) => set('action', e.target.value)} />
        <Input placeholder="Category (wg, identity…)" value={draft.category ?? ''} onChange={(e) => set('category', e.target.value)} />
        <Input placeholder="Actor email" value={draft.actor ?? ''} onChange={(e) => set('actor', e.target.value)} />
        <Input placeholder="Target" value={draft.target ?? ''} onChange={(e) => set('target', e.target.value)} />
        <Select value={draft.outcome ?? ''} onChange={(e) => set('outcome', e.target.value)} aria-label="Outcome">
          <option value="">Any outcome</option>
          <option value="Success">Success</option>
          <option value="Failure">Failure</option>
        </Select>
        <label className="flex flex-col text-xs text-ink-500">
          <span className="mb-0.5">From</span>
          <Input type="date" value={draft.from ?? ''} onChange={(e) => set('from', e.target.value)} />
        </label>
        <label className="flex flex-col text-xs text-ink-500">
          <span className="mb-0.5">To</span>
          <Input type="date" value={draft.to ?? ''} onChange={(e) => set('to', e.target.value)} />
        </label>
      </div>
      <div className="flex items-center gap-2">
        <Button type="submit" size="sm"><Search /> Apply</Button>
        {hasFilters && (
          <Button
            type="button"
            variant="ghost"
            size="sm"
            onClick={() => {
              setDraft({});
              onApply({});
            }}
          >
            <X /> Clear
          </Button>
        )}
        <div className="ml-auto flex items-center gap-2">{children}</div>
      </div>
    </form>
  );
}
