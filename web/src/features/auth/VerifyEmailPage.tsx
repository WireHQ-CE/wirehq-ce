import { useEffect, useRef, useState } from 'react';
import { Link, useNavigate, useSearchParams } from 'react-router-dom';
import { Button } from '@/components/ui/button';
import { ApiError } from '@/lib/api/client';
import { AuthLayout } from './AuthLayout';
import { authApi } from './api';

type Status = 'verifying' | 'success' | 'error';

export function VerifyEmailPage() {
  const navigate = useNavigate();
  const [params] = useSearchParams();
  const token = params.get('token') ?? '';
  const [status, setStatus] = useState<Status>(token ? 'verifying' : 'error');
  const [message, setMessage] = useState<string | null>(token ? null : 'This verification link is missing its token.');
  const ran = useRef(false);

  useEffect(() => {
    if (!token || ran.current) return;
    ran.current = true; // guard against StrictMode double-invoke (the token is single-use)
    authApi
      .verifyEmail(token)
      .then(() => setStatus('success'))
      .catch((err) => {
        setStatus('error');
        setMessage(err instanceof ApiError ? err.message : 'This verification link is invalid or has expired.');
      });
  }, [token]);

  return (
    <AuthLayout
      title="Confirm your email"
      subtitle={
        status === 'verifying' ? 'Verifying your email…'
          : status === 'success' ? 'Your email is confirmed.'
            : 'We couldn’t confirm your email.'
      }
      footer={<Link to="/login" className="text-gold-400 hover:underline">Continue to sign in</Link>}
    >
      {status === 'verifying' && (
        <div className="h-10 animate-pulse rounded bg-ink-800" />
      )}
      {status === 'success' && (
        <div className="space-y-4">
          <p className="text-sm text-ink-300">Thanks — your email address is now verified. You can use all of WireHQ.</p>
          <Button className="w-full" onClick={() => navigate('/app')}>Go to WireHQ</Button>
        </div>
      )}
      {status === 'error' && (
        <p className="text-sm text-danger-500">{message}</p>
      )}
    </AuthLayout>
  );
}
