import type { Config } from 'tailwindcss';

/**
 * The single source of truth for the WireHQ brand system (see docs/01-brand-system.md).
 * Exact HEX values from the brand doc are mapped 1:1 here. Components consume these tokens —
 * never raw hex — so the brand evolves in one place.
 */
export default {
  darkMode: 'class',
  content: ['./index.html', './src/**/*.{ts,tsx}'],
  theme: {
    extend: {
      colors: {
        // Brand — "Wire Gold". Reserved for primary action, focus, identity, the logo hub.
        gold: {
          50: '#FFFBEB',
          100: '#FEF3C7',
          200: '#FDE68A',
          300: '#FCD34D',
          400: '#FFC23C',
          500: '#F5B301',
          600: '#D98E00',
          700: '#B36F02',
          800: '#8F5708',
          900: '#75470C',
          950: '#452703',
        },
        // Neutrals — "Ink" (cool-neutral). Backgrounds, surfaces, text, borders.
        ink: {
          0: '#FFFFFF',
          50: '#F7F8FA',
          100: '#EFF1F4',
          200: '#E2E5EA',
          300: '#C7CCD4',
          400: '#9AA1AD',
          500: '#6B7280',
          600: '#4B5563',
          700: '#374151',
          800: '#222730',
          850: '#191D24',
          900: '#12151B',
          950: '#0A0B0D',
        },
        success: { 50: '#ECFDF5', 500: '#10B981', 600: '#059669', 700: '#047857' },
        warning: { 50: '#FFF7ED', 500: '#F97316', 600: '#EA580C', 700: '#C2410C' },
        danger: { 50: '#FEF2F2', 500: '#EF4444', 600: '#DC2626', 700: '#B91C1C' },
        info: { 50: '#EFF6FF', 500: '#3B82F6', 600: '#2563EB', 700: '#1D4ED8' },
      },
      fontFamily: {
        sans: ['Inter', 'ui-sans-serif', 'system-ui', '-apple-system', 'Segoe UI', 'Roboto', 'sans-serif'],
        mono: ['"JetBrains Mono"', 'ui-monospace', 'SFMono-Regular', 'Menlo', 'monospace'],
      },
      fontSize: {
        xs: ['0.75rem', { lineHeight: '1.4', letterSpacing: '0.01em' }],
        sm: ['0.8125rem', { lineHeight: '1.5' }],
        base: ['0.9375rem', { lineHeight: '1.55' }],
        'body-lg': ['1.0625rem', { lineHeight: '1.6' }],
        h3: ['1.25rem', { lineHeight: '1.3', letterSpacing: '-0.005em', fontWeight: '600' }],
        h2: ['1.5rem', { lineHeight: '1.25', letterSpacing: '-0.01em', fontWeight: '600' }],
        h1: ['1.875rem', { lineHeight: '1.2', letterSpacing: '-0.015em', fontWeight: '650' }],
        'display-lg': ['2.75rem', { lineHeight: '1.1', letterSpacing: '-0.02em', fontWeight: '700' }],
        'display-xl': ['3.5rem', { lineHeight: '1.05', letterSpacing: '-0.02em', fontWeight: '700' }],
      },
      borderRadius: {
        sm: '6px',
        DEFAULT: '8px',
        md: '8px',
        lg: '12px',
        xl: '16px',
      },
      boxShadow: {
        e1: '0 1px 2px rgba(16,21,27,.06), 0 1px 1px rgba(16,21,27,.04)',
        e2: '0 4px 12px rgba(16,21,27,.08), 0 2px 4px rgba(16,21,27,.05)',
        e3: '0 16px 48px rgba(16,21,27,.16)',
      },
      transitionTimingFunction: {
        standard: 'cubic-bezier(.2,.8,.2,1)',
        exit: 'cubic-bezier(.4,0,.2,1)',
      },
    },
  },
  plugins: [],
} satisfies Config;
