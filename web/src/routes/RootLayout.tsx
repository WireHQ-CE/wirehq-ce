import { Outlet } from 'react-router-dom';

/** Root layout for the Community Edition portal — no analytics, no RUM. */
export function RootLayout() {
  return <Outlet />;
}
