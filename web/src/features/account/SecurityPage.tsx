import { useState, type FormEvent } from 'react';
import { Laptop, ShieldCheck } from 'lucide-react';
import { PageHeader } from '@/components/layout/AppShell';
import { Card, CardContent, CardHeader, CardTitle } from '@/components/ui/card';
import { Button } from '@/components/ui/button';
import { Input, Field } from '@/components/ui/input';
import { Badge } from '@/components/ui/badge';
import { noFormErrors, toFormErrors, type FormErrors } from '@/lib/api/form-errors';
import { useAuth } from '@/features/auth/use-auth';
import {
  accountApi,
  useRevokeAllSessions,
  useRevokeSession,
  useSessions,
  type EnrollMfaResponse,
} from './api';

export function SecurityPage() {
  return (
    <>
      <PageHeader title="Security" subtitle="Protect your account with MFA and manage active sessions." />
      <div className="space-y-6">
        <MfaCard />
        <ChangePasswordCard />
        <SessionsCard />
      </div>
    </>
  );
}

function MfaCard() {
  const { user, refresh } = useAuth();
  const [enrollment, setEnrollment] = useState<EnrollMfaResponse | null>(null);
  const [code, setCode] = useState('');
  const [recoveryCodes, setRecoveryCodes] = useState<string[] | null>(null);
  const [password, setPassword] = useState('');
  const [errors, setErrors] = useState<FormErrors>(noFormErrors);
  const [busy, setBusy] = useState(false);

  async function startEnroll() {
    setErrors(noFormErrors);
    setBusy(true);
    try {
      setEnrollment(await accountApi.enrollMfa());
    } catch (err) {
      setErrors(toFormErrors(err, 'Could not start enrolment.'));
    } finally {
      setBusy(false);
    }
  }

  async function confirm(e: FormEvent) {
    e.preventDefault();
    setErrors(noFormErrors);
    setBusy(true);
    try {
      const res = await accountApi.confirmMfa(code.trim());
      setRecoveryCodes(res.recoveryCodes);
      setEnrollment(null);
      setCode('');
      await refresh();
    } catch (err) {
      setErrors(toFormErrors(err, 'That code was not accepted.'));
    } finally {
      setBusy(false);
    }
  }

  async function disable() {
    setErrors(noFormErrors);
    setBusy(true);
    try {
      await accountApi.disableMfa(password);
      setPassword('');
      await refresh();
    } catch (err) {
      setErrors(toFormErrors(err, 'Could not disable MFA.'));
    } finally {
      setBusy(false);
    }
  }

  return (
    <Card>
      <CardHeader>
        <CardTitle className="flex items-center gap-2">
          <ShieldCheck className="size-5 text-gold-500" /> Two-factor authentication
        </CardTitle>
        {user?.mfaEnabled ? <Badge tone="success" dot>Enabled</Badge> : <Badge tone="neutral" dot>Disabled</Badge>}
      </CardHeader>
      <CardContent className="space-y-4">
        {errors.general && <p className="text-sm text-danger-500">{errors.general}</p>}

        {recoveryCodes && (
          <div className="rounded-lg border border-gold-400/30 bg-gold-400/5 p-4">
            <p className="text-sm font-medium text-ink-100">Save your recovery codes</p>
            <p className="mb-3 text-sm text-ink-400">Each can be used once if you lose your device. They won’t be shown again.</p>
            <div className="grid grid-cols-2 gap-2 font-mono text-sm">
              {recoveryCodes.map((c) => (
                <span key={c} className="rounded bg-ink-950 px-2 py-1 text-ink-200">{c}</span>
              ))}
            </div>
            <Button variant="secondary" size="sm" className="mt-3" onClick={() => setRecoveryCodes(null)}>Done</Button>
          </div>
        )}

        {!user?.mfaEnabled && !enrollment && !recoveryCodes && (
          <div className="flex items-center justify-between">
            <p className="text-sm text-ink-500">Add a one-time code from an authenticator app at sign-in.</p>
            <Button onClick={startEnroll} disabled={busy}>{busy ? 'Starting…' : 'Enable MFA'}</Button>
          </div>
        )}

        {enrollment && (
          <form onSubmit={confirm} className="space-y-4">
            <div className="flex flex-col items-center gap-3 sm:flex-row sm:items-start">
              <img
                alt="Scan with your authenticator app"
                className="size-40 rounded-lg border bg-white p-2 dark:border-ink-700"
                src={`data:image/png;base64,${enrollment.qrCodePngBase64}`}
              />
              <div className="space-y-2 text-sm">
                <p className="text-ink-400">Scan the QR code, or enter this secret manually:</p>
                <code className="block break-all rounded bg-ink-950 px-2 py-1 font-mono text-ink-200">{enrollment.secret}</code>
                <p className="text-ink-400">Then enter the 6-digit code to confirm.</p>
              </div>
            </div>
            <Field label="Code" htmlFor="mfa-code" error={errors.fields.code}>
              <Input id="mfa-code" className="max-w-40 font-mono tracking-widest" placeholder="123456" value={code} onChange={(e) => setCode(e.target.value)} />
            </Field>
            <div className="flex gap-2">
              <Button type="submit" disabled={busy}>{busy ? 'Confirming…' : 'Confirm & enable'}</Button>
              <Button type="button" variant="ghost" onClick={() => setEnrollment(null)}>Cancel</Button>
            </div>
          </form>
        )}

        {user?.mfaEnabled && (
          <div className="flex flex-wrap items-end gap-2">
            <Field label="Confirm password to disable" htmlFor="disable-pw" error={errors.fields.password}>
              <Input id="disable-pw" type="password" className="max-w-xs" value={password} onChange={(e) => setPassword(e.target.value)} />
            </Field>
            <Button variant="destructive" onClick={disable} disabled={busy || !password}>Disable MFA</Button>
          </div>
        )}
      </CardContent>
    </Card>
  );
}

