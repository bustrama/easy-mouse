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
  server: { host: true, port: 5173 },
});
