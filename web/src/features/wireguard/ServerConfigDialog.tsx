import { useEffect, useState } from 'react';
import { Download } from 'lucide-react';
import { Button } from '@/components/ui/button';
import { Dialog } from '@/components/ui/dialog';
import { CodeBlock } from '@/components/ui/code-block';
import { apiFetchBlob } from '@/lib/api/client';
import { downloadText } from './download';
import type { InstanceDetail } from './types';

/**
 * Renders the full server (instance) config — `[Interface]` + a `[Peer]` block per active device —
 * for deploying on the actual WireGuard server. Fetched with the bearer token (reveals the server
 * private key; audited). No QR (server configs are deployed by file, not scanned).
 */
export function ServerConfigDialog({ instance, onClose }: { instance: InstanceDetail; onClose: () => void }) {
  const [config, setConfig] = useState<string | null>(null);
  const [error, setError] = useState<string | null>(null);

  useEffect(() => {
    let cancelled = false;
    (async () => {
      try {
        const blob = await apiFetchBlob(`/api/v1/wireguard/instances/${instance.id}/config`);
        if (!cancelled) setConfig(await blob.text());
      } catch {
        if (!cancelled) setError('Could not load the server config.');
      }
    })();
    return () => {
      cancelled = true;
    };
  }, [instance.id]);

  return (
    <Dialog
      open
      onClose={onClose}
      title="Server config"
      description="Deploy this on the WireGuard server itself: the [Interface] plus one [Peer] block per active device. Reveals the server private key — audited."
      footer={<Button onClick={onClose}>Close</Button>}
    >
      {error ? (
        <p className="text-sm text-danger-600 dark:text-danger-500">{error}</p>
      ) : !config ? (
        <div className="h-44 animate-pulse rounded bg-ink-100 dark:bg-ink-800" />
      ) : (
        <div className="space-y-3">
          <Button variant="secondary" size="sm" onClick={() => downloadText(`${instance.slug}.conf`, config)}>
            <Download /> Download {instance.slug}.conf
          </Button>
          <CodeBlock content={config} />
        </div>
      )}
    </Dialog>
  );
}
