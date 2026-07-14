import { useEffect, useRef, useState, type ChangeEvent, type FormEvent } from 'react';
import { Trash2, Upload } from 'lucide-react';
import { PageHeader } from '@/components/layout/AppShell';
import { Card, CardContent, CardHeader, CardTitle } from '@/components/ui/card';
import { Button } from '@/components/ui/button';
import { Input, Field } from '@/components/ui/input';
import { Select } from '@/components/ui/select';
import { Avatar } from '@/components/ui/Avatar';
import { useToast } from '@/components/ui/toast';
import { ApiError } from '@/lib/api/client';
import { noFormErrors, toFormErrors, type FormErrors } from '@/lib/api/form-errors';
import { LANGUAGES, TIMEZONES } from '@/lib/constants';
import { useAuth } from '@/features/auth/use-auth';
import { accountApi } from './api';

export function ProfilePage() {
  return (
    <>
      <PageHeader title="Profile" subtitle="Your personal account details." />
      <div className="max-w-xl space-y-6">
        <AvatarCard />
        <DetailsCard />
      </div>
    </>
  );
}

function AvatarCard() {
  const { user, refresh } = useAuth();
  const toast = useToast();
  const fileRef = useRef<HTMLInputElement>(null);
  const [busy, setBusy] = useState(false);

  async function onFile(e: ChangeEvent<HTMLInputElement>) {
    const file = e.target.files?.[0];
    e.target.value = ''; // allow re-selecting the same file
    if (!file) return;
    setBusy(true);
    try {
      const resized = await resizeImage(file, 256);
      await accountApi.uploadAvatar(resized);
      await refresh();
      toast('Avatar updated.');
    } catch (err) {
      toast(err instanceof ApiError ? err.message : 'Could not upload the image.', 'error');
    } finally {
      setBusy(false);
    }
  }

  async function remove() {
    setBusy(true);
    try {
      await accountApi.removeAvatar();
      await refresh();
      toast('Avatar removed.');
    } catch (err) {
      toast(err instanceof ApiError ? err.message : 'Could not remove the avatar.', 'error');
    } finally {
      setBusy(false);
    }
  }

  return (
    <Card>
      <CardHeader>
        <CardTitle>Photo</CardTitle>
      </CardHeader>
      <CardContent>
        <div className="flex items-center gap-4">
          <Avatar src={user?.avatarUrl} name={user?.name} className="size-16 text-lg" />
          <div className="flex flex-wrap items-center gap-2">
            <input ref={fileRef} type="file" accept="image/png,image/jpeg,image/webp" className="hidden" onChange={onFile} />
            <Button type="button" variant="secondary" size="sm" onClick={() => fileRef.current?.click()} disabled={busy}>
              <Upload /> {user?.avatarUrl ? 'Change' : 'Upload'}
            </Button>
            {user?.avatarUrl && (
              <Button type="button" variant="ghost" size="sm" onClick={remove} disabled={busy}>
                <Trash2 /> Remove
              </Button>
            )}
          </div>
        </div>
        <p className="mt-3 text-xs text-ink-400">PNG, JPEG or WebP. Resized to 256×256; max 512 KB after resize.</p>
      </CardContent>
    </Card>
  );
}

