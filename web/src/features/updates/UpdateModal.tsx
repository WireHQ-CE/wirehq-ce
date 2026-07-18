import { useState } from 'react';
import { AlertTriangle, Copy, Check, ExternalLink, ShieldAlert } from 'lucide-react';
import { Dialog } from '@/components/ui/dialog';
import { Badge } from '@/components/ui/badge';
import { Button } from '@/components/ui/button';
import { EDITION } from '@/lib/edition';
import type { UpdateStatus } from './api';

function timeAgo(iso: string | null): string | null {
  if (!iso) return null;
  const mins = Math.max(0, Math.round((Date.now() - new Date(iso).getTime()) / 60000));
  if (mins < 60) return `${mins} min ago`;
  const hrs = Math.round(mins / 60);
  return hrs < 48 ? `${hrs} h ago` : `${Math.round(hrs / 24)} days ago`;
}

/**
 * Explains an available update and how to apply it (docs/30 U-8). The command is FIXED here (the real upgrade
 * path — backup → migrate → roll), never taken from the manifest; the release link is constructed; the manifest's
 * summary is shown as an untrusted, visually-distinct vendor message. It never self-updates.
 */
export function UpdateModal({ status, onClose }: { status: UpdateStatus; onClose: () => void }) {
  const [copied, setCopied] = useState(false);
  // The upgrade command differs by edition (this modal only ever renders on the CE today, but stay honest for both):
  // the self-hosted CE builds from its clone (`deploy/deploy.sh` + docker-compose.prod.yml are stripped from it, and
  // it has no GHCR images to pull), while the SaaS/VPS operator rolls prebuilt images. Kept in sync with the CE
  // README's "Updating" section. (docs/30 U-8 / docs/17)
  const isCommunity = EDITION === 'community';
  const command = isCommunity
    ? 'git pull && docker compose -f deploy/docker-compose.yml up -d --build'
    : `./deploy/deploy.sh ${status.latestVersion}`;
  const webVersion = typeof __APP_VERSION__ === 'string' ? __APP_VERSION__ : null;
  const partialUpgrade = webVersion !== null && webVersion !== status.currentVersion;
  const checked = timeAgo(status.checkedAtUtc);

  async function copy() {
    try {
      await navigator.clipboard.writeText(command);
      setCopied(true);
      setTimeout(() => setCopied(false), 1500);
    } catch {
      /* clipboard blocked — the operator can select the text */
    }
  }

  return (
    <Dialog open onClose={onClose} title={status.security ? 'Security update available' : 'Update available'}>
      <div className="space-y-4 text-sm">
        <p className="text-ink-700 dark:text-ink-200">
          <span className="font-semibold">WireHQ v{status.latestVersion}</span> is available. This server is running
          v{status.currentVersion}.
        </p>

        <div className="flex flex-wrap items-center gap-2">
          {status.security && (
            <Badge tone="danger" dot>
              <ShieldAlert className="size-3.5" /> Security update{status.severity !== 'None' ? ` · ${status.severity}` : ''}
            </Badge>
          )}
          {status.unsupported && <Badge tone="warning">Your version is no longer supported</Badge>}
        </div>

        {status.summary && (
          <div className="rounded-md border border-ink-200 bg-ink-50 p-3 text-xs text-ink-600 dark:border-ink-700 dark:bg-ink-900 dark:text-ink-300">
            <span className="font-medium text-ink-500">Release note:</span> {status.summary}
          </div>
        )}

        {status.requiresMigration && (
          <div className="flex items-start gap-2 rounded-md border border-warning-500/30 bg-warning-50 p-3 text-warning-700 dark:bg-warning-700/10 dark:text-warning-400">
            <AlertTriangle className="mt-0.5 size-4 shrink-0" />
            <p>This release includes a <span className="font-medium">database migration</span> — back up your data before upgrading.</p>
          </div>
        )}

        <div>
          <p className="mb-1 text-ink-500">
            {isCommunity ? 'From the folder where WireHQ is installed, run:' : 'Run this from the host with shell access:'}
          </p>
          <div className="flex items-center justify-between gap-2 rounded-md bg-ink-900 px-3 py-2 font-mono text-xs text-ink-50">
            <code className="select-all">{command}</code>
            <button onClick={copy} className="inline-flex items-center gap-1 rounded px-1.5 py-0.5 text-ink-300 hover:bg-ink-700" aria-label="Copy command">
              {copied ? <Check className="size-3.5" /> : <Copy className="size-3.5" />}
            </button>
          </div>
          <p className="mt-1 text-xs text-ink-400">
            {isCommunity
              ? 'This rebuilds from the new source and applies any migrations via a one-shot job, preserving your data — WireHQ does not update itself.'
              : 'This backs up, runs migrations, and rolls the images — WireHQ does not update itself.'}
          </p>
        </div>

        {partialUpgrade && (
          <p className="text-xs text-warning-700 dark:text-warning-400">
            Your web ({webVersion}) and API ({status.currentVersion}) images are out of step — a previous upgrade may
            not have completed. Re-run the command above.
          </p>
        )}

        <div className="flex items-center justify-between pt-1">
          {status.releaseUrl && (
            <a href={status.releaseUrl} target="_blank" rel="noreferrer" className="inline-flex items-center gap-1 text-gold-600 hover:underline dark:text-gold-400">
              Release notes <ExternalLink className="size-3.5" />
            </a>
          )}
          {checked && <span className="text-xs text-ink-400">Last checked {checked}</span>}
        </div>

        <div className="flex justify-end pt-2">
          <Button variant="secondary" onClick={onClose}>Close</Button>
        </div>
      </div>
    </Dialog>
  );
}
