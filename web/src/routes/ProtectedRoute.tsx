import { Navigate, Outlet } from 'react-router-dom';
import { useAuthStore } from '@/stores/auth-store';
import { Mark } from '@/components/brand/Logo';

/** Gates the authenticated app. Shows a splash while the session resolves to avoid a login flash. */
export function ProtectedRoute() {
  const status = useAuthStore((s) => s.status);

  if (status === 'loading') {
    return (
      <div className="grid h-screen place-items-center bg-ink-950">
        <Mark className="h-9 w-auto animate-pulse" />
      </div>
    );
  }

  if (status === 'unauthenticated') {
    return <Navigate to="/login" replace />;
  }

  return <Outlet />;
}
