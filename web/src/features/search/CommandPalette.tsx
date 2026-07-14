import { useEffect, useMemo, useRef, useState } from 'react';
import { createPortal } from 'react-dom';
import { useNavigate } from 'react-router-dom';
import { CornerDownLeft, Search } from 'lucide-react';
import { cn } from '@/lib/utils/cn';
import { useAuthStore } from '@/stores/auth-store';
import { useSearchEntries, type SearchEntry, type SearchSection } from './registry';

/**
 * The ⌘K command palette: live, fuzzy search over the permission-filtered registry — instant
 * navigation to any page, setting or action THIS account can reach. Entirely client-side: no
 * query ever leaves the browser, and recents are stored locally, keyed by user id, so nothing
 * crosses accounts even on a shared machine. (Entity search — peers/users/teams over the
 * RLS-scoped API — is the planned second layer and will render below these results.)
 */

const SECTION_ORDER: SearchSection[] = ['Navigate', 'Account', 'Actions', 'Platform'];
const MAX_RESULTS = 12;
const MAX_RECENTS = 5;

function recentsKey(userId: string | undefined) {
  return `wirehq-palette-recents-${userId ?? 'anon'}`;
}

function loadRecents(userId: string | undefined): string[] {
  try {
    return JSON.parse(localStorage.getItem(recentsKey(userId)) ?? '[]') as string[];
  } catch {
    return [];
  }
}

function pushRecent(userId: string | undefined, id: string) {
  const next = [id, ...loadRecents(userId).filter((x) => x !== id)].slice(0, MAX_RECENTS);
  localStorage.setItem(recentsKey(userId), JSON.stringify(next));
}

/** Simple, dependency-free relevance score. 0 = no match. */
function score(entry: SearchEntry, query: string): number {
  const q = query.toLowerCase().trim();
  if (!q) return 0;
  const title = entry.title.toLowerCase();
  if (title.startsWith(q)) return 100;
  if (title.split(/\s+/).some((w) => w.startsWith(q))) return 80;
  if (title.includes(q)) return 60;
  let best = 0;
  for (const k of entry.keywords) {
    const kw = k.toLowerCase();
    if (kw === q) best = Math.max(best, 70);
    else if (kw.startsWith(q)) best = Math.max(best, 55);
    else if (kw.split(/\s+/).some((w) => w.startsWith(q))) best = Math.max(best, 45);
    else if (kw.includes(q)) best = Math.max(best, 35);
  }
  if (entry.hint?.toLowerCase().includes(q)) best = Math.max(best, 30);
  return best;
}

