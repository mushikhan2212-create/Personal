import type { ReactNode } from 'react';
import {
  Avatar, Button, Dropdown, Flex, Layout, Menu, Tag, Tooltip, Typography,
} from 'antd';
import type { Session } from '../App';

/** The screens the sidebar can reach. Kept as a union so a typo is a build error. */
export type NavKey = 'search' | 'my-sources' | 'import';

interface Props {
  session: Session;
  active: NavKey;
  onNavigate: (key: NavKey) => void;
  onSignOut: () => void;
  mode: 'light' | 'dark';
  onToggleMode: () => void;
  children: ReactNode;
}

/**
 * The frame every signed-in screen sits in: a sidebar to move between them, a header saying
 * who and where you are, and the content.
 *
 * A rail rather than buttons in the header, because navigation that grows - Phase 1 adds
 * customers, an inbox and tasks - needs somewhere to grow into. Three loose buttons scale to
 * four; they do not scale to nine.
 */
export function AppShell({
  session, active, onNavigate, onSignOut, mode, onToggleMode, children,
}: Props) {
  const canSync = session.permissions.includes('vehicles.sync');

  const items = [
    { key: 'search', icon: <SearchGlyph />, label: 'Vehicles' },
    { key: 'my-sources', icon: <SourcesGlyph />, label: 'My sources' },
    ...(canSync ? [{ key: 'import', icon: <ImportGlyph />, label: 'Import' }] : []),
  ];

  return (
    <Layout style={{ minHeight: '100vh' }}>
      <Layout.Sider
        theme="dark"
        breakpoint="lg"
        collapsedWidth={64}
        width={216}
        style={{ position: 'sticky', top: 0, height: '100vh' }}
      >
        <Flex align="center" gap={10} style={{ height: 56, padding: '0 20px' }}>
          <CarGlyph />
          <Typography.Text strong style={{ color: '#f8fafc', fontSize: 15, whiteSpace: 'nowrap' }}>
            Car Dealer
          </Typography.Text>
        </Flex>

        <Menu
          theme="dark"
          mode="inline"
          selectedKeys={[active]}
          items={items}
          onClick={({ key }) => onNavigate(key as NavKey)}
          style={{ borderInlineEnd: 0 }}
        />
      </Layout.Sider>

      <Layout>
        <Layout.Header
          style={{
            padding: '0 20px',
            display: 'flex',
            alignItems: 'center',
            justifyContent: 'space-between',
            borderBottom: '1px solid var(--shell-border)',
            position: 'sticky',
            top: 0,
            zIndex: 10,
          }}
        >
          <Flex align="center" gap={8}>
            <Typography.Text strong>{session.tenant.name}</Typography.Text>
            <Tag style={{ marginInlineEnd: 0 }}>{session.tenant.slug}</Tag>
          </Flex>

          <Flex align="center" gap={12}>
            <Tooltip title={mode === 'dark' ? 'Switch to light' : 'Switch to dark'}>
              <Button
                type="text"
                aria-label={mode === 'dark' ? 'Switch to light theme' : 'Switch to dark theme'}
                onClick={onToggleMode}
                icon={mode === 'dark' ? <SunGlyph /> : <MoonGlyph />}
              />
            </Tooltip>

            <Dropdown
              trigger={['click']}
              menu={{
                items: [
                  { key: 'email', label: session.email, disabled: true },
                  { type: 'divider' },
                  { key: 'out', label: 'Sign out', danger: true, onClick: onSignOut },
                ],
              }}
            >
              <Flex align="center" gap={8} style={{ cursor: 'pointer' }}>
                <Avatar size={28} style={{ backgroundColor: '#2563eb', fontSize: 12 }}>
                  {session.email.slice(0, 2).toUpperCase()}
                </Avatar>
              </Flex>
            </Dropdown>
          </Flex>
        </Layout.Header>

        <Layout.Content style={{ padding: 20 }}>{children}</Layout.Content>
      </Layout>
    </Layout>
  );
}

// Inline SVGs rather than @ant-design/icons: five glyphs do not justify a dependency whose
// tree-shaken bundle is still measured in tens of kilobytes.

const stroke = {
  fill: 'none',
  stroke: 'currentColor',
  strokeWidth: 1.8,
  strokeLinecap: 'round' as const,
  strokeLinejoin: 'round' as const,
};

function CarGlyph() {
  return (
    <svg width="20" height="20" viewBox="0 0 24 24" style={{ color: '#60a5fa', flexShrink: 0 }}>
      <path {...stroke} d="M3 13l2-5a2 2 0 012-1.4h10A2 2 0 0119 8l2 5v5h-3v-2H6v2H3v-5z" />
      <circle {...stroke} cx="7.5" cy="14.5" r="1.2" />
      <circle {...stroke} cx="16.5" cy="14.5" r="1.2" />
    </svg>
  );
}

function SearchGlyph() {
  return (
    <svg width="16" height="16" viewBox="0 0 24 24">
      <circle {...stroke} cx="11" cy="11" r="6" />
      <path {...stroke} d="M20 20l-4.5-4.5" />
    </svg>
  );
}

function SourcesGlyph() {
  return (
    <svg width="16" height="16" viewBox="0 0 24 24">
      <ellipse {...stroke} cx="12" cy="6" rx="7" ry="3" />
      <path {...stroke} d="M5 6v6c0 1.7 3.1 3 7 3s7-1.3 7-3V6" />
      <path {...stroke} d="M5 12v6c0 1.7 3.1 3 7 3s7-1.3 7-3v-6" />
    </svg>
  );
}

function ImportGlyph() {
  return (
    <svg width="16" height="16" viewBox="0 0 24 24">
      <path {...stroke} d="M12 3v11" />
      <path {...stroke} d="M8 10l4 4 4-4" />
      <path {...stroke} d="M4 17v2a2 2 0 002 2h12a2 2 0 002-2v-2" />
    </svg>
  );
}

function MoonGlyph() {
  return (
    <svg width="17" height="17" viewBox="0 0 24 24">
      <path {...stroke} d="M20 14.5A8.5 8.5 0 019.5 4a8.5 8.5 0 1010.5 10.5z" />
    </svg>
  );
}

function SunGlyph() {
  return (
    <svg width="17" height="17" viewBox="0 0 24 24">
      <circle {...stroke} cx="12" cy="12" r="4" />
      <path {...stroke} d="M12 2v2M12 20v2M2 12h2M20 12h2M4.9 4.9l1.4 1.4M17.7 17.7l1.4 1.4M19.1 4.9l-1.4 1.4M6.3 17.7l-1.4 1.4" />
    </svg>
  );
}
