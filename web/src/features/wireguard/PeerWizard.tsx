import { useState, type FormEvent } from 'react';
import { Download } from 'lucide-react';
import { Button } from '@/components/ui/button';
import { Input, Field } from '@/components/ui/input';
import { Dialog } from '@/components/ui/dialog';
import { CodeBlock } from '@/components/ui/code-block';
import { useToast } from '@/components/ui/toast';
import { noFormErrors, toFormErrors, type FormErrors } from '@/lib/api/form-errors';
import { useCreatePeer } from './api';
import { confFilename, downloadText } from './download';
import type { CreatePeerResponse } from './types';

/**
 * Peer Creation Wizard: a single form (with key/PSK/network options) that, on success, switches to a
 * result view showing the ready-to-use config + scannable QR — the private key is shown only once.
 */
export function PeerWizard({ instanceId, onClose }: { instanceId: string; onClose: () => void }) {
  const toast = useToast();
  const create = useCreatePeer(instanceId);
  const [result, setResult] = useState<{ res: CreatePeerResponse; name: string } | null>(null);
  const [errors, setErrors] = useState<FormErrors>(noFormErrors);
  const [form, setForm] = useState({
    name: '',
    deviceType: '',
    generateKeypair: true,
    publicKey: '',
    usePresharedKey: true,
    assignedAddress: '',
    allowedIps: '',
    persistentKeepalive: '',
  });

  function submit(e: FormEvent) {
    e.preventDefault();
    setErrors(noFormErrors);
    create.mutate(
      {
        name: form.name.trim(),
        deviceType: form.deviceType.trim() || undefined,
        generateKeypair: form.generateKeypair,
        publicKey: form.generateKeypair ? undefined : form.publicKey.trim(),
        usePresharedKey: form.usePresharedKey,
        assignedAddress: form.assignedAddress.trim() || undefined,
        allowedIps: form.allowedIps.trim()
          ? form.allowedIps.split(',').map((s) => s.trim()).filter(Boolean)
          : undefined,
        persistentKeepalive: form.persistentKeepalive.trim() ? Number(form.persistentKeepalive) : undefined,
      },
      {
        onSuccess: (res) => {
          setResult({ res, name: form.name.trim() });
          toast('Peer created.');
        },
        onError: (err) => setErrors(toFormErrors(err, 'Could not create the peer.')),
      },
    );
  }

  if (result) {
    return <PeerResult result={result.res} name={result.name} onClose={onClose} />;
  }

  return (
    <Dialog
      open
      onClose={onClose}
      title="Add peer"
      description="Create a client device on this instance. Its keypair is generated on the server unless you supply one."
      footer={
        <>
          <Button variant="secondary" onClick={onClose}>Cancel</Button>
          <Button type="submit" form="peer-wizard" disabled={create.isPending}>
            {create.isPending ? 'Creating…' : 'Create peer'}
          </Button>
        </>
      }
    >
      <form id="peer-wizard" onSubmit={submit} className="space-y-4">
        <div className="grid grid-cols-2 gap-3">
          <Field label="Name" htmlFor="pname" error={errors.fields.name}>
            <Input id="pname" required placeholder="Ada's Laptop" value={form.name} onChange={(e) => setForm({ ...form, name: e.target.value })} />
          </Field>
          <Field label="Device type" htmlFor="pdev" error={errors.fields.deviceType}>
            <Input id="pdev" placeholder="Laptop" value={form.deviceType} onChange={(e) => setForm({ ...form, deviceType: e.target.value })} />
          </Field>
        </div>

        <Toggle
          checked={form.generateKeypair}
          onChange={(v) => setForm({ ...form, generateKeypair: v })}
          label="Generate keypair on the server"
          hint="Turn off to paste the client's public key (the client keeps its private key; no full config is rendered)."
        />
        {!form.generateKeypair && (
          <Field label="Client public key" htmlFor="ppub" error={errors.fields.publicKey}>
            <Input id="ppub" required placeholder="base64 public key" value={form.publicKey} onChange={(e) => setForm({ ...form, publicKey: e.target.value })} />
          </Field>
        )}
        <Toggle
          checked={form.usePresharedKey}
          onChange={(v) => setForm({ ...form, usePresharedKey: v })}
          label="Use a preshared key"
          hint="Adds an extra symmetric layer (recommended)."
        />

        <details className="rounded-md border px-3 py-2 dark:border-ink-800">
          <summary className="cursor-pointer text-sm font-medium text-ink-700 dark:text-ink-200">Advanced</summary>
          <div className="mt-3 space-y-3">
            <Field label="Address — auto-allocated if blank" htmlFor="paddr" error={errors.fields.assignedAddress}>
              <Input id="paddr" placeholder="10.8.0.5/32" value={form.assignedAddress} onChange={(e) => setForm({ ...form, assignedAddress: e.target.value })} />
            </Field>
            <Field label="Allowed IPs (comma-separated)" htmlFor="paip" error={errors.fields.allowedIps}>
              <Input id="paip" placeholder="0.0.0.0/0, ::/0" value={form.allowedIps} onChange={(e) => setForm({ ...form, allowedIps: e.target.value })} />
            </Field>
            <Field label="Persistent keepalive (seconds)" htmlFor="pka" error={errors.fields.persistentKeepalive}>
              <Input id="pka" type="number" placeholder="25" value={form.persistentKeepalive} onChange={(e) => setForm({ ...form, persistentKeepalive: e.target.value })} />
            </Field>
          </div>
        </details>

        {errors.general && <p className="text-sm text-danger-600 dark:text-danger-500">{errors.general}</p>}
      </form>
    </Dialog>
  );
}

