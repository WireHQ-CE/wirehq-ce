import { useState } from 'react';
import { ArrowUpCircle } from 'lucide-react';
import { cn } from '@/lib/utils/cn';
import { useUpdateStatus, isUpdateAvailable, isLoudUpdate } from './api';
import { UpdateModal } from './UpdateModal';

/**
 * A Topbar affordance (docs/30 U-8): a small dot on an icon whenever a newer version is available — red for a
 * security/unsupported release, gold for routine — opening the same update modal. Renders nothing otherwise, so
 * it is inert in SaaS and for non-operators (the hook is gated).
 */
export function UpdateIndicator() {
  const { data } = useUpdateStatus();
  const [open, setOpen] = useState(false);

  if (!isUpdateAvailable(data)) {
    return null;
  }

  const loud = isLoudUpdate(data);

  return (
    <>
      <button
        onClick={() => setOpen(true)}
        aria-label="Update available"
        title={`WireHQ v${data.latestVersion} is available`}
        className="relative inline-flex size-9 items-center justify-center rounded-md text-ink-500 hover:bg-ink-100 dark:text-ink-400 dark:hover:bg-ink-800"
      >
        <ArrowUpCircle className="size-5" />
        <span className={cn('absolute right-1.5 top-1.5 size-2 rounded-full ring-2 ring-ink-0 dark:ring-ink-950', loud ? 'bg-danger-500' : 'bg-gold-500')} />
      </button>
      {open && <UpdateModal status={data} onClose={() => setOpen(false)} />}
    </>
  );
}
