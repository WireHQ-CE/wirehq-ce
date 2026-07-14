import { useEffect, useState, type FormEvent } from 'react';
import { PageHeader } from '@/components/layout/AppShell';
import { Card, CardContent, CardHeader, CardTitle } from '@/components/ui/card';
import { Badge } from '@/components/ui/badge';
import { Button } from '@/components/ui/button';
import { Input, Field } from '@/components/ui/input';
import { Select } from '@/components/ui/select';
import { useToast } from '@/components/ui/toast';
import { noFormErrors, toFormErrors, type FormErrors } from '@/lib/api/form-errors';
import { COMPANY_SIZES, COUNTRIES, TIMEZONES } from '@/lib/constants';
import { useAuthStore } from '@/stores/auth-store';
import { EDITION } from '@/lib/edition';
import { useCurrentOrganization, useUpdateOrganization } from './api';

// The Community Edition has no billing and collects no vendor/CRM profile data — hide those fields.
// Config-as-code via the edition constant (docs/17 §6): byte-identical in SaaS, overridden by the CE
// overlay to 'community'. (NOT org.edition — that's the billing tier, which the CE sets to Enterprise.)
const isCE = EDITION === 'community';

export function OrganizationPage() {
  const canManage = useAuthStore((s) => s.hasPermission('org.settings.update'));
  const { data: org, isLoading } = useCurrentOrganization();

  return (
    <>
      <PageHeader
        title="Organization"
        subtitle={
          isCE
            ? 'Your tenant — the boundary of isolation and access.'
            : 'Your tenant — the boundary of isolation, billing, and access.'
        }
      />

      <div className="max-w-3xl space-y-6">
        <div className="grid grid-cols-1 gap-6 lg:grid-cols-2">
          {canManage ? <DetailsForm /> : <DetailsReadOnly />}
          <Card>
            <CardHeader>
              <CardTitle>Usage</CardTitle>
            </CardHeader>
            <CardContent className="space-y-3 text-sm">
              <Row label="Members" value={isLoading ? '—' : String(org?.memberCount ?? 0)} />
              <Row label="Teams" value={isLoading ? '—' : String(org?.teamCount ?? 0)} />
              <Row label="Edition" value={isLoading ? '—' : (org?.edition ?? '—')} />
              <Row label="Created" value={org ? new Date(org.createdAtUtc).toLocaleDateString() : '—'} />
            </CardContent>
          </Card>
        </div>

      </div>
    </>
  );
}

function DetailsReadOnly() {
  const { data: org, isLoading } = useCurrentOrganization();
  return (
    <Card>
      <CardHeader>
        <CardTitle>Details</CardTitle>
        {org && <Badge tone={org.status === 'Active' ? 'success' : 'neutral'} dot>{org.status}</Badge>}
      </CardHeader>
      <CardContent className="space-y-3 text-sm">
        <Row label="Name" value={isLoading ? '—' : (org?.name ?? '—')} />
        {!isCE && <Row label="URL slug" value={isLoading ? '—' : (org?.slug ?? '—')} mono />}
        {!isCE && <Row label="Legal name" value={org?.legalName ?? '—'} />}
        {!isCE && <Row label="Website" value={org?.website ?? '—'} />}
        {!isCE && <Row label="Industry" value={org?.industry ?? '—'} />}
        {!isCE && <Row label="Country" value={org?.country ?? '—'} />}
      </CardContent>
    </Card>
  );
}

