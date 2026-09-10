/**
 * The handful of glyphs used outside the navigation rail.
 *
 * Inline SVG for the same reason the rail's are: a dozen icons do not justify a dependency
 * whose tree-shaken bundle is still tens of kilobytes. Shared from here rather than redeclared
 * per screen, so a delete button looks like a delete button everywhere.
 */

const stroke = {
  fill: 'none',
  stroke: 'currentColor',
  strokeWidth: 1.8,
  strokeLinecap: 'round' as const,
  strokeLinejoin: 'round' as const,
};

export function TrashGlyph() {
  return (
    <svg width="14" height="14" viewBox="0 0 24 24" aria-hidden="true">
      <path {...stroke} d="M4 7h16" />
      <path {...stroke} d="M10 11v6M14 11v6" />
      <path {...stroke} d="M6 7l1 13a1 1 0 001 1h8a1 1 0 001-1l1-13" />
      <path {...stroke} d="M9 7V4h6v3" />
    </svg>
  );
}

export function PlusGlyph() {
  return (
    <svg width="14" height="14" viewBox="0 0 24 24" aria-hidden="true">
      <path {...stroke} d="M12 5v14M5 12h14" />
    </svg>
  );
}

export function DownloadGlyph() {
  return (
    <svg width="14" height="14" viewBox="0 0 24 24" aria-hidden="true">
      <path {...stroke} d="M12 3v12" />
      <path {...stroke} d="M8 11l4 4 4-4" />
      <path {...stroke} d="M4 18v1a2 2 0 002 2h12a2 2 0 002-2v-1" />
    </svg>
  );
}

/** A pencil, for editing a record in place. */
export function PencilGlyph() {
  return (
    <svg width="14" height="14" viewBox="0 0 24 24" aria-hidden="true">
      <path {...stroke} d="M4 20h4l10-10a2.8 2.8 0 10-4-4L4 16v4z" />
      <path {...stroke} d="M13.5 6.5l4 4" />
    </svg>
  );
}
