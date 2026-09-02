/**
 * Formats a UTC timestamp from the API in the viewer's local time.
 *
 * The API stamps every ...Utc field with a trailing Z, so `new Date` reads it correctly. This
 * wrapper exists to keep that assumption in one place and to be defensive about the one case
 * that used to break it: a zone-less string, which JavaScript parses as *local* time. If one
 * ever reaches here again, treat it as UTC rather than silently shifting it by the viewer's
 * offset - a Karachi viewer would otherwise see every sync time five hours out.
 */
export function formatUtc(value: string | null): string {
  if (!value) return '—';

  const hasZone = /(?:Z|[+-]\d{2}:?\d{2})$/i.test(value);
  const parsed = new Date(hasZone ? value : `${value}Z`);

  return Number.isNaN(parsed.getTime()) ? '—' : parsed.toLocaleString();
}

/**
 * How old a record is, in days, or null when the timestamp is unusable.
 *
 * Data age is shown on every card because invisible staleness is what made the previous source
 * unusable: it published each listing once and never revisited, so a car sold six weeks ago
 * still read as for sale and nothing on screen said otherwise. Stale data a user can see is a
 * far smaller problem than stale data they cannot.
 */
export function ageInDays(value: string | null): number | null {
  if (!value) return null;

  const hasZone = /(?:Z|[+-]\d{2}:?\d{2})$/i.test(value);
  const parsed = new Date(hasZone ? value : `${value}Z`);

  if (Number.isNaN(parsed.getTime())) return null;

  return Math.floor((Date.now() - parsed.getTime()) / 86_400_000);
}

/** Past this, a listing is old enough that its availability should not be trusted. */
export const STALE_AFTER_DAYS = 14;