export function CommandPalette({ open, onClose }: { open: boolean; onClose: () => void }) {
  const navigate = useNavigate();
  const entries = useSearchEntries();
  const userId = useAuthStore((s) => s.user?.userId);
  const [query, setQuery] = useState('');
  const [highlight, setHighlight] = useState(0);
  const inputRef = useRef<HTMLInputElement>(null);
  const listRef = useRef<HTMLDivElement>(null);

  // Fresh state every time it opens.
  useEffect(() => {
    if (open) {
      setQuery('');
      setHighlight(0);
      // Focus after the overlay paints.
      requestAnimationFrame(() => inputRef.current?.focus());
    }
  }, [open]);

  const results = useMemo(() => {
    const q = query.trim();
    if (!q) {
      // Empty query: recents first, then the top-level destinations.
      const recents = loadRecents(userId)
        .map((id) => entries.find((e) => e.id === id))
        .filter((e): e is SearchEntry => Boolean(e));
      const rest = entries.filter((e) => e.section === 'Navigate' && !recents.includes(e));
      return [...recents, ...rest].slice(0, MAX_RESULTS);
    }
    return entries
      .map((e) => ({ e, s: score(e, q) }))
      .filter((x) => x.s > 0)
      .sort(
        (a, b) => b.s - a.s || SECTION_ORDER.indexOf(a.e.section) - SECTION_ORDER.indexOf(b.e.section),
      )
      .slice(0, MAX_RESULTS)
      .map((x) => x.e);
  }, [entries, query, userId]);

  // Keep the highlight in range as results change.
  useEffect(() => {
    setHighlight((h) => Math.min(h, Math.max(0, results.length - 1)));
  }, [results]);

  function go(entry: SearchEntry) {
    pushRecent(userId, entry.id);
    onClose();
    navigate(entry.path);
  }

  function onKeyDown(e: React.KeyboardEvent) {
    if (e.key === 'ArrowDown') {
      e.preventDefault();
      setHighlight((h) => (h + 1) % Math.max(1, results.length));
    } else if (e.key === 'ArrowUp') {
      e.preventDefault();
      setHighlight((h) => (h - 1 + Math.max(1, results.length)) % Math.max(1, results.length));
    } else if (e.key === 'Enter') {
      e.preventDefault();
      const entry = results[highlight];
      if (entry) go(entry);
    } else if (e.key === 'Escape') {
      e.preventDefault();
      onClose();
    }
  }

  // Keep the highlighted row in view when navigating with the keyboard.
  useEffect(() => {
    listRef.current
      ?.querySelector<HTMLElement>(`[data-index="${highlight}"]`)
      ?.scrollIntoView({ block: 'nearest' });
  }, [highlight]);

  if (!open) return null;

  const grouped = SECTION_ORDER.map((section) => ({
    section,
    items: results.filter((r) => r.section === section),
  })).filter((g) => g.items.length > 0);
  const showRecentsLabel = !query.trim() && loadRecents(userId).length > 0;

  // Portal to <body>: the Topbar's backdrop-blur makes the header a containing block for fixed
  // descendants, which would trap (and mis-centre) the overlay inside the header's box.
  return createPortal(
    <div
      className="fixed inset-0 z-50 bg-ink-950/60 p-4 pt-[12vh] backdrop-blur-sm"
      onMouseDown={(e) => {
        if (e.target === e.currentTarget) onClose();
      }}
      role="dialog"
      aria-modal="true"
      aria-label="Search"
    >
      <div className="mx-auto w-full max-w-lg overflow-hidden rounded-xl border border-ink-200 bg-ink-0 shadow-2xl dark:border-ink-700 dark:bg-ink-900">
        <div className="flex items-center gap-2.5 border-b border-ink-100 px-4 dark:border-ink-800">
          <Search className="size-4 shrink-0 text-ink-400" />
          <input
            ref={inputRef}
            value={query}
            onChange={(e) => {
              setQuery(e.target.value);
              setHighlight(0);
            }}
            onKeyDown={onKeyDown}
            placeholder="Search pages, settings and actions…"
            // focus-visible:shadow-none: a deliberate exception to the global focus ring — the input
            // is the palette's only focusable control and the panel itself signals focus; the ring
            // (drawn when opened via ⌘K) is pure noise here.
            className="h-12 w-full bg-transparent text-base text-ink-900 outline-none focus-visible:shadow-none placeholder:text-ink-400 dark:text-ink-50"
            aria-label="Search pages, settings and actions"
          />
          <kbd className="rounded border border-ink-200 px-1.5 text-xs text-ink-400 dark:border-ink-700">esc</kbd>
        </div>

        <div ref={listRef} className="max-h-[50vh] overflow-y-auto p-2">
          {results.length === 0 ? (
            <p className="px-3 py-8 text-center text-sm text-ink-400">
              No matches for “{query}” — try “MFA”, “users” or “WireGuard”.
            </p>
          ) : (
            grouped.map(({ section, items }) => (
              <div key={section}>
                <p className="px-3 pb-1 pt-2 text-[11px] font-semibold uppercase tracking-wide text-ink-400">
                  {showRecentsLabel && section === grouped[0].section ? 'Recent & pages' : section}
                </p>
                {items.map((entry) => {
                  const index = results.indexOf(entry);
                  return (
                    <button
                      key={entry.id}
                      data-index={index}
                      onClick={() => go(entry)}
                      onMouseMove={() => setHighlight(index)}
                      className={cn(
                        'flex w-full items-center gap-3 rounded-lg px-3 py-2 text-left',
                        index === highlight
                          ? 'bg-gold-500/10 text-ink-900 dark:text-ink-50'
                          : 'text-ink-700 dark:text-ink-200',
                      )}
                      aria-selected={index === highlight}
                      role="option"
                    >
                      <entry.icon
                        className={cn(
                          'size-4 shrink-0',
                          index === highlight ? 'text-gold-500 dark:text-gold-400' : 'text-ink-400',
                        )}
                      />
                      <span className="min-w-0 flex-1">
                        <span className="block truncate text-sm font-medium">{entry.title}</span>
                        {entry.hint && (
                          <span className="block truncate text-xs text-ink-400">{entry.hint}</span>
                        )}
                      </span>
                      {index === highlight && <CornerDownLeft className="size-3.5 shrink-0 text-ink-400" />}
                    </button>
                  );
                })}
              </div>
            ))
          )}
        </div>

        <div className="flex items-center gap-4 border-t border-ink-100 px-4 py-2 text-[11px] text-ink-400 dark:border-ink-800">
          <span>
            <kbd className="rounded border border-ink-200 px-1 dark:border-ink-700">↑↓</kbd> navigate
          </span>
          <span>
            <kbd className="rounded border border-ink-200 px-1 dark:border-ink-700">↵</kbd> open
          </span>
          <span className="ml-auto">Results reflect your account's access</span>
        </div>
      </div>
    </div>,
    document.body,
  );
}
