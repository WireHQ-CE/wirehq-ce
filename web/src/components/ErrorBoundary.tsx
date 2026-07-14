import { Component, type ErrorInfo, type ReactNode } from 'react';
import { ErrorFallback } from '@/components/ErrorFallback';
import { getCorrelationRef, reportClientError } from '@/lib/observability/report';

interface Props {
  children: ReactNode;
}

interface State {
  hasError: boolean;
  reference?: string;
}

/**
 * Root error boundary — the last line of defence for render errors that escape React Router's
 * per-route `errorElement` (e.g. errors in providers or layout above the router). Reports the error
 * to the observability seam and shows the friendly fallback with a quotable correlation reference.
 * React requires a class component for `componentDidCatch` (docs/15 §12).
 */
export class ErrorBoundary extends Component<Props, State> {
  state: State = { hasError: false };

  static getDerivedStateFromError(): State {
    return { hasError: true, reference: getCorrelationRef() };
  }

  componentDidCatch(error: Error, info: ErrorInfo): void {
    reportClientError(error, { source: 'error-boundary', componentStack: info.componentStack });
  }

  private reset = () => {
    // A render error usually leaves stale state; a full reload is the safe recovery at the root.
    window.location.reload();
  };

  render(): ReactNode {
    if (this.state.hasError) {
      return <ErrorFallback reference={this.state.reference} onReload={this.reset} fullScreen />;
    }
    return this.props.children;
  }
}
