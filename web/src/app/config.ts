/** Runtime config. API base is empty by default so requests hit the same origin (dev proxy / prod ingress). */
export const config = {
  apiBaseUrl: import.meta.env.VITE_API_BASE_URL ?? '',
  appName: 'WireHQ',
};
