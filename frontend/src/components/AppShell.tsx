import type { ReactNode } from 'react';
import {
  Avatar, Badge, Button, Dropdown, Flex, Layout, Menu, Tag, Tooltip, Typography,
} from 'antd';
import type { Session } from '../App';

/** The screens the sidebar can reach. Kept as a union so a typo is a build error. */
export type NavKey =
  'search' | 'customers' | 'alerts' | 'duplicates' | 'my-sources' | 'import';

interface Props {
  session: Session;
  active: NavKey;
  onNavigate: (key: NavKey) => void;
  onSignOut: () => void;
  mode: 'light' | 'dark';
  onToggleMode: () => void;
  /** Alerts nobody has looked at. Drives the bell and the sidebar badge. */
  unseenAlerts: number;
  collapsed: boolean;
  onToggleCollapsed: () => void;
  children: ReactNode;
}

const EXPANDED_WIDTH = 260;
const COLLAPSED_WIDTH = 72;

/**
 * The frame every signed-in screen sits in: a sidebar to move between them, a header saying
 * who and where you are, and the content.
 *
 * A rail rather than buttons in the header, because navigation that grows - Phase 1 adds an
 * inbox and tasks - needs somewhere to grow into. Three loose buttons scale to four; they do
 * not scale to nine.
 *
 * The rail collapses to icons on demand and remembers the choice. Two things make that work
 * rather than merely happen: every item keeps an icon that means something without its label,
 * and Ant shows the label as a tooltip while collapsed - so a narrow rail is still navigable
 * by someone who has not memorised five glyphs.
 */
