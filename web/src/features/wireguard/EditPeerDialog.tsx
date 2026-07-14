import { useState, type FormEvent } from 'react';
import { Button } from '@/components/ui/button';
import { Input, Field } from '@/components/ui/input';
import { Dialog } from '@/components/ui/dialog';
import { useToast } from '@/components/ui/toast';
import { noFormErrors, toFormErrors, type FormErrors } from '@/lib/api/form-errors';
import { useUpdatePeer } from './api';
import type { PeerListItem } from './types';

export function EditPeerDialog({ instanceId, peer, onClose }: { instanceId: string; peer: PeerListItem; onClose: () => void }) {
  const toast = useToast();
  const update = useUpdatePeer(instanceId);
  const [name, setName] = useState(peer.name);
  const [deviceType, setDeviceType] = useState(peer.deviceType ?? '');
  const [keepalive, setKeepalive] = useState('');
  const [errors, setErrors] = useState<FormErrors>(noFormErrors);

  function submit(e: FormEvent) {
    e.preventDefault();
    setErrors(noFormErrors);
    update.mutate(
      {
        peerId: peer.id,
        input: {
          name: name.trim(),
          deviceType: deviceType.trim(),
          persistentKeepalive: keepalive.trim() ? Number(keepalive) : undefined,
        },
      },
      {
        onSuccess: () => {
          toast('Peer updated.');
          onClose();
        },
        onError: (err) => setErrors(toFormErrors(err, 'Could not update the peer.')),
      },
    );
  }

  return (
    <Dialog
      open
      onClose={onClose}
      title="Edit peer"
      footer={
        <>
          <Button variant="secondary" onClick={onClose}>Cancel</Button>
          <Button type="submit" form="edit-peer" disabled={update.isPending}>
            {update.isPending ? 'Saving…' : 'Save'}
          </Button>
        </>
      }
    >
      <form id="edit-peer" onSubmit={submit} className="space-y-4">
        <Field label="Name" htmlFor="epn" error={errors.fields.name}>
          <Input id="epn" required value={name} onChange={(e) => setName(e.target.value)} />
        </Field>
        <Field label="Device type" htmlFor="epd" error={errors.fields.deviceType}>
          <Input id="epd" value={deviceType} onChange={(e) => setDeviceType(e.target.value)} />
        </Field>
        <Field label="Persistent keepalive (seconds, optional)" htmlFor="epk" error={errors.fields.persistentKeepalive}>
          <Input id="epk" type="number" placeholder="leave blank to keep" value={keepalive} onChange={(e) => setKeepalive(e.target.value)} />
        </Field>
        {errors.general && <p className="text-sm text-danger-600 dark:text-danger-500">{errors.general}</p>}
      </form>
    </Dialog>
  );
}
