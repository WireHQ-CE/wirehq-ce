import { useEffect, useMemo, useState, type FormEvent } from 'react';
import { PageHeader } from '@/components/layout/AppShell';
import { Card, CardContent } from '@/components/ui/card';
import { Button } from '@/components/ui/button';
import { Input, Field } from '@/components/ui/input';
import { Dialog } from '@/components/ui/dialog';
import { useToast } from '@/components/ui/toast';
import { ApiError } from '@/lib/api/client';
import { useAuthStore } from '@/stores/auth-store';
import {
  useOrgRoles,
  useRole,
  usePermissionCatalog,
  useCreateRole,
  useUpdateRole,
  useDeleteRole,
  type OrgRole,
  type PermissionItem,
} from './api';

export function RolesSettingsPage() {
  const hasFeature = useAuthStore((s) => s.hasFeature);
  const hasPermission = useAuthStore((s) => s.hasPermission);
  const canManage = hasPermission('identity.roles.manage');
  const { data: roles, isLoading } = useOrgRoles();
  const toast = useToast();
  const del = useDeleteRole();

  // editorId: undefined = closed, null = create, string = edit that role.
  const [editorId, setEditorId] = useState<string | null | undefined>(undefined);

  if (!hasFeature('rbac.custom_roles')) {
    return (
      <>
        <PageHeader title="Roles" subtitle="Define custom roles for your organization." />
        <Card className="max-w-2xl">
          <CardContent>
            <p className="text-sm text-ink-400">
              Custom roles are an <span className="font-medium text-ink-200">Enterprise</span> feature. Contact
              sales to enable them for your organization.
            </p>
          </CardContent>
        </Card>
      </>
    );
  }

  const systemRoles = roles?.filter((r) => r.isSystem) ?? [];
  const customRoles = roles?.filter((r) => !r.isSystem) ?? [];

  const remove = (role: OrgRole) => {
    if (!window.confirm(`Delete the role “${role.name}”? Members must be reassigned first.`)) return;
    del.mutate(role.id, {
      onSuccess: () => toast('Role deleted.'),
      onError: (e) => toast(e instanceof ApiError ? e.message : 'Could not delete the role.', 'error'),
    });
  };

  return (
    <>
      <PageHeader
        title="Roles"
        subtitle="System roles are built in; create custom roles to fit your organization."
        action={canManage ? <Button onClick={() => setEditorId(null)}>New role</Button> : undefined}
      />

      {isLoading ? (
        <p className="text-sm text-ink-400">Loading…</p>
      ) : (
        <div className="max-w-3xl space-y-6">
          <RoleGroup title="Custom roles" empty="No custom roles yet.">
            {customRoles.map((role) => (
              <RoleRow key={role.id} role={role}>
                {canManage && (
                  <div className="flex gap-2">
                    <Button variant="ghost" onClick={() => setEditorId(role.id)}>Edit</Button>
                    <Button variant="ghost" onClick={() => remove(role)}>Delete</Button>
                  </div>
                )}
              </RoleRow>
            ))}
          </RoleGroup>

          <RoleGroup title="System roles" empty="">
            {systemRoles.map((role) => (
              <RoleRow key={role.id} role={role}>
                <span className="rounded-full bg-ink-500/15 px-2 py-0.5 text-xs font-medium text-ink-400">Built-in</span>
              </RoleRow>
            ))}
          </RoleGroup>
        </div>
      )}

      {editorId !== undefined && (
        <RoleEditorDialog roleId={editorId} onClose={() => setEditorId(undefined)} />
      )}
    </>
  );
}

function RoleGroup({ title, empty, children }: { title: string; empty: string; children: React.ReactNode }) {
  const items = Array.isArray(children) ? children : [children];
  const hasItems = items.some(Boolean);
  return (
    <Card>
      <CardContent>
        <h3 className="mb-3 text-sm font-semibold text-ink-100">{title}</h3>
        {hasItems ? <div className="divide-y divide-ink-800">{children}</div> : <p className="text-sm text-ink-400">{empty}</p>}
      </CardContent>
    </Card>
  );
}

