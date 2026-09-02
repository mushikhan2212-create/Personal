import { defineConfig, loadEnv } from 'vite';
import react from '@vitejs/plugin-react';

/**
 * The API is proxied rather than called cross-origin, so the browser makes same-origin
 * requests and the backend needs no CORS configuration.
 *
 * The default is port 5080, which is what `docker compose up` publishes. Running the API with
 * `dotnet run` instead uses its launch profile, which listens on 5246 - so set VITE_API_URL
 * (see .env.example) or the screen will load against nothing and simply show no vehicles.
 */
export default defineConfig(({ mode }) => {
  const env = loadEnv(mode, process.cwd(), '');
  const target = env.VITE_API_URL || 'http://localhost:5080';

  return {
    plugins: [react()],
    server: {
      port: 5173,
      proxy: {
        '/api': {
          target,
          changeOrigin: true,

          // A backend that is not running is the single most common reason this screen looks
          // broken. Saying so beats a generic 500 in the network tab.
          configure: (proxy) => {
            proxy.on('error', (err) => {
              console.error(`\n  [proxy] cannot reach the API at ${target}: ${err.message}`);
              console.error('  Is the backend running? See frontend/README.md.\n');
            });
          },
        },
      },
    },
  };
});
