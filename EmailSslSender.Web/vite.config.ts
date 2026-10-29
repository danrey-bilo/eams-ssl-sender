import react from '@vitejs/plugin-react';
import { defineConfig } from 'vite';

// Сборка в wwwroot хоста; в Docker каталог задаёт PANEL_OUT_DIR.
// ⚠ CSP `script-src 'self'`: ни встроенных скриптов, ни внешних источников.
export default defineConfig({
  plugins: [react()],
  build: {
    outDir: process.env.PANEL_OUT_DIR ?? '../EmailSslSender.Api/wwwroot',
    emptyOutDir: true,
    assetsDir: 'assets',
    assetsInlineLimit: 0,
    sourcemap: false,
  },
  server: {
    port: 5173,
    proxy: { '/api': 'http://localhost:8001' },
  },
});
