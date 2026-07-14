import { useState } from 'react';
import { ArrowUpCircle, ShieldAlert, X } from 'lucide-react';
import { cn } from '@/lib/utils/cn';
import { useUpdateStatus, isUpdateAvailable, isLoudUpdate } from './api';
import { UpdateModal } from './UpdateModal';

const DISMISS_KEY = 'wirehq-update-dismissed';

/**
 * The operator's in-app "a newer WireHQ version is available" bar (docs/30 U-8). Loud + non-dismissible for a
 * security release or an unsupported version; subtle + dismissible (per target version) for a routine one. Renders
 * nothing unless the CE poller reports an update — so it is inert in SaaS and for non-operators (the hook is gated).
 */
export function UpdateBanner() {
  const { data } = useUpdateStatus();
  const [open, setOpen] = useState(false);
  const [dismissed, setDismissed] = useState<string | null>(() => {
    try {
      return localStorage.getItem(DISMISS_KEY);
    } catch {
      return null;
    }
  });

  if (!isUpdateAvailable(data) || !data.latestVersion) {
    return null;
  }

  const loud = isLoudUpdate(data);
  if (!loud && dismissed === data.latestVersion) {
    return null;
  }

  const latest = data.latestVersion;
  function dismiss() {
    try {
      localStorage.setItem(DISMISS_KEY, latest);
    } catch {
      /* storage blocked — fall back to a session-only dismiss */
    }
    setDismissed(latest);
  }

  const headline = loud
    ? data.security
      ? 'A security update for WireHQ is available'
      : 'This WireHQ version is no longer supported'
    : 'A new version of WireHQ is available';

  return (
    <>
      <div
        className={cn(
          'flex items-center justify-center gap-3 px-4 py-1.5 text-sm',
          loud
            ? 'bg-danger-500/15 font-medium text-danger-700 dark:text-danger-300'
            : 'bg-gold-400/15 text-gold-800 dark:text-gold-300',
        )}
      >
        {loud ? <ShieldAlert className="size-4" /> : <ArrowUpCircle className="size-4" />}
        <span>
          {headline} — <strong>v{latest}</strong>.
          {loud && <span className="opacity-80"> This stays until the server is upgraded.</span>}
        </span>
        <button
          onClick={() => setOpen(true)}
          className={cn(
            'ml-1 inline-flex items-center gap-1 rounded px-2 py-0.5 text-xs font-semibold',
            loud ? 'bg-danger-500/20 hover:bg-danger-500/30' : 'bg-gold-500/20 hover:bg-gold-500/30',
          )}
        >
          How to update
        </button>
        {!loud && (
          <button onClick={dismiss} aria-label="Dismiss" className="rounded p-0.5 text-gold-700/70 hover:bg-gold-500/20 dark:text-gold-300/70">
            <X className="size-3.5" />
          </button>
        )}
      </div>
      {open && <UpdateModal status={data} onClose={() => setOpen(false)} />}
    </>
  );
}
