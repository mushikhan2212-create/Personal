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
