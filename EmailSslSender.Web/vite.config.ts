import react from '@vitejs/plugin-react';
import { defineConfig } from 'vite';

// Панель собирается в wwwroot хоста: его отдаёт API (Program.cs, MapPanel). В образе Docker сборка
// идёт в свою стадию — каталог задаёт PANEL_OUT_DIR.
//
// ⚠ Ни одного встроенного скрипта и внешнего источника: страницы закрыты CSP `script-src 'self'`.
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
    // Разработка: `npm run dev` рядом с `dotnet run` — запросы к API уходят на хост.
    proxy: { '/api': 'http://localhost:8001' },
  },
});
