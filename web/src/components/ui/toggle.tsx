import { cn } from '@/lib/utils/cn';

/** A small accessible on/off switch. Controlled — pass `checked` + `onChange`. */
export function Toggle({
  checked,
  onChange,
  disabled,
  id,
  'aria-label': ariaLabel,
}: {
  checked: boolean;
  onChange: (checked: boolean) => void;
  disabled?: boolean;
  id?: string;
  'aria-label'?: string;
}) {
  return (
    <button
      type="button"
      role="switch"
      id={id}
      aria-checked={checked}
      aria-label={ariaLabel}
      disabled={disabled}
      onClick={() => onChange(!checked)}
      className={cn(
        'relative inline-flex h-6 w-11 shrink-0 cursor-pointer items-center rounded-full transition-colors',
        'focus-visible:outline-none focus-visible:ring-2 focus-visible:ring-gold-400/60',
        disabled && 'cursor-not-allowed opacity-50',
        checked ? 'bg-gold-500' : 'bg-ink-300 dark:bg-ink-700',
      )}
    >
      <span
        className={cn(
          'inline-block size-5 transform rounded-full bg-white shadow transition-transform',
          checked ? 'translate-x-[22px]' : 'translate-x-0.5',
        )}
      />
    </button>
  );
}
