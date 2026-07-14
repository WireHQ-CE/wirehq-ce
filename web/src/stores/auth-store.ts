import { create } from 'zustand';

export interface MembershipSummary {
  organizationId: string;
  slug: string;
  name: string;
  status: string;
}

export interface ImpersonatorSummary {
  userId: string;
  name: string;
  email: string;
  /** When the time-boxed impersonation session expires (ADR-032); null if it can't be resolved. */
  expiresAtUtc: string | null;
}

/** The active org's plan + what it includes — drives feature-gating in the UI. (docs/commercial.md) */
export interface Entitlements {
  plan: string;
  features: string[];
  /** Per-resource caps (e.g. instances, peers, gateways, seats). -1 = unlimited. */
  limits: Record<string, number>;
}

/** The active org's subscription status — drives the trial countdown + past-due banner. (docs/commercial.md §6.4) */
export interface BillingSummary {
  /** None | Trialing | Active | PastDue | Canceled */
  status: string;
  trialEndUtc: string | null;
  currentPeriodEndUtc: string | null;
  graceEndsUtc: string | null;
}

export interface CurrentUser {
  userId: string;
  email: string;
  name: string;
  firstName: string | null;
  lastName: string | null;
  username: string | null;
  jobTitle: string | null;
  phone: string | null;
  timezone: string | null;
  language: string | null;
  avatarUrl: string | null;
  mfaEnabled: boolean;
  emailVerified: boolean;
  activeOrganizationId: string | null;
  organizations: MembershipSummary[];
  permissions: string[];
  /** Platform-operator role (e.g. 'SuperAdmin'), or null for ordinary users. */
  platformRole: string | null;
  /** True when the active org still has the Welcome Wizard pending (drives the first-login redirect). */
  onboardingPending: boolean;
  /** When impersonating, the operator acting as this account; null otherwise. */
  impersonatedBy: ImpersonatorSummary | null;
  /** The active org's plan entitlements (feature flags + limits). */
  entitlements: Entitlements;
  /** The active org's subscription/billing status (trial + past-due affordances). */
  billing: BillingSummary;
}

interface AuthState {
  accessToken: string | null;
  user: CurrentUser | null;
  /** 'loading' until the first /me resolves, so guards don't flash the login page. */
  status: 'loading' | 'authenticated' | 'unauthenticated';
  setAccessToken: (token: string | null) => void;
  setUser: (user: CurrentUser | null) => void;
  hasPermission: (permission: string) => boolean;
  /** True when the active org's plan includes the feature (see docs/commercial.md). */
  hasFeature: (feature: string) => boolean;
  reset: () => void;
}

/**
 * Client/session state only. The access token lives in memory (never localStorage — XSS
 * resistance); the refresh token is an HttpOnly cookie the browser sends automatically. Server
 * data (lists, entities) belongs in TanStack Query, not here. (docs/08-frontend-structure.md)
 */
export const useAuthStore = create<AuthState>((set, get) => ({
  accessToken: null,
  user: null,
  status: 'loading',
  setAccessToken: (token) => set({ accessToken: token }),
  setUser: (user) =>
    set({ user, status: user ? 'authenticated' : 'unauthenticated' }),
  hasPermission: (permission) => get().user?.permissions.includes(permission) ?? false,
  hasFeature: (feature) => get().user?.entitlements?.features.includes(feature) ?? false,
  reset: () => set({ accessToken: null, user: null, status: 'unauthenticated' }),
}));
