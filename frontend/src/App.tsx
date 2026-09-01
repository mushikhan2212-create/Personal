import { useEffect, useState } from 'react';
import { Layout, Typography, message } from 'antd';
import { LoginPage } from './pages/LoginPage';
import { SearchPage } from './pages/SearchPage';
import type { TenantSummary } from './api/types';
import { setSessionLostHandler, setTokens } from './api/client';

export interface Session {
  tenant: TenantSummary;
  permissions: string[];
  email: string;
}

export function App() {
  const [session, setSession] = useState<Session | null>(null);

  const signOut = (): void => {
    setTokens(null, null);
    setSession(null);
  };

  useEffect(() => {
    // The client renews an expired access token on its own; this fires only when renewal
    // fails, which means the refresh token is gone too and there is nothing to do but ask
    // for credentials again. Saying so beats dropping the user on a login screen unexplained.
    setSessionLostHandler(() => {
      setSession(null);
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
        {session
          ? <SearchPage session={session} onSignOut={signOut} />
          : <LoginPage onSignedIn={setSession} />}
      </Layout.Content>
    </Layout>
  );
}