function PeerResult({ result, name, onClose }: { result: CreatePeerResponse; name: string; onClose: () => void }) {
  return (
    <Dialog
      open
      onClose={onClose}
      title="Peer created"
      description={`Assigned ${result.assignedAddress}. Share this with the device now — the private key is shown only once.`}
      footer={<Button onClick={onClose}>Done</Button>}
    >
      {result.config ? (
        <div className="space-y-4">
          <div className="flex flex-col items-center gap-3 sm:flex-row sm:items-start">
            {result.qrCodePngBase64 && (
              <img
                src={`data:image/png;base64,${result.qrCodePngBase64}`}
                alt="WireGuard config QR code"
                className="size-44 shrink-0 rounded-md border bg-white p-1 dark:border-ink-700"
              />
            )}
            <div className="flex-1">
              <p className="text-sm text-ink-500">Scan with the WireGuard mobile app, or download the <code>.conf</code> for desktop.</p>
              <Button variant="secondary" size="sm" className="mt-2" onClick={() => downloadText(confFilename(name), result.config!)}>
                <Download /> Download .conf
              </Button>
            </div>
          </div>
          <CodeBlock content={result.config} />
        </div>
      ) : (
        <p className="text-sm text-ink-500">
          This peer uses a client-supplied public key, so the server can't render a full config. Configure the
          client with its own private key, the server's public key, and address <span className="font-mono">{result.assignedAddress}</span>.
        </p>
      )}
    </Dialog>
  );
}

function Toggle({
  checked,
  onChange,
  label,
  hint,
}: {
  checked: boolean;
  onChange: (value: boolean) => void;
  label: string;
  hint?: string;
}) {
  return (
    <label className="flex cursor-pointer items-start gap-2.5">
      <input
        type="checkbox"
        checked={checked}
        onChange={(e) => onChange(e.target.checked)}
        className="mt-0.5 size-4 shrink-0 accent-gold-500"
      />
      <span>
        <span className="text-sm font-medium text-ink-800 dark:text-ink-100">{label}</span>
        {hint && <span className="mt-0.5 block text-xs text-ink-500">{hint}</span>}
      </span>
    </label>
  );
}
