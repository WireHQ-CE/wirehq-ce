import { useEffect, useState, type FormEvent } from 'react';
import { PageHeader } from '@/components/layout/AppShell';
import { Card, CardContent } from '@/components/ui/card';
import { Button } from '@/components/ui/button';
import { Input, Field } from '@/components/ui/input';
import { Toggle } from '@/components/ui/toggle';
import { useToast } from '@/components/ui/toast';
import { ApiError } from '@/lib/api/client';
import { EDITION } from '@/lib/edition';
import { useAuthStore } from '@/stores/auth-store';
import {
  notificationsApi,
  usePhoneStatus,
  useStartPhoneVerification,
  useConfirmPhoneVerification,
  useSetSmsConsent,
  type NotificationPreferences,
} from './api';

const ROWS: { key: keyof NotificationPreferences; label: string; hint: string }[] = [
  { key: 'securityAlerts', label: 'Security alerts', hint: 'New-device sign-ins, password/MFA changes, suspicious activity.' },
  { key: 'vpnStatusAlerts', label: 'VPN status alerts', hint: 'Instance/peer health, deployments and config drift.' },
  { key: 'productAnnouncements', label: 'Product announcements', hint: 'New features and product updates.' },
  { key: 'billingNotifications', label: 'Billing notifications', hint: 'Invoices, payments and plan changes.' },
  { key: 'serviceStatusAlerts', label: 'Service status alerts', hint: 'Incidents and scheduled maintenance on the WireHQ status page.' },
  { key: 'marketingEmails', label: 'Marketing emails', hint: 'Newsletters and occasional promotions.' },
];

export function NotificationsPage() {
  const toast = useToast();
  const [prefs, setPrefs] = useState<NotificationPreferences | null>(null);
  const [busy, setBusy] = useState(false);

  useEffect(() => {
    notificationsApi.get().then(setPrefs).catch(() => setPrefs(null));
  }, []);

  function set(key: keyof NotificationPreferences, value: boolean) {
    setPrefs((p) => (p ? { ...p, [key]: value } : p));
  }

  async function save() {
    if (!prefs) return;
    setBusy(true);
    try {
      await notificationsApi.update(prefs);
      toast('Notification preferences saved.');
    } catch (err) {
      toast(err instanceof ApiError ? err.message : 'Could not save preferences.', 'error');
    } finally {
      setBusy(false);
    }
  }

  return (
    <>
      <PageHeader title="Notifications" subtitle="Choose which emails and alerts WireHQ sends you." />
      <div className="max-w-xl space-y-6">
        <Card>
          <CardContent>
            {!prefs ? (
              <div className="h-48 animate-pulse rounded-lg bg-ink-100 dark:bg-ink-800" />
            ) : (
              <div className="space-y-5">
                {ROWS.map((row) => (
                  <div key={row.key} className="flex items-start justify-between gap-4">
                    <div>
                      <div className="text-sm font-medium text-ink-800 dark:text-ink-100">{row.label}</div>
                      <p className="mt-0.5 text-xs text-ink-500 dark:text-ink-400">{row.hint}</p>
                    </div>
                    <Toggle checked={prefs[row.key]} onChange={(v) => set(row.key, v)} aria-label={row.label} />
                  </div>
                ))}
                <div className="flex justify-end pt-1">
                  <Button onClick={save} disabled={busy}>{busy ? 'Saving…' : 'Save changes'}</Button>
                </div>
              </div>
            )}
          </CardContent>
        </Card>

        {prefs && (
          <SmsSection smsAlerts={prefs.smsAlerts} onConsentChange={(v) => set('smsAlerts', v)} />
        )}
      </div>
    </>
  );
}

/**
 * Account → SMS: verify a phone number, then opt in to critical SMS alerts (Notifications Wave 4 — docs/36 §10 Slice B).
 * Shown to every user; the verify/consent actions require the org to hold `notifications.sms`. The module is dark by a
 * server-side send-gate, so the code step shows a NEUTRAL, non-oracular message regardless of the 204.
 */
