import { Button } from '@/components/ui/button';

interface InfiniteLike {
  hasNextPage: boolean;
  isFetchingNextPage: boolean;
  fetchNextPage: () => void;
}

/** Cursor "Load more" footer for the keyset-paginated audit feeds. Renders nothing on the last page. */
export function LoadMore({ query }: { query: InfiniteLike }) {
  if (!query.hasNextPage) {
    return null;
  }

  return (
    <div className="mt-4 flex justify-center">
      <Button variant="secondary" size="sm" disabled={query.isFetchingNextPage} onClick={() => query.fetchNextPage()}>
        {query.isFetchingNextPage ? 'Loading…' : 'Load more'}
      </Button>
    </div>
  );
}