function DetailsCard() {
  const { user, refresh } = useAuth();
  const toast = useToast();
  const [firstName, setFirstName] = useState('');
  const [lastName, setLastName] = useState('');
  const [username, setUsername] = useState('');
  const [jobTitle, setJobTitle] = useState('');
  const [phone, setPhone] = useState('');
  const [timezone, setTimezone] = useState('');
  const [language, setLanguage] = useState('');
  const [errors, setErrors] = useState<FormErrors>(noFormErrors);
  const [busy, setBusy] = useState(false);

  useEffect(() => {
    if (!user) return;
    const [first, ...rest] = (user.name ?? '').trim().split(' ');
    setFirstName(user.firstName ?? first ?? '');
    setLastName(user.lastName ?? rest.join(' '));
    setUsername(user.username ?? '');
    setJobTitle(user.jobTitle ?? '');
    setPhone(user.phone ?? '');
    setTimezone(user.timezone ?? '');
    setLanguage(user.language ?? '');
  }, [user]);

  async function submit(e: FormEvent) {
    e.preventDefault();
    setErrors(noFormErrors);
    setBusy(true);
    try {
      await accountApi.updateProfile({
        firstName,
        lastName,
        username: username.trim() || null,
        jobTitle: jobTitle.trim() || null,
        phone: phone.trim() || null,
        timezone: timezone || null,
        language: language || null,
      });
      await refresh();
      toast('Profile updated.');
    } catch (err) {
      setErrors(toFormErrors(err, 'Could not update profile.'));
    } finally {
      setBusy(false);
    }
  }

  return (
    <Card>
      <CardHeader>
        <CardTitle>Details</CardTitle>
      </CardHeader>
      <CardContent>
        <form onSubmit={submit} className="space-y-4">
          <div className="grid grid-cols-2 gap-3">
            <Field label="First name" htmlFor="firstName" error={errors.fields.firstName}>
              <Input id="firstName" required value={firstName} onChange={(e) => setFirstName(e.target.value)} />
            </Field>
            <Field label="Last name" htmlFor="lastName" error={errors.fields.lastName}>
              <Input id="lastName" required value={lastName} onChange={(e) => setLastName(e.target.value)} />
            </Field>
          </div>
          <Field label="Email" htmlFor="email">
            <Input id="email" value={user?.email ?? ''} disabled />
          </Field>
          <Field label="Username" htmlFor="username" error={errors.fields.username}>
            <Input id="username" value={username} onChange={(e) => setUsername(e.target.value)} placeholder="optional" />
          </Field>
          <div className="grid grid-cols-2 gap-3">
            <Field label="Job title" htmlFor="jobTitle" error={errors.fields.jobTitle}>
              <Input id="jobTitle" value={jobTitle} onChange={(e) => setJobTitle(e.target.value)} placeholder="optional" />
            </Field>
            <Field label="Phone" htmlFor="phone" error={errors.fields.phone}>
              <Input id="phone" value={phone} onChange={(e) => setPhone(e.target.value)} placeholder="optional" />
            </Field>
          </div>
          <div className="grid grid-cols-2 gap-3">
            <Field label="Timezone" htmlFor="timezone" error={errors.fields.timezone}>
              <Select id="timezone" value={timezone} onChange={(e) => setTimezone(e.target.value)}>
                <option value="">Select…</option>
                {TIMEZONES.map((t) => <option key={t.value} value={t.value}>{t.label}</option>)}
              </Select>
            </Field>
            <Field label="Language" htmlFor="language" error={errors.fields.language}>
              <Select id="language" value={language} onChange={(e) => setLanguage(e.target.value)}>
                <option value="">Select…</option>
                {LANGUAGES.map((l) => <option key={l.value} value={l.value}>{l.label}</option>)}
              </Select>
            </Field>
          </div>
          {errors.general && <p className="text-sm text-danger-600 dark:text-danger-500">{errors.general}</p>}
          <Button type="submit" disabled={busy}>{busy ? 'Saving…' : 'Save changes'}</Button>
        </form>
      </CardContent>
    </Card>
  );
}

/** Downscale + re-encode an image client-side so uploads stay small (and within the server's 512 KB cap). */
async function resizeImage(file: File, max: number): Promise<File> {
  const dataUrl = await new Promise<string>((resolve, reject) => {
    const reader = new FileReader();
    reader.onload = () => resolve(reader.result as string);
    reader.onerror = () => reject(new Error('read failed'));
    reader.readAsDataURL(file);
  });

  const img = await new Promise<HTMLImageElement>((resolve, reject) => {
    const el = new Image();
    el.onload = () => resolve(el);
    el.onerror = () => reject(new Error('decode failed'));
    el.src = dataUrl;
  });

  const scale = Math.min(1, max / Math.max(img.width, img.height));
  const w = Math.max(1, Math.round(img.width * scale));
  const h = Math.max(1, Math.round(img.height * scale));
  const canvas = document.createElement('canvas');
  canvas.width = w;
  canvas.height = h;
  canvas.getContext('2d')!.drawImage(img, 0, 0, w, h);

  const blob = await new Promise<Blob>((resolve, reject) => {
    canvas.toBlob((b) => (b ? resolve(b) : reject(new Error('encode failed'))), 'image/jpeg', 0.9);
  });

  return new File([blob], 'avatar.jpg', { type: 'image/jpeg' });
}
