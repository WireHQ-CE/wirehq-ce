import { forwardRef, type SelectHTMLAttributes } from 'react';
import { ChevronDown } from 'lucide-react';
import { cn } from '@/lib/utils/cn';

/** Styled native <select> — accessible and dependency-free. */
export const Select = forwardRef<HTMLSelectElement, SelectHTMLAttributes<HTMLSelectElement>>(
  ({ className, children, ...props }, ref) => (
    <div className="relative">
      <select
        ref={ref}
        className={cn(
          'h-9 w-full appearance-none rounded-md border bg-ink-0 px-3 pr-8 text-base text-ink-900',
          'border-ink-200 transition-colors duration-[120ms]',
          'dark:border-ink-700 dark:bg-ink-900 dark:text-ink-50',
          'disabled:cursor-not-allowed disabled:opacity-50',
          className,
        )}
        {...props}
      >
        {children}
      </select>
      <ChevronDown className="pointer-events-none absolute right-2.5 top-1/2 size-4 -translate-y-1/2 text-ink-400" />
    </div>
  ),
);
Select.displayName = 'Select';
