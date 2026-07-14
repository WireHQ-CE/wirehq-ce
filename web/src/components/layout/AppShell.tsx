import { useState, type ReactNode } from 'react';
import { Outlet, useNavigate } from 'react-router-dom';
import { CreditCard, Eye, LogOut, MailWarning } from 'lucide-react';
import { Sidebar } from './Sidebar';
import { Topbar } from './Topbar';
import { UpdateBanner } from '@/features/updates/UpdateBanner';
import { useAuthStore } from '@/stores/auth-store';
import { useAuth } from '@/features/auth/use-auth';
import { authApi } from '@/features/auth/api';
import { api, ApiError } from '@/lib/api/client';
import { useToast } from '@/components/ui/toast';

/** The persistent shell: it mounts once; navigation swaps only the Outlet, so it feels instant. */
export function AppShell() {
  return (
    <div className="flex h-screen overflow-hidden bg-ink-50 dark:bg-ink-950">
      <Sidebar />
      <div className="flex flex-1 flex-col overflow-hidden">
        <ImpersonationBanner />
        <PastDueBanner />
        <VerifyEmailBanner />
        <UpdateBanner />
        <Topbar />
        <main className="flex-1 overflow-y-auto">
          <div className="mx-auto w-full max-w-[1280px] px-6 py-6">
            <Outlet />
          </div>
        </main>
      </div>
    </div>
  );
}

/** Sticky reminder that the Super Admin is acting as a customer, with a one-click exit. */
function ImpersonationBanner() {
  const navigate = useNavigate();
  const { stopImpersonating } = useAuth();
  const user = useAuthStore((s) => s.user);
  const [exiting, setExiting] = useState(false);

  if (!user?.impersonatedBy) {
    return null;
  }

  const customer = user.organizations.find((o) => o.organizationId === user.activeOrganizationId);

  async function exit() {
    setExiting(true);
    try {
      await stopImpersonating();
      navigate('/app/platform/customers');
    } finally {
      setExiting(false);
    }
  }

  return (
    <div className="flex items-center justify-center gap-3 bg-gold-500 px-4 py-1.5 text-sm font-medium text-ink-950">
      <Eye className="size-4" />
      <span>
        Viewing <strong>{customer?.name ?? 'customer'}</strong> as admin
        {user.impersonatedBy ? <span className="opacity-70"> · {user.impersonatedBy.email}</span> : null}
        {user.impersonatedBy?.expiresAtUtc ? (
          <span className="opacity-70"> · expires {new Date(user.impersonatedBy.expiresAtUtc).toLocaleTimeString([], { hour: '2-digit', minute: '2-digit' })}</span>
        ) : null}
      </span>
      <button
        onClick={exit}
        disabled={exiting}
        className="ml-1 inline-flex items-center gap-1 rounded bg-ink-950/10 px-2 py-0.5 text-xs font-semibold hover:bg-ink-950/20 disabled:opacity-60"
      >
        <LogOut className="size-3" /> {exiting ? 'Exiting…' : 'Exit'}
      </button>
    </div>
  );
}

/** A red alert when the org's subscription payment has failed — admins can jump to the billing portal. */
function PastDueBanner() {
  const user = useAuthStore((s) => s.user);
  const canManage = useAuthStore((s) => s.hasPermission('org.settings.update'));
  const toast = useToast();
  const [opening, setOpening] = useState(false);

  if (user?.billing?.status !== 'PastDue') {
    return null;
  }

  async function manage() {
    setOpening(true);
    try {
      const { url } = await api.post<{ url: string }>('/api/v1/billing/portal');
      window.location.href = url;
    } catch (err) {
      toast(err instanceof ApiError ? err.message : 'Could not open billing.', 'error');
      setOpening(false);
    }
  }

  return (
    <div className="flex items-center justify-center gap-3 bg-danger-500/15 px-4 py-1.5 text-sm text-danger-700 dark:text-danger-300">
      <CreditCard className="size-4" />
      <span>Your last payment failed — update your billing to keep Pro.</span>
      {canManage && (
        <button
          onClick={manage}
          disabled={opening}
          className="ml-1 inline-flex items-center gap-1 rounded bg-danger-500/20 px-2 py-0.5 text-xs font-semibold hover:bg-danger-500/30 disabled:opacity-60"
        >
          {opening ? 'Opening…' : 'Manage billing'}
        </button>
      )}
    </div>
  );
}

/** A gentle nudge for users who haven't confirmed their email yet (login isn't blocked). */
function VerifyEmailBanner() {
  const toast = useToast();
  const user = useAuthStore((s) => s.user);
  const [sending, setSending] = useState(false);

  // Hide while impersonating (the operator isn't the unverified party) and for verified users.
  if (!user || user.emailVerified || user.impersonatedBy) {
    return null;
  }

  async function resend() {
    setSending(true);
    try {
      await authApi.resendVerification();
      toast('Verification email sent — check your inbox.');
    } catch (err) {
      toast(err instanceof ApiError ? err.message : 'Could not send the email.', 'error');
    } finally {
      setSending(false);
    }
  }

  return (
    <div className="flex items-center justify-center gap-3 bg-gold-400/15 px-4 py-1.5 text-sm text-gold-700 dark:text-gold-300">
      <MailWarning className="size-4" />
      <span>Please confirm your email address to secure your account.</span>
      <button
        onClick={resend}
        disabled={sending}
        className="ml-1 rounded bg-gold-500/20 px-2 py-0.5 text-xs font-semibold hover:bg-gold-500/30 disabled:opacity-60"
      >
        {sending ? 'Sending…' : 'Resend email'}
      </button>
    </div>
  );
}

/** Standard page header: title + optional subtitle on the left, one primary action on the right. */
export function PageHeader({
  title,
  subtitle,
  action,
}: {
  title: string;
  subtitle?: string;
  action?: ReactNode;
}) {
  return (
    <div className="mb-6 flex items-start justify-between gap-4">
      <div>
        <h1 className="text-h1 text-ink-900 dark:text-ink-50">{title}</h1>
        {subtitle && <p className="mt-1 text-base text-ink-500">{subtitle}</p>}
      </div>
      {action}
    </div>
  );
}
