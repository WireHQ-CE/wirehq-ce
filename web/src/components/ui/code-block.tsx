import { useState } from 'react';
import { Check, Copy } from 'lucide-react';
import { cn } from '@/lib/utils/cn';

/** Monospace config/snippet block with a copy-to-clipboard button. */
export function CodeBlock({ content, className }: { content: string; className?: string }) {
  const [copied, setCopied] = useState(false);

  async function copy() {
    try {
      await navigator.clipboard.writeText(content);
      setCopied(true);
      setTimeout(() => setCopied(false), 1500);
    } catch {
      // Clipboard may be unavailable (insecure context) — fail silently.
    }
  }

  return (
    <div className={cn('relative rounded-md border bg-ink-50 dark:border-ink-800 dark:bg-ink-900', className)}>
      <button
        type="button"
        onClick={copy}
        className="absolute right-2 top-2 inline-flex items-center gap-1 rounded-md border bg-ink-0 px-2 py-1 text-xs text-ink-600 transition-colors hover:bg-ink-100 dark:border-ink-700 dark:bg-ink-850 dark:text-ink-300 dark:hover:bg-ink-800"
      >
        {copied ? <Check className="size-3.5 text-success-600" /> : <Copy className="size-3.5" />}
        {copied ? 'Copied' : 'Copy'}
      </button>
      <pre className="overflow-x-auto whitespace-pre-wrap break-all p-3 pr-20 font-mono text-xs leading-relaxed text-ink-800 dark:text-ink-200">
        {content}
      </pre>
    </div>
  );
}