function RoleRow({ role, children }: { role: OrgRole; children: React.ReactNode }) {
  return (
    <div className="flex items-center justify-between gap-4 py-3 first:pt-0 last:pb-0">
      <div>
        <p className="text-sm font-medium text-ink-100">{role.name}</p>
        {role.description && <p className="mt-0.5 text-xs text-ink-400">{role.description}</p>}
      </div>
      {children}
    </div>
  );
}

function RoleEditorDialog({ roleId, onClose }: { roleId: string | null; onClose: () => void }) {
  const isEdit = roleId !== null;
  const hasPermission = useAuthStore((s) => s.hasPermission);
  const { data: role } = useRole(roleId);
  const { data: permissions } = usePermissionCatalog();
  const create = useCreateRole();
  const update = useUpdateRole();
  const toast = useToast();

  const [name, setName] = useState('');
  const [description, setDescription] = useState('');
  const [selected, setSelected] = useState<Set<string>>(new Set());

  // Hydrate the form once the role detail arrives (edit) or reset for create.
  useEffect(() => {
    if (isEdit && role) {
      setName(role.name);
      setDescription(role.description ?? '');
      setSelected(new Set(role.permissionIds));
    }
  }, [isEdit, role]);

  const grouped = useMemo(() => {
    const groups = new Map<string, PermissionItem[]>();
    for (const p of permissions ?? []) {
      const list = groups.get(p.group);
      if (list) list.push(p);
      else groups.set(p.group, [p]);
    }
    return [...groups.entries()];
  }, [permissions]);

  const toggle = (id: string) =>
    setSelected((s) => {
      const next = new Set(s);
      if (next.has(id)) next.delete(id);
      else next.add(id);
      return next;
    });

  const pending = create.isPending || update.isPending;

  const submit = (e: FormEvent) => {
    e.preventDefault();
    const input = { name, description: description.trim() || null, permissionIds: [...selected] };
    const opts = {
      onSuccess: () => {
        toast(isEdit ? 'Role updated.' : 'Role created.');
        onClose();
      },
      onError: (err: unknown) => toast(err instanceof ApiError ? err.message : 'Could not save the role.', 'error'),
    };
    if (isEdit) update.mutate({ id: roleId, ...input }, opts);
    else create.mutate(input, opts);
  };

  return (
    <Dialog
      open
      onClose={onClose}
      title={isEdit ? 'Edit role' : 'New role'}
      description="Pick the permissions this role grants. You can only grant permissions you hold yourself."
      className="max-w-2xl"
      footer={
        <>
          <Button variant="secondary" onClick={onClose}>Cancel</Button>
          <Button form="role-form" type="submit" disabled={pending || !name.trim()}>
            {isEdit ? 'Save changes' : 'Create role'}
          </Button>
        </>
      }
    >
      <form id="role-form" onSubmit={submit} className="space-y-4">
        <Field label="Name" htmlFor="role-name">
          <Input id="role-name" value={name} onChange={(e) => setName(e.target.value)} required maxLength={64} />
        </Field>
        <Field label="Description" htmlFor="role-desc">
          <Input id="role-desc" value={description} onChange={(e) => setDescription(e.target.value)} maxLength={512} />
        </Field>

        <div className="space-y-4">
          <p className="text-sm font-medium text-ink-200">Permissions</p>
          {grouped.map(([group, perms]) => (
            <div key={group} className="space-y-1.5">
              <p className="text-xs font-semibold uppercase tracking-wide text-ink-500">{group}</p>
              {perms.map((p) => {
                // Mirror the server's escalation guard: you can add only permissions you hold, but may remove an
                // existing grant you lack.
                const disabled = !hasPermission(p.key) && !selected.has(p.id);
                return (
                  <label key={p.id} className={`flex items-start gap-2 text-sm ${disabled ? 'opacity-50' : ''}`}>
                    <input
                      type="checkbox"
                      className="mt-0.5"
                      checked={selected.has(p.id)}
                      disabled={disabled}
                      onChange={() => toggle(p.id)}
                    />
                    <span>
                      <span className="text-ink-200">{p.description}</span>
                      <span className="ml-1 text-xs text-ink-500">({p.key})</span>
                    </span>
                  </label>
                );
              })}
            </div>
          ))}
        </div>
      </form>
    </Dialog>
  );
}
