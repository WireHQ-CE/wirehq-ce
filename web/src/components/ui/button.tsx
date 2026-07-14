import { forwardRef, type ButtonHTMLAttributes } from 'react';
import { cva, type VariantProps } from 'class-variance-authority';
import { cn } from '@/lib/utils/cn';

const buttonVariants = cva(
  'inline-flex items-center justify-center gap-2 whitespace-nowrap rounded-md font-medium transition-colors duration-[120ms] ease-standard disabled:pointer-events-none disabled:opacity-50 [&_svg]:size-4 [&_svg]:shrink-0',
  {
    variants: {
      variant: {
        // Primary = gold fill, ink text — the accessible, premium pairing from the brand doc.
        primary: 'bg-gold-500 text-ink-950 hover:bg-gold-600 active:bg-gold-700',
        secondary:
          'border border-ink-200 bg-ink-0 text-ink-800 hover:bg-ink-50 dark:border-ink-700 dark:bg-ink-850 dark:text-ink-100 dark:hover:bg-ink-800',
        ghost: 'text-ink-700 hover:bg-ink-100 dark:text-ink-200 dark:hover:bg-ink-800',
        destructive: 'bg-danger-500 text-white hover:bg-danger-600',
        link: 'text-gold-600 underline-offset-4 hover:underline dark:text-gold-400',
      },
      size: {
        sm: 'h-8 px-3 text-sm',
        md: 'h-9 px-4 text-base',
        lg: 'h-10 px-5 text-base',
        icon: 'h-9 w-9',
      },
    },
    defaultVariants: { variant: 'primary', size: 'md' },
  },
);

export interface ButtonProps
  extends ButtonHTMLAttributes<HTMLButtonElement>,
    VariantProps<typeof buttonVariants> {}

export const Button = forwardRef<HTMLButtonElement, ButtonProps>(
  ({ className, variant, size, ...props }, ref) => (
    <button ref={ref} className={cn(buttonVariants({ variant, size }), className)} {...props} />
  ),
);
Button.displayName = 'Button';
