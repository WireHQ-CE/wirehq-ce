import { createBrowserRouter, Navigate } from 'react-router-dom';
import { RootLayout } from './RootLayout';
import { RouteErrorBoundary } from './RouteErrorBoundary';
import { AppShell } from '@/components/layout/AppShell';
import { ProtectedRoute } from './ProtectedRoute';
import { AppHome } from './AppHome';
import { WelcomeWizard } from '@/features/onboarding/WelcomeWizard';
import { LoginPage } from '@/features/auth/LoginPage';
import { SetupPage } from '@/features/auth/SetupPage';
import { RegisterPage } from '@/features/auth/RegisterPage';
import { ForgotPasswordPage } from '@/features/auth/ForgotPasswordPage';
import { ResetPasswordPage } from '@/features/auth/ResetPasswordPage';
import { VerifyEmailPage } from '@/features/auth/VerifyEmailPage';
import { UsersPage } from '@/features/users/UsersPage';
import { AuditPage } from '@/features/audit/AuditPage';
import { ModulesPage } from '@/features/modules/ModulesPage';
import { BrandingPage } from '@/features/branding/BrandingPage';
import { OrganizationPage } from '@/features/organizations/OrganizationPage';
import { ProfilePage } from '@/features/account/ProfilePage';
import { SecurityPage } from '@/features/account/SecurityPage';
import { NotificationsPage } from '@/features/account/NotificationsPage';
import { TeamsPage } from '@/features/teams/TeamsPage';
import { TeamDetailPage } from '@/features/teams/TeamDetailPage';
import { WireGuardPage } from '@/features/wireguard/WireGuardPage';
import { InstanceDetailPage } from '@/features/wireguard/InstanceDetailPage';
import { RolesSettingsPage } from '@/features/roles/RolesSettingsPage';
import { ApiKeysSettingsPage } from '@/features/api-keys/ApiKeysSettingsPage';
import { WebhooksSettingsPage } from '@/features/webhooks/WebhooksSettingsPage';
import { NotificationsSettingsPage } from '@/features/notifications/NotificationsSettingsPage';

// WireHQ Community Edition — a self-hosted WireGuard management portal. No public marketing site, no
// platform/super-admin tier, no billing: the app is the login + the /app portal.
export const router = createBrowserRouter([
  {
    element: <RootLayout />,
    errorElement: <RouteErrorBoundary />,
    children: [
      { path: '/', element: <Navigate to="/app" replace /> },
      { path: '/setup', element: <SetupPage /> },
      { path: '/login', element: <LoginPage /> },
      { path: '/register', element: <RegisterPage /> },
      { path: '/forgot-password', element: <ForgotPasswordPage /> },
      { path: '/reset-password', element: <ResetPasswordPage /> },
      { path: '/verify-email', element: <VerifyEmailPage /> },
      {
        element: <ProtectedRoute />,
        children: [
          {
            path: '/app',
            element: <AppShell />,
            children: [{
              errorElement: <RouteErrorBoundary />,
              children: [
                { index: true, element: <AppHome /> },
                { path: 'welcome', element: <WelcomeWizard /> },
                { path: 'organization', element: <OrganizationPage /> },
                { path: 'teams', element: <TeamsPage /> },
                { path: 'teams/:id', element: <TeamDetailPage /> },
                { path: 'users', element: <UsersPage /> },
                { path: 'wireguard', element: <WireGuardPage /> },
                { path: 'wireguard/instances/:id', element: <InstanceDetailPage /> },
                { path: 'audit', element: <AuditPage /> },
                { path: 'modules', element: <ModulesPage /> },
                { path: 'settings', element: <ProfilePage /> },
                { path: 'settings/security', element: <SecurityPage /> },
                { path: 'settings/roles', element: <RolesSettingsPage /> },
                { path: 'settings/api-keys', element: <ApiKeysSettingsPage /> },
                { path: 'settings/webhooks', element: <WebhooksSettingsPage /> },
                { path: 'settings/notification-rules', element: <NotificationsSettingsPage /> },
                { path: 'settings/notifications', element: <NotificationsPage /> },
                { path: 'settings/branding', element: <BrandingPage /> },
              ],
            }],
          },
        ],
      },
    ],
  },
]);
