import { useEffect, useState } from 'react';
import { App as AntApp, ConfigProvider, message } from 'antd';
import { AppShell } from './components/AppShell';
import type { NavKey } from './components/AppShell';
import { ImportPage } from './pages/ImportPage';
import { LoginPage } from './pages/LoginPage';
import { MySourcesPage } from './pages/MySourcesPage';
import { SearchPage } from './pages/SearchPage';
import { VehicleDetailPage } from './pages/VehicleDetailPage';
import type { TenantSummary } from './api/types';
import { setSessionLostHandler, setTokens } from './api/client';
import { darkTheme, lightTheme, readThemePreference, storeThemePreference } from './theme';

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
  // The three the sidebar can reach carry no payload, so they are exactly a NavKey. Saying it
  // that way rather than repeating the names keeps the nav and the router from drifting apart.
  | { name: NavKey }
  | { name: 'vehicle'; id: string };

export function App() {
  const [session, setSession] = useState<Session | null>(null);
  const [view, setView] = useState<View>({ name: 'search' });
  const [catalogVersion, setCatalogVersion] = useState(0);
  const [mode, setMode] = useState<'light' | 'dark'>(readThemePreference);

  const signOut = (): void => {
    setTokens(null, null);
    setSession(null);
    setView({ name: 'search' });
  };

  const toggleMode = (): void => {
    setMode((current) => {
      const next = current === 'dark' ? 'light' : 'dark';
      storeThemePreference(next);
      return next;
    });
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

  useEffect(() => {
    // The shell's header border reads from this, so it follows the theme without the two
    // files having to import each other's palette. colorScheme makes the browser's own
    // chrome - scrollbars, form controls - match rather than staying stubbornly light.
    document.documentElement.style.setProperty(
      '--shell-border',
      mode === 'dark' ? '#1e293b' : '#e2e8f0',
    );
    document.documentElement.style.colorScheme = mode;
  }, [mode]);

  /** The detail view has no sidebar entry of its own; it belongs with the vehicle list. */
  const activeNav: NavKey = view.name === 'vehicle' ? 'search' : view.name;

  return (
    <ConfigProvider theme={mode === 'dark' ? darkTheme : lightTheme}>
      <AntApp>
        {!session
          ? <LoginPage onSignedIn={setSession} mode={mode} onToggleMode={toggleMode} />
          : (
            <AppShell
              session={session}
              active={activeNav}
              onNavigate={(key) => setView({ name: key })}
              onSignOut={signOut}
              mode={mode}
              onToggleMode={toggleMode}
            >
              {view.name === 'search' && (
                <SearchPage
                  onOpenVehicle={(id) => setView({ name: 'vehicle', id })}
                  onOpenMySources={() => setView({ name: 'my-sources' })}
                  catalogVersion={catalogVersion}
                />
              )}

              {view.name === 'vehicle' && (
                <VehicleDetailPage id={view.id} onBack={() => setView({ name: 'search' })} />
              )}

              {view.name === 'my-sources' && (
                <MySourcesPage
                  canManage={session.permissions.includes('vehicles.sync')}
                  // A muted source changes what search returns, so the next visit re-runs the
                  // query rather than showing results gathered under the old choices.
                  onChanged={() => setCatalogVersion((v) => v + 1)}
                />
              )}

              {view.name === 'import' && (
                <ImportPage
                  // Bumped so returning to search re-runs the query rather than showing the
                  // catalogue as it was before the import.
                  onImported={() => setCatalogVersion((v) => v + 1)}
                />
              )}
            </AppShell>
          )}
      </AntApp>
    </ConfigProvider>
  );
}
