import { useEffect, useState } from 'react';
import { Download } from 'lucide-react';
import { Button } from '@/components/ui/button';
import { Badge } from '@/components/ui/badge';
import { Dialog } from '@/components/ui/dialog';
import { CodeBlock } from '@/components/ui/code-block';
import { apiFetchBlob } from '@/lib/api/client';
import { usePeerConfigVersions } from './api';
import { confFilename, downloadText } from './download';
import type { PeerListItem } from './types';

/** Shows an existing peer's config + QR (fetched with the bearer token) and its version history. */
export function PeerConfigDialog({ peer, onClose }: { peer: PeerListItem; onClose: () => void }) {
  const [config, setConfig] = useState<string | null>(null);
  const [qrUrl, setQrUrl] = useState<string | null>(null);
  const [error, setError] = useState<string | null>(null);
  const versions = usePeerConfigVersions(peer.id, true);

  useEffect(() => {
    let cancelled = false;
    let objectUrl: string | null = null;
    (async () => {
      try {
        const [confBlob, qrBlob] = await Promise.all([
          apiFetchBlob(`/api/v1/wireguard/peers/${peer.id}/config`),
          apiFetchBlob(`/api/v1/wireguard/peers/${peer.id}/config/qr`),
        ]);
        if (cancelled) return;
        setConfig(await confBlob.text());
        objectUrl = URL.createObjectURL(qrBlob);
        setQrUrl(objectUrl);
      } catch {
        if (!cancelled) setError('Could not load this peer’s config.');
      }
    })();
    return () => {
      cancelled = true;
      if (objectUrl) URL.revokeObjectURL(objectUrl);
    };
  }, [peer.id]);

  return (
    <Dialog
      open
      onClose={onClose}
      title={peer.name}
      description={`Config for ${peer.assignedAddress}. Exporting reveals the private key and is audited.`}
      footer={<Button onClick={onClose}>Close</Button>}
    >
      {error ? (
        <p className="text-sm text-danger-600 dark:text-danger-500">{error}</p>
      ) : !config ? (
        <div className="h-44 animate-pulse rounded bg-ink-100 dark:bg-ink-800" />
      ) : (
        <div className="space-y-4">
          <div className="flex flex-col items-center gap-3 sm:flex-row sm:items-start">
            {qrUrl && (
              <img src={qrUrl} alt="WireGuard config QR code" className="size-44 shrink-0 rounded-md border bg-white p-1 dark:border-ink-700" />
            )}
            <div className="flex-1">
              <p className="text-sm text-ink-500">Scan with the WireGuard mobile app, or download the <code>.conf</code> for desktop.</p>
              <Button variant="secondary" size="sm" className="mt-2" onClick={() => downloadText(confFilename(peer.name), config)}>
                <Download /> Download .conf
              </Button>
            </div>
          </div>

          <CodeBlock content={config} />

          <div>
            <h4 className="mb-1.5 text-xs font-medium uppercase tracking-wide text-ink-500">Config history</h4>
            <div className="space-y-1">
              {versions.data?.length ? (
                versions.data.map((v) => (
                  <div key={v.version} className="flex items-center justify-between rounded-md border px-3 py-1.5 text-sm dark:border-ink-800">
                    <span className="flex items-center gap-2">
                      <Badge tone="neutral">v{v.version}</Badge>
                      <span className="text-ink-500">{v.note ?? '—'}</span>
                    </span>
                    <span className="font-mono text-xs text-ink-400" title={v.checksum}>{v.checksum.slice(0, 12)}…</span>
                  </div>
                ))
              ) : (
                <p className="text-sm text-ink-400">No version history.</p>
              )}
            </div>
          </div>
        </div>
      )}
    </Dialog>
  );
}
