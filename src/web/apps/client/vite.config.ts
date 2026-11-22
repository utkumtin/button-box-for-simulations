import { defineConfig } from 'vite';
import react from '@vitejs/plugin-react';

export default defineConfig({
  base: '/',
  plugins: [react()],
  build: {
    outDir: 'dist',
    // /assets/<sha256> is the host's image route (decisions §7), so the bundle lives elsewhere.
    assetsDir: 'static',
    modulePreload: { polyfill: false },
  },
});
