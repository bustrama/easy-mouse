import { defineConfig } from 'vite';
import preact from '@preact/preset-vite';

// Relative base: the build is embedded in the C# server and served from `/`.
// No inline scripts or styles: the server sends a strict CSP (default-src 'self').
export default defineConfig({
  base: './',
  plugins: [preact()],
  build: {
    target: 'es2022',
    assetsInlineLimit: 0,
    cssCodeSplit: false,
    modulePreload: { polyfill: false },
  },
  server: {
    host: true,
    port: 5173,
    // npm run mock serves the protocol on 8787; the page and the socket stay same-origin.
    proxy: { '/api': 'http://localhost:8787', '/ws': { target: 'ws://localhost:8787', ws: true } },
  },
});
