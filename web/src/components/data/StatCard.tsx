import type { LucideIcon } from 'lucide-react';
import { Card } from '@/components/ui/card';

export function StatCard({
  label,
  value,
  hint,
  icon: Icon,
}: {
  label: string;
  value: string | number;
  hint?: string;
  icon?: LucideIcon;
}) {
  return (
    <Card className="p-5">
      <div className="flex items-center justify-between">
        <span className="text-xs font-medium uppercase tracking-wide text-ink-500">{label}</span>
        {Icon && <Icon className="size-4 text-ink-400" />}
      </div>
      <div className="mt-2 text-3xl font-semibold tabular text-ink-900 dark:text-ink-50">{value}</div>
      {hint && <div className="mt-1 text-sm text-ink-400">{hint}</div>}
    </Card>
  );
}
