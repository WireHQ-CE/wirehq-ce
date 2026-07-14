import { AlertTriangle, Copy, RotateCw } from 'lucide-react';
import { useState } from 'react';
import { Button } from '@/components/ui/button';
import { Mark } from '@/components/brand/Logo';

/**
 * The friendly error screen shown by the error boundaries (docs/15 §12). Shows a calm message and,
 * when we have one, the copyable correlation reference the user can quote to support (ADR-030).
 */
export function ErrorFallback({
  reference,
  onReload,
  fullScreen = false,
}: {
  reference?: string;
  /** Reset action — re-render the boundary's children (route) or reload the page (root). */
  onReload: () => void;
  /** Root boundary uses the full viewport; the route boundary sits inside the app shell. */
  fullScreen?: boolean;
}) {
  return (
    <div
      className={
        fullScreen
          ? 'grid min-h-screen place-items-center bg-ink-50 px-6 dark:bg-ink-950'
          : 'grid min-h-[60vh] place-items-center px-6'
      }
    >
      <div className="w-full max-w-md text-center">
        {fullScreen && <Mark className="mx-auto mb-6 h-8 w-auto" />}
        <div className="mx-auto mb-4 grid size-12 place-items-center rounded-full bg-danger-500/10">
          <AlertTriangle className="size-6 text-danger-600" />
        </div>
        <h1 className="text-h2 text-ink-900 dark:text-ink-50">Something went wrong</h1>
        <p className="mt-2 text-base text-ink-500">
          An unexpected error occurred. Try again — if it keeps happening, contact support and quote the
          reference below.
        </p>
        {reference && <ReferenceChip reference={reference} />}
        <div className="mt-6 flex items-center justify-center gap-3">
          <Button onClick={onReload}>
            <RotateCw className="size-4" /> Try again
          </Button>
          <Button variant="secondary" onClick={() => (window.location.href = '/app')}>
            Go to dashboard
          </Button>
        </div>
      </div>
    </div>
  );
}

function ReferenceChip({ reference }: { reference: string }) {
  const [copied, setCopied] = useState(false);
  return (
    <button
      type="button"
      onClick={() => {
        void navigator.clipboard?.writeText(reference);
        setCopied(true);
        setTimeout(() => setCopied(false), 1500);
      }}
      title="Copy this reference for support"
      className="mx-auto mt-4 flex max-w-full items-center gap-2 rounded-md border border-ink-200 bg-ink-0 px-3 py-1.5 font-mono text-xs text-ink-500 transition-colors hover:text-ink-700 dark:border-ink-700 dark:bg-ink-850 dark:hover:text-ink-200"
    >
      <Copy className="size-3.5 shrink-0" />
      <span className="truncate">{copied ? 'Copied to clipboard' : reference}</span>
    </button>
  );
}
