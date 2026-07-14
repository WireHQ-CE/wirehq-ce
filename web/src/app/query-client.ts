import { MutationCache, QueryCache, QueryClient } from '@tanstack/react-query';
import { reportClientError } from '@/lib/observability/report';

// Central capture for every query/mutation failure (docs/15 §12). Components still show their own
// toasts; this is the single reporting seam so no failure goes unobserved, each tagged with its
// correlation reference, route and build. A 401 that the api client transparently refreshes never
// reaches here — only real, surfaced errors do.
export const queryClient = new QueryClient({
  queryCache: new QueryCache({
    onError: (error, query) => {
      reportClientError(error, { source: 'query', queryKey: query.queryKey });
    },
  }),
  mutationCache: new MutationCache({
    onError: (error, _variables, _context, mutation) => {
      reportClientError(error, { source: 'mutation', mutationKey: mutation.options.mutationKey });
    },
  }),
  defaultOptions: {
    queries: {
      staleTime: 30_000,
      retry: 1,
      refetchOnWindowFocus: false,
    },
  },
});