function ChangePasswordCard() {
  const [current, setCurrent] = useState('');
  const [next, setNext] = useState('');
  const [errors, setErrors] = useState<FormErrors>(noFormErrors);
  const [done, setDone] = useState(false);
  const [busy, setBusy] = useState(false);

  async function submit(e: FormEvent) {
    e.preventDefault();
    setErrors(noFormErrors);
    setDone(false);
    setBusy(true);
    try {
      await accountApi.changePassword(current, next);
      setCurrent('');
      setNext('');
      setDone(true);
    } catch (err) {
      setErrors(toFormErrors(err, 'Could not change password.'));
    } finally {
      setBusy(false);
    }
  }

  return (
    <Card>
      <CardHeader>
        <CardTitle>Password</CardTitle>
      </CardHeader>
      <CardContent>
        <form onSubmit={submit} className="max-w-sm space-y-4">
          <Field label="Current password" htmlFor="cur-pw" error={errors.fields.currentPassword}>
            <Input id="cur-pw" type="password" autoComplete="current-password" required value={current} onChange={(e) => setCurrent(e.target.value)} />
          </Field>
          <Field label="New password" htmlFor="new-pw" error={errors.fields.newPassword}>
            <Input id="new-pw" type="password" autoComplete="new-password" required value={next} onChange={(e) => setNext(e.target.value)} />
          </Field>
          {errors.general && <p className="text-sm text-danger-500">{errors.general}</p>}
          {done && <p className="text-sm text-success-600">Password updated. Other sessions were signed out.</p>}
          <Button type="submit" disabled={busy}>{busy ? 'Updating…' : 'Update password'}</Button>
        </form>
      </CardContent>
    </Card>
  );
}

function SessionsCard() {
  const { data: sessions, isLoading } = useSessions();
  const revoke = useRevokeSession();
  const revokeAll = useRevokeAllSessions();

  return (
    <Card>
      <CardHeader>
        <CardTitle>Active sessions</CardTitle>
        <Button variant="secondary" size="sm" onClick={() => revokeAll.mutate()} disabled={revokeAll.isPending}>
          Log out everywhere
        </Button>
      </CardHeader>
      <CardContent className="space-y-2">
        {isLoading && <p className="text-sm text-ink-400">Loading…</p>}
        {sessions?.map((s) => (
          <div key={s.id} className="flex items-center justify-between rounded-lg border px-4 py-3 dark:border-ink-800">
            <div className="flex items-center gap-3">
              <Laptop className="size-4 text-ink-400" />
              <div className="text-sm">
                <div className="flex items-center gap-2 text-ink-800 dark:text-ink-100">
                  {s.userAgent ? shorten(s.userAgent) : 'Unknown device'}
                  {s.isCurrent && <Badge tone="gold">This device</Badge>}
                </div>
                <div className="text-ink-400">
                  {s.ipAddress ?? 'unknown IP'} · last seen {new Date(s.lastSeenAtUtc).toLocaleString()}
                </div>
              </div>
            </div>
            {!s.isCurrent && (
              <Button variant="ghost" size="sm" onClick={() => revoke.mutate(s.id)} disabled={revoke.isPending}>
                Revoke
              </Button>
            )}
          </div>
        ))}
      </CardContent>
    </Card>
  );
}

function shorten(ua: string): string {
  // A friendlier label than the raw user-agent.
  const match = ua.match(/(Firefox|Edg|Chrome|Safari)\/[\d.]+/);
  return match ? match[0].replace('Edg', 'Edge') : ua.slice(0, 40);
}
