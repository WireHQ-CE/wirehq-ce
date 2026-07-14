import { forwardRef, type InputHTMLAttributes, type ReactNode } from 'react';
import { cn } from '@/lib/utils/cn';

export const Input = forwardRef<HTMLInputElement, InputHTMLAttributes<HTMLInputElement>>(
  ({ className, type = 'text', ...props }, ref) => (
    <input
      ref={ref}
      type={type}
      className={cn(
        'h-9 w-full rounded-md border bg-ink-0 px-3 text-base text-ink-900 placeholder:text-ink-400',
        'border-ink-200 transition-colors duration-[120ms]',
        'dark:bg-ink-900 dark:text-ink-50 dark:border-ink-700',
        'disabled:cursor-not-allowed disabled:opacity-50',
        className,
      )}
      {...props}
    />
  ),
);
Input.displayName = 'Input';

export function Field({
  label,
  htmlFor,
  error,
  children,
}: {
  label: string;
  htmlFor: string;
  error?: string;
  children: ReactNode;
}) {
  return (
    <div className="space-y-1.5">
      <label htmlFor={htmlFor} className="text-xs font-medium uppercase tracking-wide text-ink-500">
        {label}
      </label>
      {children}
      {error && <p className="text-sm text-danger-600 dark:text-danger-500">{error}</p>}
    </div>
  );
}
