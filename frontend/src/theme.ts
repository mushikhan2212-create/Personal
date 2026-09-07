import { theme as antTheme } from 'antd';
import type { ThemeConfig } from 'antd';

/**
 * One place the whole app takes its look from.
 *
 * Ant Design derives every component's colour, spacing and radius from these tokens, so a
 * change here reaches the tables, the buttons and the drawers at once. That is the reason to
 * have the file at all: the alternative is a style prop on every element, which drifts.
 *
 * The palette is deliberately muted. This is a screen a trader looks at for hours, scanning
 * hundreds of rows, so colour is spent only where it carries meaning - a price, a warning, a
 * link - and everything else is a neutral. A dashboard that shouts is tiring by mid-morning.
 */

/** Brand blue. Dark enough to pass contrast on white without a second, darker variant. */
const BRAND = '#2563eb';

/**
 * Inter first, then the platform's own UI face.
 *
 * Not loaded from a font CDN on purpose: a webfont is a render-blocking request to a third
 * party on every page load, and this stack looks right on every OS without one. Where Inter
 * is installed it is used; where it is not, the native UI font is already the correct choice.
 */
const FONT = "Inter, -apple-system, BlinkMacSystemFont, 'Segoe UI', Roboto, "
  + "'Helvetica Neue', Arial, sans-serif";

const shared: ThemeConfig['token'] = {
  colorPrimary: BRAND,
  colorInfo: BRAND,
  fontFamily: FONT,

  // 13px body against Ant's 14. A dense table earns its density here: it is the difference
  // between eight rows on screen and eleven.
  fontSize: 13,

  borderRadius: 6,
  controlHeight: 34,
  wireframe: false,
};

export const lightTheme: ThemeConfig = {
  algorithm: antTheme.defaultAlgorithm,
  token: {
    ...shared,

    // Blue-grey rather than pure neutral: white cards lift off it, and it is easier on the
    // eye than the #f5f5f5 default over a long session.
    colorBgLayout: '#f1f5f9',
    colorBgContainer: '#ffffff',
    colorBorderSecondary: '#e2e8f0',
    colorTextBase: '#0f172a',
  },
  components: {
    Layout: {
      headerBg: '#ffffff',
      headerHeight: 56,
      siderBg: '#0f172a',
      bodyBg: '#f1f5f9',
    },
    Menu: {
      darkItemBg: '#0f172a',
      darkSubMenuItemBg: '#0f172a',
      darkItemSelectedBg: BRAND,
      darkItemColor: '#94a3b8',
    },
    Table: {
      headerBg: '#f8fafc',
      headerColor: '#475569',
      rowHoverBg: '#f8fafc',
      cellPaddingBlock: 10,

      // The sort indicator belongs in the header. Shading the whole column in the body puts
      // the page's heaviest emphasis on whichever column is sorted, which by default is the
      // least important one on the screen.
      bodySortBg: 'transparent',
      headerSortActiveBg: '#eef2f7',
      headerSortHoverBg: '#eef2f7',
    },
  },
};

export const darkTheme: ThemeConfig = {
  algorithm: antTheme.darkAlgorithm,
  token: {
    ...shared,

    // Lifted a step from Ant's near-black default. Pure black backgrounds make every border
    // read as a hard line, which is exactly wrong for a table of many thin rows.
    colorBgLayout: '#0b1120',
    colorBgContainer: '#131c31',
    colorBorderSecondary: '#1e293b',
  },
  components: {
    Layout: {
      headerBg: '#131c31',
      headerHeight: 56,
      siderBg: '#0b1120',
      bodyBg: '#0b1120',
    },
    Menu: {
      darkItemBg: '#0b1120',
      darkSubMenuItemBg: '#0b1120',
      darkItemSelectedBg: BRAND,
      darkItemColor: '#94a3b8',
    },
    Table: {
      headerBg: '#0f1729',
      rowHoverBg: '#18233c',
      cellPaddingBlock: 10,
      bodySortBg: 'transparent',
      headerSortActiveBg: '#16203a',
      headerSortHoverBg: '#16203a',
    },
  },
};

const STORAGE_KEY = 'cardealer.theme';

/**
 * The stored preference, or the operating system's if there is none.
 *
 * Wrapped because localStorage throws outright in a private window with site data blocked,
 * and a colour preference is not worth a blank screen.
 */
export function readThemePreference(): 'light' | 'dark' {
  try {
    const stored = localStorage.getItem(STORAGE_KEY);

    if (stored === 'light' || stored === 'dark') {
      return stored;
    }
  } catch {
    // Unreadable storage is not an error worth surfacing; fall through to the OS setting.
  }

  return window.matchMedia?.('(prefers-color-scheme: dark)').matches ? 'dark' : 'light';
}

export function storeThemePreference(mode: 'light' | 'dark'): void {
  try {
    localStorage.setItem(STORAGE_KEY, mode);
  } catch {
    // The choice still applies to this session; it just will not survive a reload.
  }
}
