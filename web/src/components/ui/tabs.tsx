import type { ReactNode } from 'react';
import { cn } from '@/lib/utils/cn';

/** Minimal controlled tab strip — the parent owns the active value. A `disabled` tab stays visible
 * (discoverability) but can't be selected — used e.g. for module-gated areas in the CE. */
export function Tabs<T extends string>({
  tabs,
  value,
  onChange,
  className,
}: {
  tabs: { value: T; label: ReactNode; count?: number; disabled?: boolean; title?: string }[];
  value: T;
  onChange: (value: T) => void;
  className?: string;
}) {
  return (
    <div className={cn('flex gap-1 border-b dark:border-ink-800', className)}>
      {tabs.map((t) => (
        <button
          key={t.value}
          type="button"
          onClick={() => !t.disabled && onChange(t.value)}
          disabled={t.disabled}
          title={t.title}
          className={cn(
            'relative -mb-px flex items-center gap-1.5 border-b-2 px-3 py-2 text-sm font-medium transition-colors',
            t.disabled
              ? 'cursor-not-allowed border-transparent text-ink-300 dark:text-ink-600'
              : value === t.value
                ? 'border-gold-500 text-ink-900 dark:text-ink-50'
                : 'border-transparent text-ink-500 hover:text-ink-800 dark:hover:text-ink-200',
          )}
        >
          {t.label}
          {t.count !== undefined && (
            <span className="rounded-full bg-ink-100 px-1.5 text-xs text-ink-500 dark:bg-ink-800">
              {t.count}
            </span>
          )}
        </button>
      ))}
    </div>
  );
}
