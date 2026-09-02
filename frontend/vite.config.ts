import { defineConfig, loadEnv } from 'vite';
import react from '@vitejs/plugin-react';

// ---------------------------------------------------------------------------
//  YOUR BACKEND PORT
//
//  Visual Studio, debug (F5) ..... 5246   <- the default below
//  docker compose up ............. 5080
//
//  Take it from the "Now listening on: http://localhost:____" line the API
//  prints at startup. Change it here, or set VITE_API_URL in .env.local to
//  avoid editing a tracked file. Either way, restart `npm run dev` afterwards:
//  Vite reads this file once, at startup.
//
//  Use the http:// address even when the API also listens on https://. The
//  https port serves a self-signed certificate that the proxy would reject,
//  and Development does not redirect http to https, so plain http is the
//  simpler and fully working choice.
// ---------------------------------------------------------------------------
const DEFAULT_API_URL = 'http://localhost:5246';

/**
 * The API is proxied rather than called cross-origin.
 *
 * This is why the browser's network tab shows requests going to localhost:5173 - the Vite dev
 * server's own origin - rather than to the API's port. That is correct and intended: the page
 * calls its own origin, and Vite forwards anything under /api to DEFAULT_API_URL server-side.
 * The browser never talks to the API directly, so there is no cross-origin request and the
 * backend needs no CORS configuration at all.
 *
 * The consequence worth knowing: when login fails with a 500 from localhost:5173, the failure
 * is usually the proxy being unable to reach the backend, not the backend rejecting the
 * login. The dev server's console says which.
 */
export default defineConfig(({ mode }) => {
  const env = loadEnv(mode, process.cwd(), '');
  const target = env.VITE_API_URL || DEFAULT_API_URL;

  console.log(`\n  API proxy: /api -> ${target}\n`);

  return {
    plugins: [react()],
    server: {
      port: 5173,
      proxy: {
        '/api': {
          target,
          changeOrigin: true,
          configure: (proxy) => {
            proxy.on('error', (err) => {
              console.error(`\n  [proxy] cannot reach the API at ${target}`);
              console.error(`  [proxy] ${err.message}`);
              console.error('  Is the backend running, and is that the port it printed at');
              console.error('  startup? Set VITE_API_URL in .env.local to change it.\n');
            });
          },
        },
      },
    },
  };
});
