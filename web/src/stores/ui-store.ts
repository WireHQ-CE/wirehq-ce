import { create } from 'zustand';

type Theme = 'dark' | 'light';

interface UiState {
  theme: Theme;
  sidebarCollapsed: boolean;
  toggleTheme: () => void;
  toggleSidebar: () => void;
}

function applyTheme(theme: Theme) {
  const root = document.documentElement;
  root.classList.toggle('dark', theme === 'dark');
  localStorage.setItem('wh_theme', theme);
}

const initialTheme: Theme =
  (localStorage.getItem('wh_theme') as Theme | null) ??
  (window.matchMedia('(prefers-color-scheme: light)').matches ? 'light' : 'dark');
applyTheme(initialTheme);

export const useUiStore = create<UiState>((set, get) => ({
  theme: initialTheme,
  sidebarCollapsed: false,
  toggleTheme: () => {
    const theme = get().theme === 'dark' ? 'light' : 'dark';
    applyTheme(theme);
    set({ theme });
  },
  toggleSidebar: () => set({ sidebarCollapsed: !get().sidebarCollapsed }),
}));
