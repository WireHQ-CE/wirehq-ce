import { createContext, useCallback, useContext, useState, type ReactNode } from 'react';
import { createPortal } from 'react-dom';
import { AlertCircle, CheckCircle2, Copy, Info, X } from 'lucide-react';
import { cn } from '@/lib/utils/cn';
import { getCorrelationRef } from '@/lib/observability/report';

type ToastTone = 'success' | 'error' | 'info';

interface ToastOptions {
  /** A correlation reference to show under the message so the user can quote it to support (ADR-030). */
  reference?: string;
}

interface ToastItem {
  id: number;
  message: string;
  tone: ToastTone;
  reference?: string;
}

interface ToastContextValue {
  toast: (message: string, tone?: ToastTone, options?: ToastOptions) => void;
}

const ToastContext = createContext<ToastContextValue | null>(null);

/** App-wide toast host. Replaces the old window.alert/prompt feedback pattern. */
export function ToastProvider({ children }: { children: ReactNode }) {
  const [toasts, setToasts] = useState<ToastItem[]>([]);

  const remove = useCallback((id: number) => {
    setToasts((current) => current.filter((t) => t.id !== id));
  }, []);

  const toast = useCallback(
    (message: string, tone: ToastTone = 'success', options?: ToastOptions) => {
      const id = Date.now() + Math.random();
      // Error toasts get the support reference automatically — the failing request's correlation id —
      // so every error a user sees is quotable, without each call site threading it through (ADR-030).
      const reference = options?.reference ?? (tone === 'error' ? getCorrelationRef() : undefined);
      setToasts((current) => [...current, { id, message, tone, reference }]);
      setTimeout(() => remove(id), 4500);
    },
    [remove],
  );

  return (
    <ToastContext.Provider value={{ toast }}>
      {children}
      {createPortal(
        <div className="pointer-events-none fixed bottom-4 right-4 z-[60] flex w-[22rem] max-w-[calc(100vw-2rem)] flex-col gap-2">
          {toasts.map((t) => (
            <div
              key={t.id}
              className={cn(
                'pointer-events-auto flex items-start gap-2.5 rounded-lg border bg-ink-0 p-3 shadow-e2 dark:border-ink-800 dark:bg-ink-850',
              )}
            >
              {t.tone === 'success' && <CheckCircle2 className="size-4 shrink-0 text-success-600" />}
              {t.tone === 'error' && <AlertCircle className="size-4 shrink-0 text-danger-600" />}
              {t.tone === 'info' && <Info className="size-4 shrink-0 text-info-600" />}
              <div className="min-w-0 flex-1">
                <p className="text-sm text-ink-800 dark:text-ink-100">{t.message}</p>
                {t.reference && <CorrelationRef reference={t.reference} />}
              </div>
              <button
                type="button"
                onClick={() => remove(t.id)}
                aria-label="Dismiss"
                className="text-ink-400 transition-colors hover:text-ink-700 dark:hover:text-ink-200"
              >
                <X className="size-3.5" />
              </button>
            </div>
          ))}
        </div>,
        document.body,
      )}
    </ToastContext.Provider>
  );
}

/** A small, copyable support reference shown under an error message. */
function CorrelationRef({ reference }: { reference: string }) {
  const [copied, setCopied] = useState(false);
  return (
    <button
      type="button"
      onClick={() => {
        void navigator.clipboard?.writeText(reference);
        setCopied(true);
        setTimeout(() => setCopied(false), 1500);
      }}
      title="Copy this reference for support"
      className="mt-1 flex max-w-full items-center gap-1 font-mono text-[11px] text-ink-400 transition-colors hover:text-ink-600 dark:hover:text-ink-300"
    >
      <Copy className="size-3 shrink-0" />
      <span className="truncate">{copied ? 'Copied' : `Ref: ${reference}`}</span>
    </button>
  );
}

// eslint-disable-next-line react-refresh/only-export-components -- hook colocated with its provider
export function useToast() {
  const ctx = useContext(ToastContext);
  if (!ctx) throw new Error('useToast must be used within a ToastProvider');
  return ctx.toast;
}
