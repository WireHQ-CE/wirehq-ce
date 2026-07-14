import { useState } from 'react';
import { cn } from '@/lib/utils/cn';

/** A round avatar: shows the user's image when present, otherwise their initials on a neutral fill. */
export function Avatar({
  src,
  name,
  className,
}: {
  src?: string | null;
  name?: string | null;
  className?: string;
}) {
  const [failed, setFailed] = useState(false);
  const initials = toInitials(name);

  return (
    <span
      className={cn(
        'inline-flex items-center justify-center overflow-hidden rounded-full bg-ink-200 text-ink-600 dark:bg-ink-700 dark:text-ink-200',
        'size-9 text-sm font-medium',
        className,
      )}
      aria-hidden={!name}
    >
      {src && !failed ? (
        <img src={src} alt={name ?? 'Avatar'} className="size-full object-cover" onError={() => setFailed(true)} />
      ) : (
        <span>{initials}</span>
      )}
    </span>
  );
}

function toInitials(name?: string | null): string {
  if (!name) return '?';
  const parts = name.trim().split(/\s+/).filter(Boolean);
  if (parts.length === 0) return '?';
  if (parts.length === 1) return parts[0][0]!.toUpperCase();
  return (parts[0][0]! + parts[parts.length - 1][0]!).toUpperCase();
}