export function AppShell({
  session, active, onNavigate, onSignOut, mode, onToggleMode, unseenAlerts, collapsed,
  onToggleCollapsed, children,
}: Props) {
  const canSync = session.permissions.includes('vehicles.sync');
  const canSeeCustomers = session.permissions.includes('customers.read');
  const canMerge = session.permissions.includes('vehicles.merge');

  const items = [
    { key: 'search', icon: <SearchGlyph />, label: 'Vehicles' },
    ...(canSeeCustomers
      ? [
          { key: 'customers', icon: <PeopleGlyph />, label: 'Customers' },
          {
            key: 'alerts',
            icon: <BellGlyph />,
            // Counted in the rail as well as on the bell: someone working in Customers all day
            // never looks at the header, and an alert nobody sees is not an alert.
            label: (
              <Flex align="center" justify="space-between" gap={8}>
                <span>New matches</span>
                {unseenAlerts > 0 && <Badge count={unseenAlerts} color="#3C50E0" size="small" />}
              </Flex>
            ),
          },
        ]
      : []),
    // Below the selling screens and above the administrative ones, which is where it belongs:
    // reviewing duplicates is housekeeping on the catalogue, not part of anybody's day.
    ...(canMerge
      ? [{ key: 'duplicates', icon: <MergeGlyph />, label: 'Duplicates' }]
      : []),
    { key: 'my-sources', icon: <SourcesGlyph />, label: 'My sources' },
    ...(canSync ? [{ key: 'import', icon: <ImportGlyph />, label: 'Import' }] : []),
  ];

  return (
    <Layout style={{ minHeight: '100vh' }}>
      <Layout.Sider
        theme="dark"
        breakpoint="lg"
        collapsible
        collapsed={collapsed}
        onCollapse={onToggleCollapsed}
        trigger={null}
        collapsedWidth={COLLAPSED_WIDTH}
        width={EXPANDED_WIDTH}
        style={{ position: 'sticky', top: 0, height: '100vh', overflow: 'hidden' }}
      >
        <Flex
          align="center"
          justify={collapsed ? 'center' : 'flex-start'}
          gap={10}
          style={{ height: 64, padding: collapsed ? 0 : '0 22px', flexShrink: 0 }}
        >
          <CarGlyph />

          {!collapsed && (
            <Typography.Text
              strong
              style={{ color: '#f8fafc', fontSize: 17, whiteSpace: 'nowrap', letterSpacing: 0.2 }}
            >
              Car Dealer
            </Typography.Text>
          )}
        </Flex>

        <div
          className="app-sider-nav"
          style={{ height: 'calc(100vh - 64px)', overflowY: 'auto', overflowX: 'hidden' }}
        >
          {!collapsed && (
            // TailAdmin groups its rail under small caps headings. With one group it is not
            // yet organising anything, but it is where the second group goes when tasks and
            // the inbox arrive, and it stops the first item sitting flush under the logo.
            <Typography.Text
              style={{
                display: 'block',
                padding: '18px 34px 8px',
                color: '#8A99AF',
                fontSize: 11,
                fontWeight: 600,
                letterSpacing: 1,
              }}
            >
              MENU
            </Typography.Text>
          )}

          <Menu
            theme="dark"
            mode="inline"
            // Stated rather than inherited. Ant passes this down from a collapsed Sider through
            // context, but only to a Menu that is its direct child - and this one sits inside
            // the scrolling div. Without it the labels are not hidden, they are clipped, and a
            // collapsed rail reads "Ve  Cu  My  Im".
            inlineCollapsed={collapsed}
            selectedKeys={[active]}
            items={items}
            onClick={({ key }) => onNavigate(key as NavKey)}
            style={{ borderInlineEnd: 0, paddingTop: collapsed ? 12 : 0 }}
          />
        </div>
      </Layout.Sider>

      <Layout>
        <Layout.Header
          style={{
            padding: '0 24px',
            display: 'flex',
            alignItems: 'center',
            justifyContent: 'space-between',
            borderBottom: '1px solid var(--app-stroke)',
            position: 'sticky',
            top: 0,
            zIndex: 10,
          }}
        >
          <Flex align="center" gap={14}>
            <Tooltip title={collapsed ? 'Expand the menu' : 'Collapse the menu'}>
              <Button
                type="text"
                aria-label={collapsed ? 'Expand the menu' : 'Collapse the menu'}
                aria-expanded={!collapsed}
                onClick={onToggleCollapsed}
                icon={<MenuGlyph />}
              />
            </Tooltip>

            <Flex align="center" gap={8}>
              <Typography.Text strong style={{ fontSize: 15 }}>{session.tenant.name}</Typography.Text>
              <Tag style={{ marginInlineEnd: 0 }}>{session.tenant.slug}</Tag>
            </Flex>
          </Flex>

          <Flex align="center" gap={12}>
            {canSeeCustomers && (
              <Tooltip
                title={unseenAlerts === 0
                  ? 'No new matches'
                  : `${unseenAlerts} new match${unseenAlerts === 1 ? '' : 'es'}`}
              >
                <Badge count={unseenAlerts} size="small" offset={[-2, 4]}>
                  <Button
                    type="text"
                    aria-label={`New matches${unseenAlerts > 0 ? `, ${unseenAlerts} unseen` : ''}`}
                    onClick={() => onNavigate('alerts')}
                    icon={<BellGlyph />}
                  />
                </Badge>
              </Tooltip>
            )}

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
                <Avatar size={34} style={{ backgroundColor: '#3C50E0', fontSize: 13 }}>
                  {session.email.slice(0, 2).toUpperCase()}
                </Avatar>
              </Flex>
            </Dropdown>
          </Flex>
        </Layout.Header>

        <Layout.Content style={{ padding: 24 }}>{children}</Layout.Content>
      </Layout>
    </Layout>
  );
}

// Inline SVGs rather than @ant-design/icons: six glyphs do not justify a dependency whose
// tree-shaken bundle is still measured in tens of kilobytes.

const stroke = {
  fill: 'none',
  stroke: 'currentColor',
  strokeWidth: 1.8,
  strokeLinecap: 'round' as const,
  strokeLinejoin: 'round' as const,
};

/**
 * Ant clones a className onto whatever is passed as a menu item's `icon`, and its collapsed
 * styling is written against that class - the rule that hides a label when the rail is narrow
 * is `.ant-menu-item-icon + span { opacity: 0 }`.
 *
 * A function component swallows a cloned className, so without this the class never reaches
 * the svg, the rule never matches, and a collapsed rail shows clipped labels reading
 * "Ve  Cu  My  Im" instead of icons. Every glyph below therefore forwards it.
 */
interface GlyphProps {
  className?: string;
}

