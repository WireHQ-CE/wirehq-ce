import { defineConfig } from 'vite';
import react from '@vitejs/plugin-react';
import path from 'node:path';
import { createRequire } from 'node:module';

// Surface the app version to the client as a build-time constant, so client-error reports carry the build
// that produced them (docs/15 §12). The release pipeline stamps it from the tag (WIREHQ_VERSION, I2); a local
// build falls back to web/package.json.
const { version: pkgVersion } = createRequire(import.meta.url)('./package.json') as { version: string };
const version = process.env.WIREHQ_VERSION || pkgVersion;

export default defineConfig({
  plugins: [react()],
  define: {
    __APP_VERSION__: JSON.stringify(version),
  },
  resolve: {
    alias: { '@': path.resolve(__dirname, './src') },
  },
  build: {
    // Don't inline Vite's module-preload polyfill into index.html — an inline <script> would be
    // refused by our strict CSP (script-src has no 'unsafe-inline'). Modern browsers support
    // modulepreload natively; this only drops the polyfill for older ones. Keeps the CSP strict.
    modulePreload: { polyfill: false },
  },
  server: {
    port: 28173,
    // Proxy API calls in dev so the SPA and API share an origin (cookies + CSP-friendly). Targets the
    // compose API's published host port (28080).
    proxy: {
      '/api': { target: 'http://localhost:28080', changeOrigin: true },
      // The dynamic sitemap lives on the API; expose it at the canonical root path in dev too.
      '/sitemap.xml': {
        target: 'http://localhost:28080',
        changeOrigin: true,
        rewrite: () => '/api/v1/content/sitemap.xml',
      },
    },
  },
  // `vite preview` serves the production bundle; proxy the API the same way so the built app works locally.
  preview: {
    proxy: {
      '/api': { target: 'http://localhost:28080', changeOrigin: true },
      '/sitemap.xml': {
        target: 'http://localhost:28080',
        changeOrigin: true,
        rewrite: () => '/api/v1/content/sitemap.xml',
      },
    },
  },
});
