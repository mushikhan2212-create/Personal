import { theme as antTheme } from 'antd';
import type { ThemeConfig } from 'antd';

/**
 * One place the whole app takes its look from.
 *
 * Ant Design derives every component's colour, spacing and radius from these tokens, so a
 * change here reaches the cards, the buttons and the drawers at once. That is the reason to
 * have the file at all: the alternative is a style prop on every element, which drifts.
 *
 * The palette is TailAdmin's, which the product owner picked as the reference for how this
 * application should look. Its values are transcribed here rather than approximated, because
 * "roughly that blue" across forty components is how a design stops being a design. TailAdmin
 * is a Tailwind template and this application is Ant Design, so what carries over is the
 * palette, the elevation and the proportions - not its markup.
 */

/** TailAdmin `primary`. Every accent, link and selected state resolves from this. */
const BRAND = '#3C50E0';

/** TailAdmin `black` - the sidebar, in both themes. */
const SIDEBAR_BG = '#1C2434';

/** TailAdmin `boxdark` and `boxdark-2`: the raised surface and the ground it sits on. */
const DARK_SURFACE = '#24303F';
const DARK_GROUND = '#1A222C';

/** TailAdmin `stroke` / `strokedark`. Every divider in the app is one of these two. */
const STROKE = '#E2E8F0';
const STROKE_DARK = '#2E3A47';

/** TailAdmin `bodydark` / `bodydark1`: sidebar text at rest and when selected. */
const SIDEBAR_TEXT = '#AEB7C0';
const SIDEBAR_TEXT_ACTIVE = '#DEE4EE';

/**
 * TailAdmin's `meta` colours, which is where its status hues live.
 *
 * Mapped onto Ant's semantic roles rather than used directly, so a success tag drawn by Ant
 * and one drawn by hand are the same green.
 */
const SUCCESS = '#10B981';
const WARNING = '#FFBA00';
const DANGER = '#DC3545';
const INFO = '#259AE6';

// TailAdmin's shadow-default lives in index.css rather than here: Ant applies its own card
// shadow only to borderless cards, and these keep their border.

/**
 * Inter first, then the platform's own UI face.
 *
 * TailAdmin ships Satoshi. It is not used here: a webfont is a render-blocking request to a
 * third party on every page load, and the shape of the design survives the substitution where
 * a missing font would not. Where Inter is installed it is used; where it is not, the native
 * UI font is already the correct choice.
 */
const FONT = "Inter, -apple-system, BlinkMacSystemFont, 'Segoe UI', Roboto, "
  + "'Helvetica Neue', Arial, sans-serif";

const shared: ThemeConfig['token'] = {
  colorPrimary: BRAND,
  colorInfo: INFO,
  colorSuccess: SUCCESS,
  colorWarning: WARNING,
  colorError: DANGER,
  fontFamily: FONT,

  // 14, up from the 13 this screen used when it was a dense table. The result set is a card
  // grid now, so the density that 13 bought is no longer the thing being optimised for, and
  // 14 is easier to read across a working day.
  fontSize: 14,

  borderRadius: 8,
  controlHeight: 38,
  wireframe: false,
};

export const lightTheme: ThemeConfig = {
  algorithm: antTheme.defaultAlgorithm,
  token: {
    ...shared,

    // Blue-grey rather than pure neutral: white cards lift off it, and it is easier on the
    // eye than a flat #f5f5f5 over a long session. TailAdmin's dashboard ground.
    colorBgLayout: '#F1F5F9',
    colorBgContainer: '#FFFFFF',
    colorBorderSecondary: STROKE,
    colorBorder: STROKE,
    colorTextBase: '#1C2434',
  },
  components: {
    Layout: {
      headerBg: '#FFFFFF',
      headerHeight: 64,
      siderBg: SIDEBAR_BG,
      bodyBg: '#F1F5F9',
    },
    Menu: {
      darkItemBg: SIDEBAR_BG,
      darkSubMenuItemBg: SIDEBAR_BG,

      // TailAdmin marks the current page with a lighter panel rather than a saturated fill.
      // Against a navy sidebar a solid brand block is the loudest thing on the screen, which
      // is the wrong amount of emphasis for "you are here".
      darkItemSelectedBg: 'rgba(255, 255, 255, 0.08)',
      darkItemSelectedColor: '#FFFFFF',
      darkItemColor: SIDEBAR_TEXT,
      darkItemHoverBg: 'rgba(255, 255, 255, 0.06)',
      darkItemHoverColor: SIDEBAR_TEXT_ACTIVE,
      itemHeight: 44,
      itemMarginInline: 12,

      // The glyphs here are 18px, wider than Ant's 14px default slot, so without this the icon
      // overflows into its own label and the two touch.
      iconSize: 18,
      iconMarginInlineEnd: 12,
    },
    Card: {
      headerBg: 'transparent',
      headerFontSize: 15,
      paddingLG: 20,
    },
    Table: {
      headerBg: '#F7F9FC',
      headerColor: '#64748B',
      rowHoverBg: '#F7F9FC',
      cellPaddingBlock: 14,

      // The sort indicator belongs in the header. Shading the whole column in the body puts
      // the page's heaviest emphasis on whichever column is sorted, which by default is the
      // least important one on the screen.
      bodySortBg: 'transparent',
      headerSortActiveBg: '#EFF4FB',
      headerSortHoverBg: '#EFF4FB',
    },
  },
};

