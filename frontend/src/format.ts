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

/**
 * How an enum value from the API reads on screen.
 *
 * The API sends names, not numbers - "ContinuouslyVariable", never 2 - which is what stops a
 * renumbering on the server from silently changing what a filter means. The cost is that the
 * name is a C# identifier, and a screen that prints it unchanged shows the customer
 * "ContinuouslyVariable" where the trade says CVT, and "FrontWheelDrive" where it says FWD.
 *
 * One table rather than one per screen, for the same reason `formatMoney` is shared: the search
 * chips already translated these and the cards beside them did not, so the same car read two
 * ways on one screen.
 *
 * Values needing no translation are absent on purpose - "Petrol" and "Automatic" are already
 * the words a person uses, and listing them would only invite the table to drift from the enum.
 */
const SPEC_LABELS: Record<string, string> = {
  // Steering, spelled out. The cards want the code instead - see steeringShort.
  RightHandDrive: 'Right-hand drive',
  LeftHandDrive: 'Left-hand drive',

  // Fuel.
  PluginHybrid: 'Plug-in hybrid',
  Lpg: 'LPG',
  Cng: 'CNG',

  // Transmission.
  ContinuouslyVariable: 'CVT',
  SemiAutomatic: 'Semi-automatic',
  DualClutch: 'Dual clutch',

  // Drivetrain, as codes: this trade writes FWD and 4WD, not the words.
  FrontWheelDrive: 'FWD',
  RearWheelDrive: 'RWD',
  AllWheelDrive: 'AWD',
  FourWheelDrive: '4WD',

  // Incoterms, which read as codes in this trade rather than as prose.
  ExWorks: 'EXW',
  FreeOnBoard: 'FOB',
  CostAndFreight: 'CFR',
  CostInsuranceFreight: 'CIF',

  // Nothing known. An em dash says that; the word "Unknown" says the record is broken.
  Unknown: '—',
};

/**
 * A spec value in the words a person uses, falling back to the value itself.
 *
 * The fallback is the important half: an enum member added on the server and not added here
 * shows as its name, which is ugly but true. Mapping the unknown to "—" would hide a real
 * value behind a blank.
 */
export function specLabel(value: string | null | undefined): string {
  if (!value) return '—';

  return SPEC_LABELS[value] ?? value;
}

/**
 * Steering as a two- or three-letter code, or null when it is not known.
 *
 * The card has room for three specs on one line, so it wants RHD; the detail page has a whole
 * row per spec and wants "Right-hand drive". Null rather than "—" because a card drops the
 * chip entirely instead of showing a dash.
 */
export function steeringShort(value: string): string | null {
  if (value === 'RightHandDrive') return 'RHD';
  if (value === 'LeftHandDrive') return 'LHD';

  return null;
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
