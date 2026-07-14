import { useEffect, useState, type FormEvent } from 'react';
import { Link, useNavigate, useParams } from 'react-router-dom';
import { ArrowLeft, Pencil, Trash2, UserPlus, Users2 } from 'lucide-react';
import { Card, CardContent, CardHeader, CardTitle } from '@/components/ui/card';
import { Button } from '@/components/ui/button';
import { Badge } from '@/components/ui/badge';
import { Dialog } from '@/components/ui/dialog';
import { Field, Input } from '@/components/ui/input';
import { Select } from '@/components/ui/select';
import { useToast } from '@/components/ui/toast';
import { ApiError } from '@/lib/api/client';
import { noFormErrors, toFormErrors, type FormErrors } from '@/lib/api/form-errors';
import { useAuthStore } from '@/stores/auth-store';
import { useOrgRoles } from '@/features/roles/api';
import { useAddTeamMember, useDeleteTeam, useRemoveTeamMember, useTeam, useUpdateTeam } from './api';
import type { TeamDetail } from './types';

export function TeamDetailPage() {
  const { id = '' } = useParams();
  const navigate = useNavigate();
  const toast = useToast();
  const { data: team, isLoading } = useTeam(id);
  const canManage = useAuthStore((s) => s.hasPermission('identity.teams.manage'));
  const remove = useDeleteTeam();
  const [editing, setEditing] = useState(false);

  function deleteTeam() {
    if (!team || !window.confirm(`Delete the “${team.name}” team? This cannot be undone.`)) return;
    remove.mutate(id, {
      onSuccess: () => { toast('Team deleted.'); navigate('/app/teams'); },
      onError: (err) => toast(err instanceof ApiError ? err.message : 'Could not delete the team.', 'error'),
    });
  }

  if (isLoading) {
    return <div className="h-64 animate-pulse rounded-lg bg-ink-100 dark:bg-ink-800" />;
  }

  if (!team) {
    return <p className="text-ink-500">Team not found.</p>;
  }

  return (
    <div className="space-y-6">
      <Link to="/app/teams" className="inline-flex items-center gap-1.5 text-sm text-ink-500 hover:text-ink-800 dark:hover:text-ink-200">
        <ArrowLeft className="size-4" /> Teams
      </Link>

      <div className="flex flex-wrap items-start justify-between gap-4">
        <div>
          <h1 className="text-h1 text-ink-900 dark:text-ink-50">{team.name}</h1>
          <p className="mt-1 font-mono text-sm text-ink-400">{team.slug}</p>
          {team.description && <p className="mt-2 max-w-prose text-sm text-ink-500">{team.description}</p>}
        </div>
        {canManage && (
          <div className="flex flex-wrap items-center gap-2">
            <Button variant="secondary" onClick={() => setEditing(true)}><Pencil /> Edit</Button>
            <Button variant="secondary" onClick={deleteTeam} disabled={remove.isPending}><Trash2 /> Delete</Button>
          </div>
        )}
      </div>

      <TeamMembers team={team} canManage={canManage} />

      {editing && <EditTeamDialog id={id} current={team} onClose={() => setEditing(false)} />}
    </div>
  );
}

function TeamMembers({ team, canManage }: { team: TeamDetail; canManage: boolean }) {
  const toast = useToast();
  const remove = useRemoveTeamMember(team.id);
  const [adding, setAdding] = useState(false);

  function removeMember(membershipId: string, name: string) {
    if (!window.confirm(`Remove ${name} from this team?`)) return;
    remove.mutate(membershipId, {
      onSuccess: () => toast('Member removed.'),
      onError: (err) => toast(err instanceof ApiError ? err.message : 'Could not remove the member.', 'error'),
    });
  }

  return (
    <Card className="overflow-hidden">
      <CardHeader>
        <CardTitle>Members</CardTitle>
        {canManage && <Button size="sm" onClick={() => setAdding(true)}><UserPlus /> Add member</Button>}
      </CardHeader>
      <CardContent className="pt-0">
        {team.members.length === 0 ? (
          <EmptyMembers />
        ) : (
          <table className="w-full text-sm">
            <thead>
              <tr className="border-b text-left text-xs uppercase tracking-wide text-ink-500 dark:border-ink-800">
                <th className="px-2 py-2 font-medium">Name</th>
                <th className="px-2 py-2 font-medium">Status</th>
                <th className="px-2 py-2 font-medium">Added</th>
                {canManage && <th className="px-2 py-2" />}
              </tr>
            </thead>
            <tbody>
              {team.members.map((m) => (
                <tr key={m.membershipId} className="border-b last:border-0 dark:border-ink-800">
                  <td className="px-2 py-2">
                    <div className="font-medium text-ink-800 dark:text-ink-100">{m.name}</div>
                    <div className="text-xs text-ink-400">{m.email}</div>
                  </td>
                  <td className="px-2 py-2"><Badge tone={m.status === 'Active' ? 'success' : 'neutral'} dot>{m.status}</Badge></td>
                  <td className="px-2 py-2 text-ink-400">{new Date(m.addedAtUtc).toLocaleDateString()}</td>
                  {canManage && (
                    <td className="px-2 py-2 text-right">
                      <Button variant="ghost" size="sm" onClick={() => removeMember(m.membershipId, m.name)} disabled={remove.isPending}>
                        <Trash2 />
                      </Button>
                    </td>
                  )}
                </tr>
              ))}
            </tbody>
          </table>
        )}
      </CardContent>

      {adding && <AddMemberDialog team={team} onClose={() => setAdding(false)} />}
    </Card>
  );
}