function SmsSection({
  smsAlerts,
  onConsentChange,
}: {
  smsAlerts: boolean;
  onConsentChange: (enabled: boolean) => void;
}) {
  const hasFeature = useAuthStore((s) => s.hasFeature);
  const canSms = hasFeature('notifications.sms');
  const toast = useToast();

  const { data: status } = usePhoneStatus(canSms);
  const start = useStartPhoneVerification();
  const confirm = useConfirmPhoneVerification();
  const setConsent = useSetSmsConsent();

  // 'idle' = show status / phone entry; 'code' = a code was requested, show the neutral message + code entry.
  const [step, setStep] = useState<'idle' | 'code'>('idle');
  const [phone, setPhone] = useState('');
  const [code, setCode] = useState('');
  const [codeError, setCodeError] = useState<string | null>(null);
  // Enabling consent is a two-tap flow: the toggle reveals the disclosure, which must be accepted before we PUT.
  const [confirmingConsent, setConfirmingConsent] = useState(false);

  const fail = (e: unknown, fallback: string) => toast(e instanceof ApiError ? e.message : fallback, 'error');

  const providerConfigured = status?.providerConfigured ?? false;

  const onStart = (e: FormEvent) => {
    e.preventDefault();
    start.mutate(phone.trim(), {
      // 204 on the happy AND every silent path — advance to the code step with a neutral message either way.
      onSuccess: () => { setStep('code'); setCode(''); setCodeError(null); },
      // 400 is a real input error (bad format / cooldown / provider not configured) — surface it, stay on this step.
      onError: (e) => fail(e, 'Could not start verification. Check the number and try again.'),
    });
  };

  const onConfirm = (e: FormEvent) => {
    e.preventDefault();
    setCodeError(null);
    confirm.mutate(code.trim(), {
      onSuccess: () => { setStep('idle'); setPhone(''); setCode(''); toast('Phone number verified.'); },
      // Generic failure — no oracle about which part was wrong.
      onError: () => setCodeError('That code didn’t work. Check it and try again.'),
    });
  };

  const onToggleConsent = (next: boolean) => {
    if (next) {
      // Reveal the disclosure; consent is not recorded until the user accepts it below.
      setConfirmingConsent(true);
      return;
    }
    setConsent.mutate(false, {
      onSuccess: () => { onConsentChange(false); toast('SMS alerts turned off.'); },
      onError: (e) => fail(e, 'Could not update SMS alerts.'),
    });
  };

  const acceptConsent = () => {
    setConsent.mutate(true, {
      onSuccess: () => { onConsentChange(true); setConfirmingConsent(false); toast('SMS alerts turned on.'); },
      onError: (e) => fail(e, 'Could not update SMS alerts.'),
    });
  };

  return (
    <Card>
      <CardContent>
        <h2 className="mb-1 text-sm font-semibold text-ink-800 dark:text-ink-100">SMS alerts</h2>
        <p className="mb-4 text-xs text-ink-500 dark:text-ink-400">
          Get critical security and status alerts by text message. Verify a mobile number, then opt in below.
        </p>

        {!canSms ? (
          <p className="rounded-md border border-ink-200 bg-ink-50 px-3 py-2 text-xs text-ink-500 dark:border-ink-800 dark:bg-ink-900/40 dark:text-ink-400">
            {EDITION === 'community'
              ? 'SMS alerts are available as a Marketplace module. Ask an organization owner to add the SMS Integration module.'
              : 'SMS alerts are available on the Pro and Enterprise plans.'}
          </p>
        ) : (
          <div className="space-y-5">
            {/* --- Phone verification --- */}
            <div>
              {status?.phoneVerified ? (
                <div className="flex flex-wrap items-center justify-between gap-2">
                  <div className="flex items-center gap-2">
                    <span className="rounded-full bg-success-500/15 px-2 py-0.5 text-xs font-medium text-success-700 dark:text-success-400">
                      Verified
                    </span>
                    <span className="font-mono text-sm text-ink-800 dark:text-ink-100">{status.phoneMasked}</span>
                  </div>
                  <Button variant="ghost" onClick={() => { setStep('idle'); setPhone(''); }}>
                    Use a different number
                  </Button>
                </div>
              ) : step === 'idle' ? (
                <form onSubmit={onStart} className="flex flex-col gap-3 sm:flex-row sm:items-end">
                  <Field label="Mobile number" htmlFor="sms-phone">
                    <Input
                      id="sms-phone"
                      type="tel"
                      value={phone}
                      onChange={(e) => setPhone(e.target.value)}
                      placeholder="+15551234567"
                      autoComplete="tel"
                    />
                  </Field>
                  <Button type="submit" disabled={start.isPending || !phone.trim() || !providerConfigured}>
                    Send code
                  </Button>
                </form>
              ) : (
                <form onSubmit={onConfirm} className="space-y-3">
                  <p className="text-xs text-ink-500 dark:text-ink-400">
                    If that number can receive texts, we’ve sent a 6-digit code. Enter it below to verify.
                  </p>
                  <div className="flex flex-col gap-3 sm:flex-row sm:items-end">
                    <Field label="Verification code" htmlFor="sms-code" error={codeError ?? undefined}>
                      <Input
                        id="sms-code"
                        inputMode="numeric"
                        autoComplete="one-time-code"
                        maxLength={6}
                        value={code}
                        onChange={(e) => setCode(e.target.value)}
                        placeholder="123456"
                      />
                    </Field>
                    <Button type="submit" disabled={confirm.isPending || code.trim().length === 0}>Verify</Button>
                    <Button type="button" variant="ghost" onClick={() => { setStep('idle'); setCode(''); setCodeError(null); }}>
                      Cancel
                    </Button>
                  </div>
                </form>
              )}
              {step === 'idle' && !status?.phoneVerified && !providerConfigured && (
                <p className="mt-2 text-xs text-ink-500 dark:text-ink-400">
                  Your organization hasn’t set up an SMS provider yet. An admin can add one under Notifications settings.
                </p>
              )}
            </div>

            {/* --- Consent --- */}
            <div className="border-t border-ink-100 pt-4 dark:border-ink-800">
              <div className="flex items-start justify-between gap-4">
                <div>
                  <div className="text-sm font-medium text-ink-800 dark:text-ink-100">Send me SMS alerts</div>
                  <p className="mt-0.5 text-xs text-ink-500 dark:text-ink-400">Critical alerts only, for your current organization. Reply STOP at any time to opt out.</p>
                </div>
                <Toggle
                  checked={smsAlerts}
                  onChange={onToggleConsent}
                  disabled={setConsent.isPending || confirmingConsent}
                  aria-label="Send me SMS alerts"
                />
              </div>

              {confirmingConsent && status && (
                <div className="mt-3 rounded-md border border-ink-200 bg-ink-50 p-3 dark:border-ink-800 dark:bg-ink-900/40">
                  <p className="text-xs text-ink-600 dark:text-ink-300">{status.disclosureText}</p>
                  <p className="mt-1 text-xs text-ink-500 dark:text-ink-400">Msg &amp; data rates may apply.</p>
                  <div className="mt-3 flex justify-end gap-2">
                    <Button variant="ghost" onClick={() => setConfirmingConsent(false)}>Cancel</Button>
                    <Button onClick={acceptConsent} disabled={setConsent.isPending}>Agree &amp; turn on</Button>
                  </div>
                </div>
              )}
            </div>
          </div>
        )}
      </CardContent>
    </Card>
  );
}
