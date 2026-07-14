import { useState, type FormEvent } from 'react';
import { Link } from 'react-router-dom';
import { Button } from '@/components/ui/button';
import { Input, Field } from '@/components/ui/input';
import { AuthLayout } from './AuthLayout';
import { authApi } from './api';
import { useCaptcha } from './useCaptcha';

export function ForgotPasswordPage() {
  const captcha = useCaptcha();
  const [email, setEmail] = useState('');
  const [sent, setSent] = useState(false);
  const [submitting, setSubmitting] = useState(false);

  async function onSubmit(e: FormEvent) {
    e.preventDefault();
    setSubmitting(true);
    try {
      await authApi.forgotPassword(email, captcha.token);
    } finally {
      // Always show the same confirmation (no account enumeration).
      setSent(true);
      setSubmitting(false);
    }
  }

  return (
    <AuthLayout
      title="Reset your password"
      subtitle={sent ? 'Check your inbox.' : 'Enter your email and we’ll send a reset link.'}
      footer={
        <Link to="/login" className="text-gold-400 hover:underline">
          Back to sign in
        </Link>
      }
    >
      {sent ? (
        <p className="text-sm text-ink-300">
          If an account exists for <span className="font-medium text-ink-100">{email}</span>, a
          password-reset link is on its way. The link is valid for one hour.
        </p>
      ) : (
        <form onSubmit={onSubmit} className="space-y-4">
          <Field label="Email" htmlFor="email">
            <Input id="email" type="email" autoComplete="email" required value={email} onChange={(e) => setEmail(e.target.value)} />
          </Field>
          {captcha.widget}
          <Button type="submit" className="w-full" disabled={submitting || (captcha.required && !captcha.token)}>
            {submitting ? 'Sending…' : 'Send reset link'}
          </Button>
        </form>
      )}
    </AuthLayout>
  );
}
