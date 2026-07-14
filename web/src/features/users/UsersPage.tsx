import { useEffect, useState, type FormEvent } from 'react';
import { UserPlus, Users2 } from 'lucide-react';
import { PageHeader } from '@/components/layout/AppShell';
import { Card } from '@/components/ui/card';
import { Button } from '@/components/ui/button';
import { Dialog } from '@/components/ui/dialog';
import { Field, Input } from '@/components/ui/input';
import { Select } from '@/components/ui/select';
import { Badge } from '@/components/ui/badge';
import { useToast } from '@/components/ui/toast';
import { EmptyState } from '@/components/data/EmptyState';
import { noFormErrors, toFormErrors, type FormErrors } from '@/lib/api/form-errors';
import { useAuthStore } from '@/stores/auth-store';
import { useOrgRoles } from '@/features/roles/api';
import { useInviteUser, useUsers } from './api';

const statusTone: Record<string, 'success' | 'warning' | 'neutral'> = {
  Active: 'success',
  Invited: 'warning',
  Suspended: 'neutral',
};

export function UsersPage() {
  const [search, setSearch] = useState('');
  const [inviting, setInviting] = useState(false);
  const { data, isLoading } = useUsers(search);
  const canInvite = useAuthStore((s) => s.hasPermission('identity.users.invite'));

  return (
    <>
      <PageHeader
        title="Users"
        subtitle="People with access to this organization."
        action={
          canInvite && (
            <Button onClick={() => setInviting(true)}>
              <UserPlus /> Invite user
            </Button>
          )
        }
      />

      {inviting && <InviteUserDialog onClose={() => setInviting(false)} />}

      <div className="mb-4 max-w-xs">
        <Input placeholder="Search users…" value={search} onChange={(e) => setSearch(e.target.value)} />
      </div>

      <Card className="overflow-hidden">
        {isLoading ? (
          <TableSkeleton />
        ) : !data || data.items.length === 0 ? (
          <EmptyState icon={Users2} title="No users found" description="Invite teammates to collaborate in this organization." />
        ) : (
          <table className="w-full text-sm">
            <thead>
              <tr className="border-b text-left text-xs uppercase tracking-wide text-ink-500 dark:border-ink-800">
                <th className="px-5 py-3 font-medium">Name</th>
                <th className="px-5 py-3 font-medium">Email</th>
                <th className="px-5 py-3 font-medium">Status</th>
                <th className="px-5 py-3 font-medium">Joined</th>
              </tr>
            </thead>
            <tbody>
              {data.items.map((u) => (
                <tr key={u.membershipId} className="border-b last:border-0 hover:bg-ink-50 dark:border-ink-800 dark:hover:bg-ink-850">
                  <td className="px-5 py-3 font-medium text-ink-800 dark:text-ink-100">{u.name}</td>
                  <td className="px-5 py-3 font-mono text-ink-500">{u.email}</td>
                  <td className="px-5 py-3">
                    <Badge tone={statusTone[u.status] ?? 'neutral'} dot>
                      {u.status}
                    </Badge>
                  </td>
                  <td className="px-5 py-3 tabular text-ink-500">
                    {u.joinedAtUtc ? new Date(u.joinedAtUtc).toLocaleDateString() : '—'}
                  </td>
                </tr>
              ))}
            </tbody>
          </table>
        )}
      </Card>

      {data && data.total > 0 && (
        <p className="mt-3 text-sm text-ink-400">{data.total} member{data.total === 1 ? '' : 's'}</p>
      )}
    </>
  );
}

function InviteUserDialog({ onClose }: { onClose: () => void }) {
  const toast = useToast();
  const invite = useInviteUser();
  const { data: roles, isLoading: rolesLoading } = useOrgRoles();
  const [email, setEmail] = useState('');
  const [name, setName] = useState('');
  const [roleId, setRoleId] = useState('');
  const [errors, setErrors] = useState<FormErrors>(noFormErrors);

  // Default to the least-privilege "Member" role once roles load.
  useEffect(() => {
    if (!roleId && roles?.length) {
      setRoleId(roles.find((r) => r.name === 'Member')?.id ?? roles[0].id);
    }
  }, [roles, roleId]);

  function submit(e: FormEvent) {
    e.preventDefault();
    setErrors(noFormErrors);
    const trimmed = email.trim();
    if (!trimmed) {
      setErrors({ fields: { email: 'Enter an email address.' }, general: null });
      return;
    }
    invite.mutate(
      { email: trimmed, name: name.trim() || undefined, roleId: roleId || undefined },
      {
        onSuccess: () => {
          toast(`Invitation sent to ${trimmed}.`);
          onClose();
        },
        onError: (err) => setErrors(toFormErrors(err, 'Could not send the invitation.')),
      },
    );
  }

  return (
    <Dialog
      open
      onClose={onClose}
      title="Invite user"
      description="Invite a colleague by email to this organization — they'll get a link to set a password and join."
      footer={
        <>
          <Button variant="secondary" onClick={onClose}>Cancel</Button>
          <Button type="submit" form="invite-user" disabled={invite.isPending}>
            {invite.isPending ? 'Sending…' : 'Send invitation'}
          </Button>
        </>
      }
    >
      <form id="invite-user" onSubmit={submit} className="space-y-4">
        <Field label="Email" htmlFor="iu-email" error={errors.fields.email}>
          <Input
            id="iu-email"
            type="email"
            required
            value={email}
            placeholder="colleague@company.com"
            onChange={(e) => setEmail(e.target.value)}
          />
        </Field>
        <Field label="Name (optional)" htmlFor="iu-name" error={errors.fields.name}>
          <Input id="iu-name" value={name} placeholder="Their name" onChange={(e) => setName(e.target.value)} />
        </Field>
        <Field label="Role" htmlFor="iu-role" error={errors.fields.roleIds ?? errors.fields.roleId}>
          {rolesLoading ? (
            <div className="h-9 animate-pulse rounded bg-ink-100 dark:bg-ink-800" />
          ) : (
            <Select id="iu-role" value={roleId} onChange={(e) => setRoleId(e.target.value)}>
              {(roles ?? []).map((r) => (
                <option key={r.id} value={r.id}>{r.name}</option>
              ))}
            </Select>
          )}
        </Field>
        {errors.general && <p className="text-sm text-danger-600 dark:text-danger-500">{errors.general}</p>}
      </form>
    </Dialog>
  );
}

function TableSkeleton() {
  return (
    <div className="divide-y dark:divide-ink-800">
      {Array.from({ length: 5 }).map((_, i) => (
        <div key={i} className="flex items-center gap-4 px-5 py-3.5">
          <div className="h-4 w-32 animate-pulse rounded bg-ink-100 dark:bg-ink-800" />
          <div className="h-4 w-48 animate-pulse rounded bg-ink-100 dark:bg-ink-800" />
          <div className="ml-auto h-5 w-16 animate-pulse rounded-full bg-ink-100 dark:bg-ink-800" />
        </div>
      ))}
    </div>
  );
}
