import { useState, type FormEvent } from 'react';
import { Link, useNavigate, useSearchParams } from 'react-router-dom';
import { Button } from '@/components/ui/button';
import { Input, Field } from '@/components/ui/input';
import { ApiError } from '@/lib/api/client';
import { AuthLayout } from './AuthLayout';
import { authApi } from './api';
import { useCaptcha } from './useCaptcha';

export function ResetPasswordPage() {
  const navigate = useNavigate();
  const [params] = useSearchParams();
  const token = params.get('token') ?? '';
  const captcha = useCaptcha();
  const [password, setPassword] = useState('');
  const [error, setError] = useState<string | null>(null);
  const [errors, setErrors] = useState<Record<string, string[]>>({});
  const [submitting, setSubmitting] = useState(false);

  async function onSubmit(e: FormEvent) {
    e.preventDefault();
    setError(null);
    setErrors({});
    setSubmitting(true);
    try {
      await authApi.resetPassword(token, password, captcha.token);
      navigate('/login', { replace: true });
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

  return (
    <AuthLayout
      title="Choose a new password"
      subtitle="Set a new password for your account."
      footer={
        <Link to="/login" className="text-gold-400 hover:underline">
          Back to sign in
        </Link>
      }
    >
      {!token ? (
        <p className="text-sm text-danger-500">This reset link is missing its token. Request a new one.</p>
      ) : (
        <form onSubmit={onSubmit} className="space-y-4">
          <Field label="New password" htmlFor="password" error={errors.newPassword?.[0]}>
            <Input id="password" type="password" autoComplete="new-password" required value={password} onChange={(e) => setPassword(e.target.value)} />
          </Field>
          {captcha.widget}
          {error && <p className="text-sm text-danger-500">{error}</p>}
          <Button type="submit" className="w-full" disabled={submitting || (captcha.required && !captcha.token)}>
            {submitting ? 'Updating…' : 'Update password'}
          </Button>
        </form>
      )}
    </AuthLayout>
  );
}
