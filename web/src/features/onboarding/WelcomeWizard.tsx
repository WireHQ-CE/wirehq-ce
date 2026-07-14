import { useEffect, useState, type FormEvent } from 'react';
import { useNavigate } from 'react-router-dom';
import { ArrowRight, Sparkles } from 'lucide-react';
import { Card, CardContent } from '@/components/ui/card';
import { Button } from '@/components/ui/button';
import { Field, Input } from '@/components/ui/input';
import { Select } from '@/components/ui/select';
import { useToast } from '@/components/ui/toast';
import { noFormErrors, toFormErrors, type FormErrors } from '@/lib/api/form-errors';
import { useAuth } from '@/features/auth/use-auth';
import { useOnboarding, useSaveOnboarding, useSkipOnboarding } from './api';

const USE_CASES = [
  ['BusinessVpn', 'Business VPN'],
  ['Msp', 'MSP / Managed Service Provider'],
  ['Consultant', 'Consultant'],
  ['Homelab', 'Homelab / Personal'],
  ['Education', 'Education'],
  ['Healthcare', 'Healthcare'],
  ['Government', 'Government'],
  ['SoftwareCompany', 'Software Company'],
  ['Other', 'Other'],
] as const;

const SIZES = ['1', '2–10', '11–50', '51–200', '201–1000', '1000+'];
const VPN_SOLUTIONS = ['OpenVPN', 'Tailscale', 'NetBird', 'ZeroTier', 'Fortinet', 'Cisco', 'Palo Alto', 'None', 'Other'];

/**
 * The post-signup "Tell us about your deployment" wizard. Entirely optional + skippable — it organises and
 * segments customers from the start, but never blocks getting into the product.
 */
