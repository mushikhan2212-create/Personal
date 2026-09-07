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

/**
 * A price with its currency, or an em dash when there is none.
 *
 * Shared rather than local to one component because a price rendered two ways on two screens
 * is a price a user has to reconcile. Falls back to the plain number when the code is not one
 * Intl recognises: an unfamiliar currency should show the amount, not crash the grid.
 */
export function formatMoney(amount: number | null, currency: string | null): string {
  if (amount === null) return '—';

  try {
    return new Intl.NumberFormat(undefined, {
      style: 'currency',
      currency: currency ?? 'USD',
      maximumFractionDigits: 0,
    }).format(amount);
  } catch {
    return `${amount.toLocaleString()} ${currency ?? ''}`.trim();
  }
}

/** How this listing's age should read, and whether it is old enough to distrust. */
export function describeAge(lastSeenAtUtc: string): { label: string; isStale: boolean } {
  const age = ageInDays(lastSeenAtUtc);

  if (age === null) return { label: 'Age unknown', isStale: false };

  return {
    label: age <= 0 ? 'Today' : `${age}d ago`,
    isStale: age > STALE_AFTER_DAYS,
  };
}
