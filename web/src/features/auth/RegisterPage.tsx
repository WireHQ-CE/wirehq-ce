import { useState, type ChangeEvent, type FormEvent } from 'react';
import { Link, Navigate, useNavigate } from 'react-router-dom';
import { Button } from '@/components/ui/button';
import { Input, Field } from '@/components/ui/input';
import { PasswordStrength } from '@/components/ui/PasswordStrength';
import { ApiError } from '@/lib/api/client';
import { AuthLayout } from './AuthLayout';
import { useAuth } from './use-auth';
import { useCaptcha, useSecurityConfig } from './useCaptcha';

export function RegisterPage() {
  const navigate = useNavigate();
  const { register, login } = useAuth();
  const captcha = useCaptcha();
  const { data: securityConfig } = useSecurityConfig();
  const [form, setForm] = useState({ firstName: '', lastName: '', email: '', password: '' });
  const [acceptTerms, setAcceptTerms] = useState(false);
  const [errors, setErrors] = useState<Record<string, string[]>>({});
  const [error, setError] = useState<string | null>(null);
  const [submitting, setSubmitting] = useState(false);

  const set = (key: keyof typeof form) => (e: ChangeEvent<HTMLInputElement>) =>
    setForm({ ...form, [key]: e.target.value });

  async function onSubmit(e: FormEvent) {
    e.preventDefault();
    setError(null);
    setErrors({});
    setSubmitting(true);
    try {
      await register({ ...form, acceptTerms, turnstileToken: captcha.token });
      if (captcha.required) {
        // The Turnstile token was consumed by register; sign in fresh on the login page.
        navigate('/login');
      } else {
        await login(form.email, form.password);
        navigate('/app'); // AppHome routes first-time users into the Welcome Wizard.
      }
    } catch (err) {
      captcha.reset();
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

  // A fresh, ownerless self-hosted instance: first-run setup comes before any signup.
  if (securityConfig?.setupRequired) {
    return <Navigate to="/setup" replace />;
  }

  // Invite-only (self-hosted) installs: replace the form with a notice. The API rejects register
  // calls regardless — this is UX, not the enforcement point.
  if (securityConfig?.registrationEnabled === false) {
    return (
      <AuthLayout
        title="Registration is closed"
        subtitle="This WireHQ instance is invite-only."
        footer={
          <>
            Already have an account?{' '}
            <Link to="/login" className="text-gold-400 hover:underline">
              Sign in
            </Link>
          </>
        }
      >
        <p className="text-sm text-ink-300">
          Self-serve signup is disabled on this instance. Ask an administrator to invite you — you'll
          receive an email with a link to set your password.
        </p>
      </AuthLayout>
    );
  }

  return (
    <AuthLayout
      title="Create your account"
      subtitle="Get started with WireHQ in seconds — no credit card, no company details required."
      footer={
        <>
          Already have an account?{' '}
          <Link to="/login" className="text-gold-400 hover:underline">
            Sign in
          </Link>
        </>
      }
    >
      <form onSubmit={onSubmit} className="space-y-4">
        <div className="grid grid-cols-2 gap-3">
          <Field label="First name" htmlFor="firstName" error={errors.firstName?.[0]}>
            <Input id="firstName" autoComplete="given-name" required value={form.firstName} onChange={set('firstName')} />
          </Field>
          <Field label="Last name" htmlFor="lastName" error={errors.lastName?.[0]}>
            <Input id="lastName" autoComplete="family-name" required value={form.lastName} onChange={set('lastName')} />
          </Field>
        </div>
        <Field label="Work email" htmlFor="email" error={errors.email?.[0]}>
          <Input id="email" type="email" autoComplete="email" required value={form.email} onChange={set('email')} />
        </Field>
        <Field label="Password" htmlFor="password" error={errors.password?.[0]}>
          <Input id="password" type="password" autoComplete="new-password" required value={form.password} onChange={set('password')} />
          <PasswordStrength password={form.password} />
        </Field>

        <label className="flex items-start gap-2.5 text-sm text-ink-300">
          <input
            type="checkbox"
            checked={acceptTerms}
            onChange={(e) => setAcceptTerms(e.target.checked)}
            className="mt-0.5 size-4 shrink-0 rounded border-ink-600 bg-ink-900 text-gold-500 focus:ring-gold-400/60"
          />
          <span>
            I agree to the{' '}
            <Link to="/terms-of-service" target="_blank" className="text-gold-400 hover:underline">Terms of Service</Link>
            {' '}and{' '}
            <Link to="/cookie-policy" target="_blank" className="text-gold-400 hover:underline">Cookie Policy</Link>.
          </span>
        </label>
        {errors.acceptTerms?.[0] && <p className="text-sm text-danger-500">{errors.acceptTerms[0]}</p>}

        {captcha.widget}
        {error && <p className="text-sm text-danger-500">{error}</p>}
        <Button type="submit" className="w-full" disabled={submitting || !acceptTerms || (captcha.required && !captcha.token)}>
          {submitting ? 'Creating account…' : 'Create account'}
        </Button>
      </form>
    </AuthLayout>
  );
}
