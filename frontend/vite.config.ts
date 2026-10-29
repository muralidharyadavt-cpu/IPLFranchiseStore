import { defineConfig } from 'vite';
export default defineConfig({
  server: { host: 'localhost', port: 5173, strictPort: true, proxy: { '/api': 'http://localhost:5000' } },
});