export const darkTheme: ThemeConfig = {
  algorithm: antTheme.darkAlgorithm,
  token: {
    ...shared,
    colorBgLayout: DARK_GROUND,
    colorBgContainer: DARK_SURFACE,
    colorBorderSecondary: STROKE_DARK,
    colorBorder: STROKE_DARK,
  },
  components: {
    Layout: {
      headerBg: DARK_SURFACE,
      headerHeight: 64,
      siderBg: SIDEBAR_BG,
      bodyBg: DARK_GROUND,
    },
    Menu: {
      darkItemBg: SIDEBAR_BG,
      darkSubMenuItemBg: SIDEBAR_BG,
      darkItemSelectedBg: 'rgba(255, 255, 255, 0.08)',
      darkItemSelectedColor: '#FFFFFF',
      darkItemColor: SIDEBAR_TEXT,
      darkItemHoverBg: 'rgba(255, 255, 255, 0.06)',
      darkItemHoverColor: SIDEBAR_TEXT_ACTIVE,
      itemHeight: 44,
      itemMarginInline: 12,

      // The glyphs here are 18px, wider than Ant's 14px default slot, so without this the icon
      // overflows into its own label and the two touch.
      iconSize: 18,
      iconMarginInlineEnd: 12,
    },
    Card: {
      headerBg: 'transparent',
      headerFontSize: 15,
      paddingLG: 20,
    },
    Table: {
      headerBg: '#1F2A38',
      rowHoverBg: '#1F2A38',
      cellPaddingBlock: 14,
      bodySortBg: 'transparent',
      headerSortActiveBg: '#22303F',
      headerSortHoverBg: '#22303F',
    },
  },
};

/** The divider colour the shell's own borders read from, per theme. */
export const strokeFor = (mode: 'light' | 'dark'): string =>
  (mode === 'dark' ? STROKE_DARK : STROKE);

/**
 * The page ground, for the one screen that is not inside a Layout.
 *
 * `colorBgLayout` reaches Ant's Layout and nothing else, so the sign-in screen sat on the
 * browser's default white while every other screen sat on blue-grey - and its card had nothing
 * to lift off. Painting the body covers both.
 */
export const groundFor = (mode: 'light' | 'dark'): string =>
  (mode === 'dark' ? DARK_GROUND : '#F1F5F9');

const STORAGE_KEY = 'cardealer.theme';
const SIDEBAR_KEY = 'cardealer.sidebar';

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

/**
 * Whether the sidebar was left collapsed.
 *
 * Remembered for the same reason the theme is: someone who works with it collapsed does not
 * want to collapse it again every morning. Defaults to expanded, because a first-time user
 * needs to read the labels before the icons mean anything.
 */
export function readSidebarCollapsed(): boolean {
  try {
    return localStorage.getItem(SIDEBAR_KEY) === 'collapsed';
  } catch {
    return false;
  }
}

export function storeSidebarCollapsed(collapsed: boolean): void {
  try {
    localStorage.setItem(SIDEBAR_KEY, collapsed ? 'collapsed' : 'expanded');
  } catch {
    // Same as the theme: the choice holds for this session and no further.
  }
}