function CarGlyph() {
  return (
    <svg width="26" height="26" viewBox="0 0 24 24" style={{ color: '#8098F9', flexShrink: 0 }}>
      <path {...stroke} d="M3 13l2-5a2 2 0 012-1.4h10A2 2 0 0119 8l2 5v5h-3v-2H6v2H3v-5z" />
      <circle {...stroke} cx="7.5" cy="14.5" r="1.2" />
      <circle {...stroke} cx="16.5" cy="14.5" r="1.2" />
    </svg>
  );
}

function MenuGlyph({ className }: GlyphProps) {
  return (
    <svg width="20" height="20" viewBox="0 0 24 24" className={className}>
      <path {...stroke} d="M4 7h16M4 12h16M4 17h16" />
    </svg>
  );
}

function SearchGlyph({ className }: GlyphProps) {
  return (
    <svg width="18" height="18" viewBox="0 0 24 24" className={className}>
      <circle {...stroke} cx="11" cy="11" r="6" />
      <path {...stroke} d="M20 20l-4.5-4.5" />
    </svg>
  );
}

function PeopleGlyph({ className }: GlyphProps) {
  return (
    <svg width="18" height="18" viewBox="0 0 24 24" className={className}>
      <circle {...stroke} cx="9" cy="8" r="3.2" />
      <path {...stroke} d="M3.5 19.5a5.5 5.5 0 0111 0" />
      <path {...stroke} d="M16 5.5a3.2 3.2 0 010 5.6M17.5 14.2a5.5 5.5 0 013 5.3" />
    </svg>
  );
}

function BellGlyph({ className }: GlyphProps) {
  return (
    <svg width="18" height="18" viewBox="0 0 24 24" className={className}>
      <path {...stroke} d="M18 8a6 6 0 10-12 0c0 6-2 7-2 7h16s-2-1-2-7z" />
      <path {...stroke} d="M13.7 20a2 2 0 01-3.4 0" />
    </svg>
  );
}

function SourcesGlyph({ className }: GlyphProps) {
  return (
    <svg width="18" height="18" viewBox="0 0 24 24" className={className}>
      <ellipse {...stroke} cx="12" cy="6" rx="7" ry="3" />
      <path {...stroke} d="M5 6v6c0 1.7 3.1 3 7 3s7-1.3 7-3V6" />
      <path {...stroke} d="M5 12v6c0 1.7 3.1 3 7 3s7-1.3 7-3v-6" />
    </svg>
  );
}

/** Two paths converging into one — what a merge does. */
function MergeGlyph({ className }: GlyphProps) {
  return (
    <svg width="18" height="18" viewBox="0 0 24 24" className={className}>
      <path {...stroke} d="M6 3v4c0 2.2 1.8 4 4 4h4" />
      <path {...stroke} d="M18 3v4c0 2.2-1.8 4-4 4h-4" />
      <path {...stroke} d="M12 11v10" />
      <path {...stroke} d="M9 18l3 3 3-3" />
    </svg>
  );
}

function ImportGlyph({ className }: GlyphProps) {
  return (
    <svg width="18" height="18" viewBox="0 0 24 24" className={className}>
      <path {...stroke} d="M12 3v11" />
      <path {...stroke} d="M8 10l4 4 4-4" />
      <path {...stroke} d="M4 17v2a2 2 0 002 2h12a2 2 0 002-2v-2" />
    </svg>
  );
}

function MoonGlyph({ className }: GlyphProps) {
  return (
    <svg width="18" height="18" viewBox="0 0 24 24" className={className}>
      <path {...stroke} d="M20 14.5A8.5 8.5 0 019.5 4a8.5 8.5 0 1010.5 10.5z" />
    </svg>
  );
}

function SunGlyph({ className }: GlyphProps) {
  return (
    <svg width="18" height="18" viewBox="0 0 24 24" className={className}>
      <circle {...stroke} cx="12" cy="12" r="4" />
      <path {...stroke} d="M12 2v2M12 20v2M2 12h2M20 12h2M4.9 4.9l1.4 1.4M17.7 17.7l1.4 1.4M19.1 4.9l-1.4 1.4M6.3 17.7l-1.4 1.4" />
    </svg>
  );
}
