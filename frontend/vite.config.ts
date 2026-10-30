import { defineConfig } from 'vite';
export default defineConfig(({ mode }) => ({
  define: { __DEVELOPMENT_ADMIN_UI__: JSON.stringify(mode === 'development') },
  server: { host: 'localhost', port: 5173, strictPort: true, proxy: { '/api': 'http://localhost:5000' } },
}));
