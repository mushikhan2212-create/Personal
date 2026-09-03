import { useEffect, useState } from 'react';
import { Layout, Typography, message } from 'antd';
import { ImportPage } from './pages/ImportPage';
import { LoginPage } from './pages/LoginPage';
import { SearchPage } from './pages/SearchPage';
import { VehicleDetailPage } from './pages/VehicleDetailPage';
import type { TenantSummary } from './api/types';
import { setSessionLostHandler, setTokens } from './api/client';

export interface Session {
  tenant: TenantSummary;
  permissions: string[];
  email: string;
}

/**
 * Which screen is showing.
 *
 * Held in state rather than routed through a URL. A router would be the right answer for an
 * app with shareable links, and this one deliberately has none: tokens live in memory only, so
 * a reload signs you out and a deep link could never load anyway. Adding react-router now
 * would be scaffolding for a property the app does not have.
 */
type View =
  | { name: 'search' }
  | { name: 'vehicle'; id: string }
  | { name: 'import' };

export function App() {
  const [session, setSession] = useState<Session | null>(null);
  const [view, setView] = useState<View>({ name: 'search' });
  const [catalogVersion, setCatalogVersion] = useState(0);

  const signOut = (): void => {
    setTokens(null, null);
    setSession(null);
    setView({ name: 'search' });
  };

  useEffect(() => {
    // The client renews an expired access token on its own; this fires only when renewal
    // fails, which means the refresh token is gone too and there is nothing to do but ask
    // for credentials again. Saying so beats dropping the user on a login screen unexplained.
    setSessionLostHandler(() => {
      setSession(null);
      setView({ name: 'search' });
      void message.warning('Your session expired. Please sign in again.', 6);
    });

    return () => setSessionLostHandler(null);
  }, []);

  return (
    <Layout style={{ minHeight: '100vh' }}>
      <Layout.Header style={{ display: 'flex', alignItems: 'center', gap: 16 }}>
        <Typography.Title level={4} style={{ color: '#fff', margin: 0 }}>
          Car Dealer — Vehicle Search
        </Typography.Title>
        <Typography.Text style={{ color: 'rgba(255,255,255,0.65)' }}>
          Phase 0.5 POC
        </Typography.Text>
      </Layout.Header>

      <Layout.Content style={{ padding: 24 }}>
        {!session && <LoginPage onSignedIn={setSession} />}

        {session && view.name === 'search' && (
          <SearchPage
            session={session}
            onSignOut={signOut}
            onOpenVehicle={(id) => setView({ name: 'vehicle', id })}
            onOpenImport={() => setView({ name: 'import' })}
            catalogVersion={catalogVersion}
          />
        )}

        {session && view.name === 'vehicle' && (
          <VehicleDetailPage id={view.id} onBack={() => setView({ name: 'search' })} />
        )}

        {session && view.name === 'import' && (
          <ImportPage
            onBack={() => setView({ name: 'search' })}
            // Bumped so returning to search re-runs the query rather than showing the
            // catalogue as it was before the import.
            onImported={() => setCatalogVersion((v) => v + 1)}
          />
        )}
      </Layout.Content>
    </Layout>
  );
}
