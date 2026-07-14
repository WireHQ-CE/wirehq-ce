import { useEffect, useState } from 'react';
import { PageHeader } from '@/components/layout/AppShell';
import { Card, CardContent } from '@/components/ui/card';
import { Button } from '@/components/ui/button';
import { Toggle } from '@/components/ui/toggle';
import { useToast } from '@/components/ui/toast';
import { ApiError } from '@/lib/api/client';
import { notificationsApi, type NotificationPreferences } from './api';

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
      <Card className="max-w-xl">
        <CardContent>
          {!prefs ? (
            <div className="h-48 animate-pulse rounded-lg bg-ink-100 dark:bg-ink-800" />
          ) : (
            <div className="space-y-5">
              {ROWS.map((row) => (
                <div key={row.key} className="flex items-start justify-between gap-4">
                  <div>
                    <div className="text-sm font-medium text-ink-800 dark:text-ink-100">{row.label}</div>
                    <p className="mt-0.5 text-xs text-ink-400">{row.hint}</p>
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
    </>
  );
}
