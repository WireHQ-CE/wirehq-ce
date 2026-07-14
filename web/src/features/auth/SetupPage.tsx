import { useState, type ChangeEvent, type FormEvent } from 'react';
import { Link, Navigate, useNavigate } from 'react-router-dom';
import { Button } from '@/components/ui/button';
import { Input, Field } from '@/components/ui/input';
import { PasswordStrength } from '@/components/ui/PasswordStrength';
import { ApiError } from '@/lib/api/client';
import { authApi } from './api';
import { AuthLayout } from './AuthLayout';
import { useAuth } from './use-auth';
import { useSecurityConfig } from './useCaptcha';

/**
 * Browser first-run setup for a fresh self-hosted instance: the first visitor creates the owner
 * account and organization, then is signed straight in. Only reachable while the API reports
 * setupRequired (Setup:Enabled + zero users) — the server enforces the same conditions, this page
 * is just the front door. (docs/17-community-edition.md)
 */
export function SetupPage() {
  const navigate = useNavigate();
  const { login } = useAuth();
  const { data: securityConfig } = useSecurityConfig();
  const [form, setForm] = useState({
    firstName: '',
    lastName: '',
    email: '',
    password: '',
    confirmPassword: '',
    organizationName: '',
  });
  const [errors, setErrors] = useState<Record<string, string[]>>({});
  const [error, setError] = useState<string | null>(null);
  const [submitting, setSubmitting] = useState(false);

  const set = (key: keyof typeof form) => (e: ChangeEvent<HTMLInputElement>) =>
    setForm({ ...form, [key]: e.target.value });

  // An already-claimed (or SaaS) instance has no setup to do — don't render a dead form.
  if (securityConfig && !securityConfig.setupRequired) {
    return <Navigate to="/login" replace />;
  }

  async function onSubmit(e: FormEvent) {
    e.preventDefault();
    setError(null);
    setErrors({});
    if (form.password !== form.confirmPassword) {
      setErrors({ confirmPassword: ["Passwords don't match."] });
      return;
    }
    setSubmitting(true);
    try {
      await authApi.setup({
        email: form.email,
        firstName: form.firstName,
        lastName: form.lastName,
        password: form.password,
        organizationName: form.organizationName.trim() || undefined,
      });
      await login(form.email, form.password);
      navigate('/app');
    } catch (err) {
      if (err instanceof ApiError) {
        setError(err.message);
        if (err.errors) setErrors(err.errors);
      } else {
        setError('Something went wrong. Please try again.');
      }
    } finally {
      setSubmitting(false);
    }
  }

  return (
    <AuthLayout
      title="Welcome to WireHQ"
      subtitle="Let's set up your instance — create the owner account to get started."
      footer={
        <>
          Already set up?{' '}
          <Link to="/login" className="text-gold-400 hover:underline">
            Sign in
          </Link>
        </>
      }
    >
      <form onSubmit={onSubmit} className="space-y-4">
        <div className="grid grid-cols-2 gap-3">
          <Field label="First name" htmlFor="firstName" error={errors.firstName?.[0]}>
            <Input id="firstName" autoComplete="given-name" autoFocus required value={form.firstName} onChange={set('firstName')} />
          </Field>
          <Field label="Last name" htmlFor="lastName" error={errors.lastName?.[0]}>
            <Input id="lastName" autoComplete="family-name" required value={form.lastName} onChange={set('lastName')} />
          </Field>
        </div>
        <Field label="Email" htmlFor="email" error={errors.email?.[0]}>
          <Input id="email" type="email" autoComplete="email" required value={form.email} onChange={set('email')} />
        </Field>
        <Field label="Password" htmlFor="password" error={errors.password?.[0]}>
          <Input id="password" type="password" autoComplete="new-password" required value={form.password} onChange={set('password')} />
          <PasswordStrength password={form.password} />
        </Field>
        <Field label="Confirm password" htmlFor="confirmPassword" error={errors.confirmPassword?.[0]}>
          <Input id="confirmPassword" type="password" autoComplete="new-password" required value={form.confirmPassword} onChange={set('confirmPassword')} />
        </Field>
        <Field label="Organization name" htmlFor="organizationName" error={errors.organizationName?.[0]}>
          <Input id="organizationName" placeholder="WireHQ" value={form.organizationName} onChange={set('organizationName')} />
          <p className="text-xs text-ink-400">Shown across the app — you can change it later in Settings.</p>
        </Field>

        {error && <p className="text-sm text-danger-500">{error}</p>}
        <Button type="submit" className="w-full" disabled={submitting}>
          {submitting ? 'Setting up…' : 'Create owner account'}
        </Button>
        <p className="text-xs text-ink-400">
          This one-time setup creates the instance owner. Once created, this page locks itself — more
          users are added by invitation.
        </p>
      </form>
    </AuthLayout>
  );
}