function EmptyMembers() {
  return (
    <div className="flex items-center gap-2 py-2 text-sm text-ink-400">
      <Users2 className="size-4" /> No members yet.
    </div>
  );
}

function AddMemberDialog({ team, onClose }: { team: TeamDetail; onClose: () => void }) {
  const toast = useToast();
  const add = useAddTeamMember(team.id);
  const { data: roles, isLoading: rolesLoading } = useOrgRoles();
  const [email, setEmail] = useState('');
  const [name, setName] = useState('');
  const [roleId, setRoleId] = useState('');
  const [errors, setErrors] = useState<FormErrors>(noFormErrors);

  // Default the role to "Member" (least privilege) once roles load.
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
    add.mutate(
      { email: trimmed, name: name.trim() || undefined, roleId: roleId || undefined },
      {
        onSuccess: (res) => {
          toast(res.outcome === 'AlreadyMember' ? 'Added to the team.' : `Invitation sent to ${trimmed}.`);
          onClose();
        },
        onError: (err) => setErrors(toFormErrors(err, 'Could not add the member.')),
      },
    );
  }

  return (
    <Dialog
      open
      onClose={onClose}
      title="Add member"
      description="Invite a colleague by email to this team — they'll get access to this account."
      footer={
        <>
          <Button variant="secondary" onClick={onClose}>Cancel</Button>
          <Button type="submit" form="add-team-member" disabled={add.isPending}>
            {add.isPending ? 'Adding…' : 'Add member'}
          </Button>
        </>
      }
    >
      <form id="add-team-member" onSubmit={submit} className="space-y-4">
        <Field label="Email" htmlFor="atm-email" error={errors.fields.email}>
          <Input
            id="atm-email"
            type="email"
            required
            value={email}
            placeholder="colleague@company.com"
            onChange={(e) => setEmail(e.target.value)}
          />
        </Field>
        <Field label="Name (optional)" htmlFor="atm-name" error={errors.fields.name}>
          <Input id="atm-name" value={name} placeholder="Their name" onChange={(e) => setName(e.target.value)} />
        </Field>
        <Field label="Role" htmlFor="atm-role" error={errors.fields.roleId}>
          {rolesLoading ? (
            <div className="h-9 animate-pulse rounded bg-ink-100 dark:bg-ink-800" />
          ) : (
            <Select id="atm-role" value={roleId} onChange={(e) => setRoleId(e.target.value)}>
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

function EditTeamDialog({ id, current, onClose }: { id: string; current: TeamDetail; onClose: () => void }) {
  const toast = useToast();
  const update = useUpdateTeam(id);
  const [name, setName] = useState(current.name);
  const [description, setDescription] = useState(current.description ?? '');
  const [errors, setErrors] = useState<FormErrors>(noFormErrors);

  function submit(e: FormEvent) {
    e.preventDefault();
    setErrors(noFormErrors);
    update.mutate({ name, description }, {
      onSuccess: () => { toast('Team updated.'); onClose(); },
      onError: (err) => setErrors(toFormErrors(err, 'Could not update the team.')),
    });
  }

  return (
    <Dialog
      open
      onClose={onClose}
      title="Edit team"
      footer={
        <>
          <Button variant="secondary" onClick={onClose}>Cancel</Button>
          <Button type="submit" form="edit-team" disabled={update.isPending}>{update.isPending ? 'Saving…' : 'Save'}</Button>
        </>
      }
    >
      <form id="edit-team" onSubmit={submit} className="space-y-4">
        <Field label="Team name" htmlFor="et-name" error={errors.fields.name}>
          <Input id="et-name" required value={name} onChange={(e) => setName(e.target.value)} />
        </Field>
        <Field label="Description" htmlFor="et-desc" error={errors.fields.description}>
          <Input id="et-desc" value={description} onChange={(e) => setDescription(e.target.value)} placeholder="Optional" />
        </Field>
        {errors.general && <p className="text-sm text-danger-600 dark:text-danger-500">{errors.general}</p>}
      </form>
    </Dialog>
  );
}
