/// <reference types="vite/client" />

interface ImportMetaEnv {
  readonly VITE_API_BASE_URL?: string;
}

interface ImportMeta {
  readonly env: ImportMetaEnv;
}

/** App version baked in at build time from web/package.json (see vite.config.ts `define`). */
declare const __APP_VERSION__: string;
