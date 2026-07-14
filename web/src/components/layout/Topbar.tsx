import { useEffect, useState } from 'react';
import { useNavigate } from 'react-router-dom';
import { ChevronDown, LogOut, Moon, Search, Sun } from 'lucide-react';
import { Button } from '@/components/ui/button';
import { Avatar } from '@/components/ui/Avatar';
import { useAuth } from '@/features/auth/use-auth';
import { CommandPalette } from '@/features/search/CommandPalette';
import { UpdateIndicator } from '@/features/updates/UpdateIndicator';
import { useAuthStore } from '@/stores/auth-store';
import { useUiStore } from '@/stores/ui-store';

export function Topbar() {
  const navigate = useNavigate();
  const { logout } = useAuth();
  const user = useAuthStore((s) => s.user);
  const { theme, toggleTheme } = useUiStore();
  const [paletteOpen, setPaletteOpen] = useState(false);

  // ⌘K / Ctrl+K opens the palette from anywhere in the app.
  useEffect(() => {
    function onKeyDown(e: KeyboardEvent) {
      if ((e.metaKey || e.ctrlKey) && e.key.toLowerCase() === 'k') {
        e.preventDefault();
        setPaletteOpen((o) => !o);
      }
    }
    window.addEventListener('keydown', onKeyDown);
    return () => window.removeEventListener('keydown', onKeyDown);
  }, []);

  const activeOrg = user?.organizations.find((o) => o.organizationId === user.activeOrganizationId);

  return (
    <header className="flex h-14 shrink-0 items-center gap-3 border-b bg-ink-0/80 px-4 backdrop-blur dark:bg-ink-950/80 dark:border-ink-800">
      {/* Tenant switcher — multi-tenancy is always in view. */}
      <button className="flex items-center gap-2 rounded-md border border-ink-200 px-2.5 py-1.5 text-sm font-medium hover:bg-ink-50 dark:border-ink-700 dark:hover:bg-ink-800">
        <span className="grid size-5 place-items-center rounded bg-gold-500 text-[10px] font-bold text-ink-950">
          {activeOrg?.name?.[0]?.toUpperCase() ?? 'W'}
        </span>
        <span className="max-w-40 truncate">{activeOrg?.name ?? 'No organization'}</span>
        <ChevronDown className="size-4 text-ink-400" />
      </button>

      {/* Command palette (⌘K): live search over the pages/settings/actions THIS account can reach. */}
      <button
        onClick={() => setPaletteOpen(true)}
        className="flex h-9 flex-1 items-center gap-2 rounded-md border border-ink-200 px-3 text-sm text-ink-400 hover:bg-ink-50 dark:border-ink-700 dark:hover:bg-ink-800 md:max-w-md"
        aria-label="Search (⌘K)"
      >
        <Search className="size-4" />
        <span className="flex-1 text-left">Search…</span>
        <kbd className="rounded border border-ink-200 px-1.5 text-xs dark:border-ink-700">⌘K</kbd>
      </button>
      <CommandPalette open={paletteOpen} onClose={() => setPaletteOpen(false)} />

      <div className="ml-auto flex items-center gap-1">
        <UpdateIndicator />
        <Button variant="ghost" size="icon" onClick={toggleTheme} aria-label="Toggle theme">
          {theme === 'dark' ? <Sun /> : <Moon />}
        </Button>
        <div className="ml-1 flex items-center gap-2 pl-2">
          <div className="hidden text-right sm:block">
            <div className="text-sm font-medium leading-tight">{user?.name}</div>
            <div className="text-xs text-ink-400">{user?.email}</div>
          </div>
          <Avatar src={user?.avatarUrl} name={user?.name} className="size-8" />
          <Button
            variant="ghost"
            size="icon"
            aria-label="Sign out"
            onClick={async () => {
              await logout();
              navigate('/login');
            }}
          >
            <LogOut />
          </Button>
        </div>
      </div>
    </header>
  );
}
