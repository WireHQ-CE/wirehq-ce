import type { ReactNode } from 'react';
import type { LucideIcon } from 'lucide-react';

export function EmptyState({
  icon: Icon,
  title,
  description,
  action,
}: {
  icon: LucideIcon;
  title: string;
  description: string;
  action?: ReactNode;
}) {
  return (
    <div className="flex flex-col items-center justify-center gap-2 px-6 py-16 text-center">
      <div className="grid size-11 place-items-center rounded-lg bg-ink-100 text-ink-400 dark:bg-ink-800">
        <Icon className="size-5" />
      </div>
      <h3 className="mt-1 text-h3 text-ink-900 dark:text-ink-50">{title}</h3>
      <p className="max-w-sm text-sm text-ink-500">{description}</p>
      {action && <div className="mt-2">{action}</div>}
    </div>
  );
}
