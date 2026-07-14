import { config } from '@/app/config';
import { useAuthStore } from '@/stores/auth-store';
import { setLastCorrelationId } from '@/lib/observability/report';

/** Typed error parsed from an RFC 9457 problem+json response. */
export class ApiError extends Error {
  constructor(
    public readonly status: number,
    public readonly code: string,
    message: string,
    public readonly errors?: Record<string, string[]>,
    /** The W3C trace id echoed by the API (`X-Correlation-Id`) — the reference a user quotes to support. */
    public readonly correlationId?: string,
  ) {
    super(message);
    this.name = 'ApiError';
  }
}

/** The correlation reference the API echoes on every response (ADR-030); captured for support + reporting. */
const CORRELATION_HEADER = 'X-Correlation-Id';

/** Record the response's correlation id (for global error reporting) and return it for the caller. */
function captureCorrelation(response: Response): string | undefined {
  const id = response.headers.get(CORRELATION_HEADER) ?? undefined;
  setLastCorrelationId(id);
  return id;
}

/**
 * The user-facing message for a problem+json payload. Validation failures carry a per-field `errors`
 * map (e.g. `{ cidr: ["CIDR must look like 10.8.0.0/24."] }`) — surface those specific messages rather
 * than the generic "One or more validation errors occurred." title, so the user can tell what to fix.
 */
function problemMessage(payload: { title?: unknown; errors?: unknown } | undefined, fallback: string): string {
  const errors = payload?.errors;
  if (errors && typeof errors === 'object') {
    const flat = Object.values(errors as Record<string, string[]>).flat().filter(Boolean);
    if (flat.length > 0) return flat.join(' ');
  }
  return (payload?.title as string) ?? fallback;
}

interface RequestOptions extends Omit<RequestInit, 'body'> {
  body?: unknown;
  /** Internal: prevents infinite refresh loops. */
  _retry?: boolean;
}

let refreshPromise: Promise<boolean> | null = null;

async function refreshAccessToken(): Promise<boolean> {
  // Share a single in-flight refresh across concurrent 401s.
  refreshPromise ??= (async () => {
    try {
      const res = await fetch(`${config.apiBaseUrl}/api/v1/auth/refresh`, {
        method: 'POST',
        credentials: 'include',
      });
      if (!res.ok) return false;
      const data = (await res.json()) as { accessToken: string };
      useAuthStore.getState().setAccessToken(data.accessToken);
      return true;
    } catch {
      return false;
    } finally {
      refreshPromise = null;
    }
  })();

  return refreshPromise;
}

export async function apiFetch<T>(path: string, options: RequestOptions = {}): Promise<T> {
  const { body, headers, _retry, ...rest } = options;
  const accessToken = useAuthStore.getState().accessToken;

  const response = await fetch(`${config.apiBaseUrl}${path}`, {
    ...rest,
    credentials: 'include',
    headers: {
      'Content-Type': 'application/json',
      ...(accessToken ? { Authorization: `Bearer ${accessToken}` } : {}),
      ...headers,
    },
    body: body === undefined ? undefined : JSON.stringify(body),
  });

  // Transparently refresh once on an expired/invalid access token, then retry.
  if (response.status === 401 && !_retry && (await refreshAccessToken())) {
    return apiFetch<T>(path, { ...options, _retry: true });
  }

  const correlationId = captureCorrelation(response);

  if (response.status === 204) {
    return undefined as T;
  }

  const payload = response.headers.get('content-type')?.includes('json')
    ? await response.json()
    : undefined;

  if (!response.ok) {
    const code = (payload?.code as string) ?? 'error';
    const message = problemMessage(payload, response.statusText);
    throw new ApiError(response.status, code, message, payload?.errors, correlationId);
  }

  return payload as T;
}

/**
 * Authenticated binary GET (peer config downloads, QR PNGs). These endpoints require the bearer
 * header, so a plain `<a download>` won't work — fetch with the token, then build a blob URL.
 * Shares the same single-flight refresh-and-retry as {@link apiFetch}.
 */
export async function apiFetchBlob(path: string, _retry = false): Promise<Blob> {
  const accessToken = useAuthStore.getState().accessToken;

  const response = await fetch(`${config.apiBaseUrl}${path}`, {
    method: 'GET',
    credentials: 'include',
    headers: accessToken ? { Authorization: `Bearer ${accessToken}` } : {},
  });

  if (response.status === 401 && !_retry && (await refreshAccessToken())) {
    return apiFetchBlob(path, true);
  }

  const correlationId = captureCorrelation(response);

  if (!response.ok) {
    throw new ApiError(response.status, 'error', response.statusText, undefined, correlationId);
  }

  return response.blob();
}

/**
 * Authenticated multipart POST (e.g. CSV bulk-enrollment upload). The browser sets the multipart
 * boundary, so we must NOT set Content-Type here. Shares the single-flight refresh-and-retry.
 */
export async function apiUpload<T>(path: string, formData: FormData, _retry = false): Promise<T> {
  const accessToken = useAuthStore.getState().accessToken;

  const response = await fetch(`${config.apiBaseUrl}${path}`, {
    method: 'POST',
    credentials: 'include',
    headers: accessToken ? { Authorization: `Bearer ${accessToken}` } : {},
    body: formData,
  });

  if (response.status === 401 && !_retry && (await refreshAccessToken())) {
    return apiUpload<T>(path, formData, true);
  }

  const correlationId = captureCorrelation(response);

  if (response.status === 204) {
    return undefined as T;
  }

  const payload = response.headers.get('content-type')?.includes('json')
    ? await response.json()
    : undefined;

  if (!response.ok) {
    const code = (payload?.code as string) ?? 'error';
    const message = problemMessage(payload, response.statusText);
    throw new ApiError(response.status, code, message, payload?.errors, correlationId);
  }

  return payload as T;
}

export const api = {
  get: <T>(path: string) => apiFetch<T>(path, { method: 'GET' }),
  post: <T>(path: string, body?: unknown) => apiFetch<T>(path, { method: 'POST', body }),
  put: <T>(path: string, body?: unknown) => apiFetch<T>(path, { method: 'PUT', body }),
  patch: <T>(path: string, body?: unknown) => apiFetch<T>(path, { method: 'PATCH', body }),
  delete: <T>(path: string) => apiFetch<T>(path, { method: 'DELETE' }),
  blob: (path: string) => apiFetchBlob(path),
  upload: <T>(path: string, formData: FormData) => apiUpload<T>(path, formData),
};
