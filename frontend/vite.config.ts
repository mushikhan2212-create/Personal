import { defineConfig } from 'vite';
import react from '@vitejs/plugin-react';

// The API is proxied rather than called cross-origin, so the dev server needs no CORS
// configuration on the backend and the browser sends same-origin requests.
export default defineConfig({
  plugins: [react()],
  server: {
    port: 5173,
    proxy: {
      '/api': {
        target: process.env.VITE_API_URL ?? 'http://localhost:5080',
        changeOrigin: true,
      },
    },
  },
});
