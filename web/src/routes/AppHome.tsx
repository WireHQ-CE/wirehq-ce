import { Navigate } from 'react-router-dom';
import { useAuthStore } from '@/stores/auth-store';
import { DashboardPage } from '@/features/dashboard/DashboardPage';

/** The `/app` index — the org dashboard (Community Edition has no platform/super-admin tier). */
export function AppHome() {
  const user = useAuthStore((s) => s.user);

  // First login for a new self-hosted org → run the Welcome Wizard before the dashboard.
  if (user?.onboardingPending) {
    return <Navigate to="/app/welcome" replace />;
  }

  return <DashboardPage />;
}
