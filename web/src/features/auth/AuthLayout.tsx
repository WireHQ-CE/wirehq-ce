import type { ReactNode } from 'react';
import { Link } from 'react-router-dom';
import { Logo } from '@/components/brand/Logo';

/** Centered, premium auth surface used by login/register/reset. */
export function AuthLayout({
  title,
  subtitle,
  children,
  footer,
}: {
  title: string;
  subtitle: string;
  children: ReactNode;
  footer: ReactNode;
}) {
  return (
    // The auth surface is designed dark (fixed ink-950/900 + ink-50 text). Pin `.dark` so it — and the
    // logo variant — render correctly even when the visitor's stored theme is light (which would
    // otherwise strip `.dark` off <html>). Mirrors MarketingLayout.
    <div className="dark grid min-h-screen place-items-center bg-ink-950 px-4">
      <div className="w-full max-w-sm">
        <Link to="/" className="mb-8 flex justify-center">
          <Logo className="h-10" />
        </Link>
        <div className="rounded-xl border border-ink-800 bg-ink-900 p-7 shadow-e3">
          <h1 className="text-h2 text-ink-50">{title}</h1>
          <p className="mt-1 text-sm text-ink-400">{subtitle}</p>
          <div className="mt-6">{children}</div>
        </div>
        <div className="mt-5 text-center text-sm text-ink-400">{footer}</div>
      </div>
    </div>
  );
}
