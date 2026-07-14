import { useState, type FormEvent } from 'react';
import { Link, Navigate, useNavigate, useSearchParams } from 'react-router-dom';
import { Button } from '@/components/ui/button';
import { Input, Field } from '@/components/ui/input';
import { noFormErrors, toFormErrors, type FormErrors } from '@/lib/api/form-errors';
import { EDITION } from '@/lib/edition';
import { AuthLayout } from './AuthLayout';
import { useAuth } from './use-auth';
import { useCaptcha, useSecurityConfig } from './useCaptcha';

export function LoginPage() {
  const navigate = useNavigate();
  const { login, verifyMfa } = useAuth();
  const captcha = useCaptcha();
  const { data: securityConfig } = useSecurityConfig();
  // Hide signup only when the instance explicitly runs invite-only, so the link doesn't flicker
  // out while the config loads on open-registration (SaaS) installs.
  const registrationClosed = securityConfig?.registrationEnabled === false;
  const [email, setEmail] = useState('');
  const [password, setPassword] = useState('');
  const [mfaStep, setMfaStep] = useState(false);
  const [code, setCode] = useState('');
  const [errors, setErrors] = useState<FormErrors>(noFormErrors);
  const [submitting, setSubmitting] = useState(false);
  // Enterprise SSO (SaaS only): a work-email/org entry that hands off to the org's IdP. Also surfaces the
  // ?sso_error flag the callback redirects to when single sign-on fails.
  const [params] = useSearchParams();
  const ssoFailed = params.get('sso_error') !== null;
  const [ssoMode, setSsoMode] = useState(false);
  const [ssoOrg, setSsoOrg] = useState('');

  function startSso(e: FormEvent) {
    e.preventDefault();
    const slug = ssoOrg.trim().toLowerCase();
    if (slug) {
      window.location.href = `/api/v1/auth/sso/${encodeURIComponent(slug)}/start`;
    }
  }

  // A fresh, ownerless self-hosted instance has nobody to sign in — route to first-run setup.
  // (After every hook: an early return above a hook would break the rules of hooks.)
  if (securityConfig?.setupRequired) {
    return <Navigate to="/setup" replace />;
  }

  async function onSubmit(e: FormEvent) {
    e.preventDefault();
    setErrors(noFormErrors);
    setSubmitting(true);
    try {
      if (mfaStep) {
        await verifyMfa(code.trim());
        navigate('/app');
        return;
      }
      const { mfaRequired } = await login(email, password, captcha.token);
      if (mfaRequired) {
        setMfaStep(true);
      } else {
        navigate('/app');
      }
    } catch (err) {
      captcha.reset();
      setErrors(toFormErrors(err, 'Something went wrong. Please try again.'));
    } finally {
      setSubmitting(false);
    }
  }

  if (mfaStep) {
    return (
      <AuthLayout
        title="Two-factor authentication"
        subtitle="Enter the 6-digit code from your authenticator app, or a recovery code."
        footer={
          <button className="text-gold-400 hover:underline" onClick={() => setMfaStep(false)}>
            Back to sign in
          </button>
        }
      >
        <form onSubmit={onSubmit} className="space-y-4">
          <Field label="Authentication code" htmlFor="code" error={errors.fields.code}>
            <Input
              id="code"
              inputMode="text"
              autoComplete="one-time-code"
              autoFocus
              placeholder="123456"
              className="font-mono tracking-widest"
              value={code}
              onChange={(e) => setCode(e.target.value)}
            />
          </Field>
          {errors.general && <p className="text-sm text-danger-500">{errors.general}</p>}
          <Button type="submit" className="w-full" disabled={submitting}>
            {submitting ? 'Verifying…' : 'Verify'}
          </Button>
        </form>
      </AuthLayout>
    );
  }

  return (
    <AuthLayout
      title="Sign in"
      subtitle="Welcome back to your WireGuard control plane."
      footer={
        registrationClosed ? undefined : (
          <>
            No account?{' '}
            <Link to="/register" className="text-gold-400 hover:underline">
              Create one
            </Link>
          </>
        )
      }
    >
      <>
      {ssoFailed && (
        <p className="mb-4 rounded-lg bg-danger-500/10 px-3 py-2 text-sm text-danger-500">
          Single sign-on didn’t complete. Try again, or sign in with your email and password.
        </p>
      )}
      <form onSubmit={onSubmit} className="space-y-4">
        <Field label="Email" htmlFor="email" error={errors.fields.email}>
          <Input id="email" type="email" autoComplete="email" required value={email} onChange={(e) => setEmail(e.target.value)} />
        </Field>
        <Field label="Password" htmlFor="password" error={errors.fields.password}>
          <Input id="password" type="password" autoComplete="current-password" required value={password} onChange={(e) => setPassword(e.target.value)} />
        </Field>
        {captcha.widget}
        {errors.general && <p className="text-sm text-danger-500">{errors.general}</p>}
        <Button type="submit" className="w-full" disabled={submitting || (captcha.required && !captcha.token)}>
          {submitting ? 'Signing in…' : 'Sign in'}
        </Button>
        <div className="text-center">
          <Link to="/forgot-password" className="text-sm text-ink-400 hover:text-ink-200">
            Forgot password?
          </Link>
        </div>
      </form>
      {EDITION === 'saas' && (
        <div className="mt-6 border-t border-ink-800 pt-6">
          {ssoMode ? (
            <form onSubmit={startSso} className="space-y-3">
              <Field label="Organization" htmlFor="sso-org">
                <Input
                  id="sso-org"
                  autoFocus
                  placeholder="your-company"
                  value={ssoOrg}
                  onChange={(e) => setSsoOrg(e.target.value)}
                />
              </Field>
              <Button type="submit" variant="secondary" className="w-full" disabled={!ssoOrg.trim()}>
                Continue with SSO
              </Button>
              <button
                type="button"
                className="block w-full text-center text-sm text-ink-400 hover:text-ink-200"
                onClick={() => setSsoMode(false)}
              >
                Back
              </button>
            </form>
          ) : (
            <Button type="button" variant="secondary" className="w-full" onClick={() => setSsoMode(true)}>
              Sign in with SSO
            </Button>
          )}
        </div>
      )}
      </>
    </AuthLayout>
  );
}
