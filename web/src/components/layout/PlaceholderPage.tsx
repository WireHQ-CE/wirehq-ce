import { Hammer } from 'lucide-react';
import { Card } from '@/components/ui/card';
import { EmptyState } from '@/components/data/EmptyState';
import { PageHeader } from './AppShell';

/** Stand-in for routes whose modules land post-MVP (Teams, Settings, …). */
export function PlaceholderPage({ title }: { title: string }) {
  return (
    <>
      <PageHeader title={title} subtitle="This module arrives on the roadmap — the foundation is ready for it." />
      <Card>
        <EmptyState
          icon={Hammer}
          title={`${title} is coming`}
          description="The navigation, layout, auth, and data plumbing already support this screen. See docs/09-roadmap.md."
        />
      </Card>
    </>
  );
}
