import { useEffect } from 'react';
import { useRouteError } from 'react-router-dom';
import { ErrorFallback } from '@/components/ErrorFallback';
import { ApiError } from '@/lib/api/client';
import { getCorrelationRef, reportClientError } from '@/lib/observability/report';

/**
 * Route-level error boundary (React Router `errorElement`). Catches render/loader errors thrown inside
 * a route so the rest of the app shell (sidebar, nav) stays usable, reports them to the observability
 * seam, and shows the friendly fallback with a quotable correlation reference (docs/15 §12, ADR-030).
 */
export function RouteErrorBoundary() {
  const error = useRouteError();
  // Prefer the failing request's own correlation id when the error came from the api client.
  const reference = error instanceof ApiError ? error.correlationId : getCorrelationRef(error);

  useEffect(() => {
    reportClientError(error, { source: 'route' });
  }, [error]);

  return <ErrorFallback reference={reference} onReload={() => window.location.reload()} />;
}
