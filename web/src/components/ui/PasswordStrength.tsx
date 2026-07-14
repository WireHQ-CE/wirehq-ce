import { cn } from '@/lib/utils/cn';

/**
 * A lightweight, dependency-free password strength meter. Heuristic only (length + character variety +
 * obvious-pattern penalties) — it nudges users toward stronger passwords; the authoritative policy is
 * still enforced server-side (min length + complexity). (ADR-015: no new dependencies.)
 */
export function PasswordStrength({ password }: { password: string }) {
  if (!password) return null;

  const { score, label } = estimateStrength(password);
  const tones = ['bg-danger-500', 'bg-danger-500', 'bg-warning-500', 'bg-gold-500', 'bg-success-500'];
  const textTones = ['text-danger-500', 'text-danger-500', 'text-warning-500', 'text-gold-500', 'text-success-600 dark:text-success-500'];

  return (
    <div className="mt-2">
      <div className="flex gap-1" aria-hidden>
        {[0, 1, 2, 3].map((i) => (
          <span
            key={i}
            className={cn('h-1 flex-1 rounded-full transition-colors', i < score ? tones[score] : 'bg-ink-200 dark:bg-ink-700')}
          />
        ))}
      </div>
      <p className={cn('mt-1 text-xs', textTones[score])}>Password strength: {label}</p>
    </div>
  );
}

function estimateStrength(password: string): { score: number; label: string } {
  let points = 0;
  if (password.length >= 8) points++;
  if (password.length >= 12) points++;
  if (password.length >= 16) points++;

  const classes = [/[a-z]/, /[A-Z]/, /\d/, /[^A-Za-z0-9]/].filter((re) => re.test(password)).length;
  if (classes >= 2) points++;
  if (classes >= 3) points++;
  if (classes >= 4) points++;

  // Penalise obvious weaknesses.
  if (/(.)\1{2,}/.test(password)) points--; // 3+ repeated chars
  if (/^[a-z]+$/i.test(password) || /^\d+$/.test(password)) points--; // single class only
  if (/password|qwerty|12345|admin|wirehq|letmein/i.test(password)) points -= 2;

  // Map raw points → a 1..4 bucket (0 reserved for the empty case, never rendered here).
  const score = Math.max(1, Math.min(4, points <= 1 ? 1 : points <= 3 ? 2 : points <= 4 ? 3 : 4));
  const label = ['', 'Weak', 'Fair', 'Good', 'Strong'][score];
  return { score, label };
}
