import { useEffect, type ReactNode } from 'react';
import { QueryClientProvider } from '@tanstack/react-query';
import { queryClient } from './query-client';
import { ToastProvider } from '@/components/ui/toast';
import { BrandProvider } from '@/features/branding/BrandProvider';
import { useAuth } from '@/features/auth/use-auth';

function AuthBootstrap({ children }: { children: ReactNode }) {
  const { bootstrap } = useAuth();
  useEffect(() => {
    void bootstrap();
    // Run once on mount.
    // eslint-disable-next-line react-hooks/exhaustive-deps
  }, []);
  return <>{children}</>;
}

export function Providers({ children }: { children: ReactNode }) {
  return (
    <QueryClientProvider client={queryClient}>
      <BrandProvider>
        <ToastProvider>
          <AuthBootstrap>{children}</AuthBootstrap>
        </ToastProvider>
      </BrandProvider>
    </QueryClientProvider>
  );
}
