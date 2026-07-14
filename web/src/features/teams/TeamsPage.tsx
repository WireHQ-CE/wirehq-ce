import { useState, type FormEvent } from 'react';
import { useNavigate } from 'react-router-dom';
import { Plus, Users2 } from 'lucide-react';
import { Button } from '@/components/ui/button';
import { Dialog } from '@/components/ui/dialog';
import { Field, Input } from '@/components/ui/input';
import { useToast } from '@/components/ui/toast';
import { EmptyState } from '@/components/data/EmptyState';
import { PageHeader } from '@/components/layout/AppShell';
import { noFormErrors, toFormErrors, type FormErrors } from '@/lib/api/form-errors';
import { useAuthStore } from '@/stores/auth-store';
import { useCreateTeam, useTeams } from './api';
import type { TeamListItem } from './types';

/** Teams in the active organization — the intra-tenant grouping in the SaaS hierarchy. */
export function TeamsPage() {
  const { data, isLoading } = useTeams();
  const canManage = useAuthStore((s) => s.hasPermission('identity.teams.manage'));
  const [creating, setCreating] = useState(false);

  return (
    <div>
      <PageHeader
        title="Teams"
        subtitle="Group people inside this organization. Teams scope ownership and collaboration."
        action={canManage && <Button onClick={() => setCreating(true)}><Plus /> New team</Button>}
      />

      {isLoading ? (
        <div className="h-40 animate-pulse rounded-lg bg-ink-100 dark:bg-ink-800" />
      ) : !data || data.length === 0 ? (
        <EmptyState
          icon={Users2}
          title="No teams yet"
          description="Create a team to group members and organize who owns what inside this organization."
          action={canManage && <Button onClick={() => setCreating(true)}><Plus /> New team</Button>}
        />
      ) : (
        <div className="overflow-hidden rounded-lg border bg-ink-0 dark:border-ink-800 dark:bg-ink-900">
          <table className="w-full text-sm">
            <thead>
              <tr className="border-b text-left text-xs uppercase tracking-wide text-ink-500 dark:border-ink-800">
                <th className="px-5 py-3 font-medium">Team</th>
                <th className="px-5 py-3 font-medium">Members</th>
                <th className="px-5 py-3 font-medium">Created</th>
              </tr>
            </thead>
            <tbody>
              {data.map((t) => (
                <TeamRow key={t.id} team={t} />
              ))}
            </tbody>
          </table>
        </div>
      )}

      {data && data.length > 0 && (
        <p className="mt-3 text-sm text-ink-400">{data.length} team{data.length === 1 ? '' : 's'}</p>
      )}

      {creating && <CreateTeamDialog onClose={() => setCreating(false)} />}
    </div>
  );
}

function TeamRow({ team }: { team: TeamListItem }) {
  const navigate = useNavigate();
  return (
    <tr
      className="cursor-pointer border-b last:border-0 hover:bg-ink-50 dark:border-ink-800 dark:hover:bg-ink-850"
      onClick={() => navigate(`/app/teams/${team.id}`)}
    >
      <td className="px-5 py-3">
        <div className="font-medium text-ink-800 dark:text-ink-100">{team.name}</div>
        <div className="font-mono text-xs text-ink-400">{team.slug}</div>
        {team.description && <div className="mt-0.5 text-xs text-ink-500">{team.description}</div>}
      </td>
      <td className="px-5 py-3 text-ink-500">{team.memberCount}</td>
      <td className="px-5 py-3 text-ink-400">{new Date(team.createdAtUtc).toLocaleDateString()}</td>
    </tr>
  );
}

function CreateTeamDialog({ onClose }: { onClose: () => void }) {
  const toast = useToast();
  const navigate = useNavigate();
  const create = useCreateTeam();
  const [name, setName] = useState('');
  const [description, setDescription] = useState('');
  const [errors, setErrors] = useState<FormErrors>(noFormErrors);

  function submit(e: FormEvent) {
    e.preventDefault();
    setErrors(noFormErrors);
    create.mutate(
      { name, description: description || undefined },
      {
        onSuccess: (res) => {
          toast('Team created.');
          onClose();
          navigate(`/app/teams/${res.id}`);
        },
        onError: (err) => setErrors(toFormErrors(err, 'Could not create the team.')),
      },
    );
  }

  return (
    <Dialog
      open
      onClose={onClose}
      title="New team"
      description="Create a team inside this organization, then add members to it."
      footer={
        <>
          <Button variant="secondary" onClick={onClose}>Cancel</Button>
          <Button type="submit" form="create-team" disabled={create.isPending}>
            {create.isPending ? 'Creating…' : 'Create team'}
          </Button>
        </>
      }
    >
      <form id="create-team" onSubmit={submit} className="space-y-4">
        <Field label="Team name" htmlFor="ct-name" error={errors.fields.name}>
          <Input id="ct-name" required value={name} onChange={(e) => setName(e.target.value)} placeholder="Platform Engineering" />
        </Field>
        <Field label="Description" htmlFor="ct-desc" error={errors.fields.description}>
          <Input id="ct-desc" value={description} onChange={(e) => setDescription(e.target.value)} placeholder="Optional" />
        </Field>
        {errors.general && <p className="text-sm text-danger-600 dark:text-danger-500">{errors.general}</p>}
      </form>
    </Dialog>
  );
}
