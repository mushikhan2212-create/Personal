import { useEffect, useState } from 'react';
import { App as AntApp, ConfigProvider, message } from 'antd';
import { AlertsPage } from './pages/AlertsPage';
import { DuplicatesPage } from './pages/DuplicatesPage';
import { TemplatesPage } from './pages/TemplatesPage';
import { AppShell } from './components/AppShell';
import type { NavKey } from './components/AppShell';
import { CustomerDetailPage } from './pages/CustomerDetailPage';
import { CustomersPage } from './pages/CustomersPage';
import { ImportPage } from './pages/ImportPage';
import { LoginPage } from './pages/LoginPage';
import { MySourcesPage } from './pages/MySourcesPage';
import { SearchPage } from './pages/SearchPage';
import { VehicleDetailPage } from './pages/VehicleDetailPage';
import type { TenantSummary } from './api/types';
import { countAlerts, setSessionLostHandler, setTokens } from './api/client';
import {
  darkTheme, groundFor, lightTheme, readSidebarCollapsed, readThemePreference,
  storeSidebarCollapsed, storeThemePreference, strokeFor,
} from './theme';

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
  | { name: 'vehicle'; id: string }
  | { name: 'customer'; id: string };

export function App() {
  const [session, setSession] = useState<Session | null>(null);
  const [view, setView] = useState<View>({ name: 'search' });
  const [catalogVersion, setCatalogVersion] = useState(0);
  const [mode, setMode] = useState<'light' | 'dark'>(readThemePreference);
  const [collapsed, setCollapsed] = useState<boolean>(readSidebarCollapsed);
  const [unseenAlerts, setUnseenAlerts] = useState(0);
  const [alertVersion, setAlertVersion] = useState(0);

  const signOut = (): void => {
    setTokens(null, null);
    setSession(null);
    setView({ name: 'search' });
    setUnseenAlerts(0);
  };

  const toggleMode = (): void => {
    setMode((current) => {
      const next = current === 'dark' ? 'light' : 'dark';
      storeThemePreference(next);
      return next;
    });
  };

  const toggleCollapsed = (): void => {
    setCollapsed((current) => {
      storeSidebarCollapsed(!current);
      return !current;
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
    // Every hand-drawn border in the app reads from this variable, so they follow the theme
    // without each file having to import the palette. data-theme is what index.css switches
    // the card elevation on, since a shadow tuned for a white ground is invisible on a dark
    // one. colorScheme makes the browser's own chrome - scrollbars, form controls - match
    // rather than staying stubbornly light.
    document.documentElement.style.setProperty('--app-stroke', strokeFor(mode));
    document.documentElement.dataset.theme = mode;
    document.documentElement.style.colorScheme = mode;
    document.body.style.background = groundFor(mode);
  }, [mode]);

  useEffect(() => {
    // The bell's count. Refreshed when the app opens, when the alerts screen changes something,
    // and whenever the catalogue does - an import is the most likely thing to have produced a
    // new match, so a stale zero right after one would be the worst moment to be wrong.
    if (!session?.permissions.includes('customers.read')) return;

    let cancelled = false;

    countAlerts()
      .then(({ unseen }) => { if (!cancelled) setUnseenAlerts(unseen); })
      // A failed count is not worth a message: the bell simply keeps its last number, and
      // every other screen still works.
      .catch(() => undefined);

    return () => { cancelled = true; };
  }, [session, alertVersion, catalogVersion]);

  /** The detail view has no sidebar entry of its own; it belongs with the vehicle list. */
  const activeNav: NavKey = view.name === 'vehicle' ? 'search'
    : view.name === 'customer' ? 'customers'
      : view.name;

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
              unseenAlerts={unseenAlerts}
              collapsed={collapsed}
              onToggleCollapsed={toggleCollapsed}
            >
              {view.name === 'search' && (
                <SearchPage
                  onOpenVehicle={(id) => setView({ name: 'vehicle', id })}
                  onOpenMySources={() => setView({ name: 'my-sources' })}
                  catalogVersion={catalogVersion}
                />
              )}

              {view.name === 'customers' && (
                <CustomersPage
                  canManage={session.permissions.includes('customers.manage')}
                  onOpenCustomer={(id) => setView({ name: 'customer', id })}
                />
              )}

              {view.name === 'alerts' && (
                <AlertsPage
                  canManage={session.permissions.includes('customers.manage')}
                  onOpenCustomer={(id) => setView({ name: 'customer', id })}
                  onOpenVehicle={(id) => setView({ name: 'vehicle', id })}
                  onChanged={() => setAlertVersion((v) => v + 1)}
                />
              )}

              {view.name === 'duplicates' && (
                <DuplicatesPage
                  onOpenVehicle={(id) => setView({ name: 'vehicle', id })}
                  onChanged={() => setCatalogVersion((v) => v + 1)}
                />
              )}

              {view.name === 'templates' && <TemplatesPage />}

              {view.name === 'customer' && (
                <CustomerDetailPage
                  publicId={view.id}
                  canManage={session.permissions.includes('customers.manage')}
                  canRank={session.permissions.includes('ai.recommend')}
                  onBack={() => setView({ name: 'customers' })}
                  onOpenVehicle={(id) => setView({ name: 'vehicle', id })}
                />
              )}

              {view.name === 'vehicle' && (
                <VehicleDetailPage
                  id={view.id}
                  onBack={() => setView({ name: 'search' })}
                  canMessage={session.permissions.includes('customers.manage')}
                  canPrice={session.permissions.includes('vehicles.price')}
                />
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