function DetailsForm() {
  const { data: org } = useCurrentOrganization();
  const update = useUpdateOrganization();
  const toast = useToast();
  const [name, setName] = useState('');
  const [legalName, setLegalName] = useState('');
  const [website, setWebsite] = useState('');
  const [industry, setIndustry] = useState('');
  const [companySize, setCompanySize] = useState('');
  const [country, setCountry] = useState('');
  const [timezone, setTimezone] = useState('');
  const [errors, setErrors] = useState<FormErrors>(noFormErrors);

  useEffect(() => {
    if (!org) return;
    setName(org.name ?? '');
    setLegalName(org.legalName ?? '');
    setWebsite(org.website ?? '');
    setIndustry(org.industry ?? '');
    setCompanySize(org.companySize ?? '');
    setCountry(org.country ?? '');
    setTimezone(org.timezone ?? '');
  }, [org]);

  async function submit(e: FormEvent) {
    e.preventDefault();
    setErrors(noFormErrors);
    try {
      await update.mutateAsync({
        name,
        legalName: legalName.trim() || null,
        website: website.trim() || null,
        industry: industry.trim() || null,
        companySize: companySize || null,
        country: country || null,
        timezone: timezone || null,
      });
      toast('Organization updated.');
    } catch (err) {
      setErrors(toFormErrors(err, 'Could not update the organization.'));
    }
  }

  return (
    <Card>
      <CardHeader>
        <CardTitle>Details</CardTitle>
        {org && <Badge tone={org.status === 'Active' ? 'success' : 'neutral'} dot>{org.status}</Badge>}
      </CardHeader>
      <CardContent>
        <form onSubmit={submit} className="space-y-4">
          <Field label="Name" htmlFor="name" error={errors.fields.name}>
            <Input id="name" required value={name} onChange={(e) => setName(e.target.value)} />
          </Field>
          {!isCE && (
            <>
              <Field label="URL slug" htmlFor="slug">
                <Input id="slug" value={org?.slug ?? ''} disabled />
              </Field>
              <Field label="Legal name" htmlFor="legalName" error={errors.fields.legalName}>
                <Input id="legalName" value={legalName} onChange={(e) => setLegalName(e.target.value)} placeholder="optional" />
              </Field>
              <Field label="Website" htmlFor="website" error={errors.fields.website}>
                <Input id="website" value={website} onChange={(e) => setWebsite(e.target.value)} placeholder="https://…" />
              </Field>
              <div className="grid grid-cols-2 gap-3">
                <Field label="Industry" htmlFor="industry" error={errors.fields.industry}>
                  <Input id="industry" value={industry} onChange={(e) => setIndustry(e.target.value)} placeholder="optional" />
                </Field>
                <Field label="Company size" htmlFor="companySize" error={errors.fields.companySize}>
                  <Select id="companySize" value={companySize} onChange={(e) => setCompanySize(e.target.value)}>
                    <option value="">Select…</option>
                    {COMPANY_SIZES.map((s) => <option key={s} value={s}>{s}</option>)}
                  </Select>
                </Field>
              </div>
            </>
          )}
          <div className="grid grid-cols-2 gap-3">
            {!isCE && (
              <Field label="Country" htmlFor="country" error={errors.fields.country}>
                <Select id="country" value={country} onChange={(e) => setCountry(e.target.value)}>
                  <option value="">Select…</option>
                  {COUNTRIES.map((c) => <option key={c.value} value={c.value}>{c.label}</option>)}
                </Select>
              </Field>
            )}
            <Field label="Timezone" htmlFor="timezone" error={errors.fields.timezone}>
              <Select id="timezone" value={timezone} onChange={(e) => setTimezone(e.target.value)}>
                <option value="">Select…</option>
                {TIMEZONES.map((t) => <option key={t.value} value={t.value}>{t.label}</option>)}
              </Select>
            </Field>
          </div>
          {errors.general && <p className="text-sm text-danger-600 dark:text-danger-500">{errors.general}</p>}
          <Button type="submit" disabled={update.isPending}>{update.isPending ? 'Saving…' : 'Save changes'}</Button>
        </form>
      </CardContent>
    </Card>
  );
}

function Row({ label, value, mono }: { label: string; value: string; mono?: boolean }) {
  return (
    <div className="flex items-center justify-between">
      <span className="text-ink-500">{label}</span>
      <span className={mono ? 'font-mono text-ink-800 dark:text-ink-200' : 'text-ink-800 dark:text-ink-200'}>{value}</span>
    </div>
  );
}
