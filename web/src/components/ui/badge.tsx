import type { ReactNode } from 'react';
import { cva, type VariantProps } from 'class-variance-authority';
import { cn } from '@/lib/utils/cn';

const badgeVariants = cva(
  'inline-flex items-center gap-1.5 rounded-full px-2 py-0.5 text-xs font-medium',
  {
    variants: {
      tone: {
        neutral: 'bg-ink-100 text-ink-600 dark:bg-ink-800 dark:text-ink-300',
        success: 'bg-success-50 text-success-700 dark:bg-success-700/20 dark:text-success-500',
        warning: 'bg-warning-50 text-warning-700 dark:bg-warning-700/20 dark:text-warning-500',
        danger: 'bg-danger-50 text-danger-700 dark:bg-danger-700/20 dark:text-danger-500',
        info: 'bg-info-50 text-info-700 dark:bg-info-700/20 dark:text-info-500',
        gold: 'bg-gold-100 text-gold-700 dark:bg-gold-400/15 dark:text-gold-400',
      },
    },
    defaultVariants: { tone: 'neutral' },
  },
);

export interface BadgeProps extends VariantProps<typeof badgeVariants> {
  className?: string;
  dot?: boolean;
  children: ReactNode;
}

export function Badge({ tone, dot, className, children }: BadgeProps) {
  return (
    <span className={cn(badgeVariants({ tone }), className)}>
      {dot && <span className="size-1.5 rounded-full bg-current" />}
      {children}
    </span>
  );
}