export function WelcomeWizard() {
  const navigate = useNavigate();
  const toast = useToast();
  const { user, refresh } = useAuth();
  const { data } = useOnboarding();
  const save = useSaveOnboarding();
  const skip = useSkipOnboarding();

  const [useCase, setUseCase] = useState('BusinessVpn');
  const [companyName, setCompanyName] = useState('');
  const [companyWebsite, setCompanyWebsite] = useState('');
  const [industry, setIndustry] = useState('');
  const [teamSize, setTeamSize] = useState('');
  const [vpnUsers, setVpnUsers] = useState('');
  const [currentVpnSolution, setCurrentVpnSolution] = useState('');
  const [errors, setErrors] = useState<FormErrors>(noFormErrors);

  useEffect(() => {
    if (!data) return;
    if (data.useCase && data.useCase !== 'Unspecified') setUseCase(data.useCase);
    setCompanyName(data.companyName ?? '');
    setCompanyWebsite(data.companyWebsite ?? '');
    setIndustry(data.industry ?? '');
    setTeamSize(data.teamSize ?? '');
    setVpnUsers(data.vpnUsers ?? '');
    setCurrentVpnSolution(data.currentVpnSolution ?? '');
  }, [data]);

  async function finish(e: FormEvent) {
    e.preventDefault();
    setErrors(noFormErrors);
    save.mutate(
      {
        useCase,
        companyName: companyName.trim() || null,
        companyWebsite: companyWebsite.trim() || null,
        industry: industry.trim() || null,
        teamSize: teamSize || null,
        vpnUsers: vpnUsers || null,
        currentVpnSolution: currentVpnSolution || null,
      },
      {
        onSuccess: async () => {
          toast('Welcome to WireHQ — you’re all set.');
          await refresh();
          navigate('/app');
        },
        onError: (err) => setErrors(toFormErrors(err, 'Could not save. Please try again.')),
      },
    );
  }

  async function onSkip() {
    skip.mutate(undefined, {
      onSuccess: async () => {
        await refresh();
        navigate('/app');
      },
      onError: (err) => setErrors(toFormErrors(err, 'Could not skip. Please try again.')),
    });
  }

  const busy = save.isPending || skip.isPending;

  return (
    <div className="mx-auto max-w-2xl py-6">
      <div className="mb-6 text-center">
        <span className="mb-3 inline-flex items-center gap-2 rounded-full border border-gold-400/30 bg-gold-400/10 px-3 py-1 text-xs font-medium text-gold-700 dark:text-gold-300">
          <Sparkles className="size-3.5" /> Welcome{user?.firstName ? `, ${user.firstName}` : ''}
        </span>
        <h1 className="text-h1 text-ink-900 dark:text-ink-50">Tell us about your deployment</h1>
        <p className="mx-auto mt-2 max-w-md text-base text-ink-500">
          A few optional questions help us tailor WireHQ to you. You can skip this and do it later.
        </p>
      </div>

      <Card>
        <CardContent>
          <form onSubmit={finish} className="space-y-5">
            <Field label="What will you use WireHQ for?" htmlFor="useCase" error={errors.fields.useCase}>
              <Select id="useCase" value={useCase} onChange={(e) => setUseCase(e.target.value)}>
                {USE_CASES.map(([value, label]) => (
                  <option key={value} value={value}>{label}</option>
                ))}
              </Select>
            </Field>

            <div className="grid grid-cols-1 gap-4 sm:grid-cols-2">
              <Field label="Company name (optional)" htmlFor="companyName" error={errors.fields.companyName}>
                <Input id="companyName" value={companyName} onChange={(e) => setCompanyName(e.target.value)} placeholder="Acme Corp" />
              </Field>
              <Field label="Company website (optional)" htmlFor="companyWebsite" error={errors.fields.companyWebsite}>
                <Input id="companyWebsite" value={companyWebsite} onChange={(e) => setCompanyWebsite(e.target.value)} placeholder="https://acme.com" />
              </Field>
            </div>

            <div className="grid grid-cols-1 gap-4 sm:grid-cols-2">
              <Field label="Industry (optional)" htmlFor="industry" error={errors.fields.industry}>
                <Input id="industry" value={industry} onChange={(e) => setIndustry(e.target.value)} placeholder="e.g. Software, Healthcare" />
              </Field>
              <Field label="Current VPN solution (optional)" htmlFor="currentVpn" error={errors.fields.currentVpnSolution}>
                <Select id="currentVpn" value={currentVpnSolution} onChange={(e) => setCurrentVpnSolution(e.target.value)}>
                  <option value="">Select…</option>
                  {VPN_SOLUTIONS.map((s) => (
                    <option key={s} value={s}>{s}</option>
                  ))}
                </Select>
              </Field>
            </div>

            <div className="grid grid-cols-1 gap-4 sm:grid-cols-2">
              <Field label="Team size (optional)" htmlFor="teamSize" error={errors.fields.teamSize}>
                <Select id="teamSize" value={teamSize} onChange={(e) => setTeamSize(e.target.value)}>
                  <option value="">Select…</option>
                  {SIZES.map((s) => (
                    <option key={s} value={s}>{s}</option>
                  ))}
                </Select>
              </Field>
              <Field label="Expected VPN users (optional)" htmlFor="vpnUsers" error={errors.fields.vpnUsers}>
                <Select id="vpnUsers" value={vpnUsers} onChange={(e) => setVpnUsers(e.target.value)}>
                  <option value="">Select…</option>
                  {SIZES.map((s) => (
                    <option key={s} value={s}>{s}</option>
                  ))}
                </Select>
              </Field>
            </div>

            {errors.general && <p className="text-sm text-danger-600 dark:text-danger-500">{errors.general}</p>}

            <div className="flex items-center justify-between pt-2">
              <Button type="button" variant="ghost" onClick={onSkip} disabled={busy}>Skip for now</Button>
              <Button type="submit" disabled={busy}>
                {save.isPending ? 'Saving…' : 'Finish setup'} <ArrowRight />
              </Button>
            </div>
          </form>
        </CardContent>
      </Card>
    </div>
  );
}
