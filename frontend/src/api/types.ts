// Contracts mirrored from the API's OpenAPI document.
//
// Hand-written for the POC, and deliberately narrow: only what the search screen consumes.
// Decision D8 chose TypeScript so that a frontend/backend mismatch is a build error rather
// than a blank cell, and the intended end state is generating this file from
// /swagger/v1/swagger.json as part of the build. Until that generator is wired, these types
// have to be kept in step with VehiclesController by hand.
//
// Enums arrive as names, not numbers - the API sends "RightHandDrive", never 1 - so a
// renumbering on the server cannot silently change what a filter means here.

export type SteeringSide = 'Unknown' | 'RightHandDrive' | 'LeftHandDrive';

export type FuelType =
  | 'Unknown' | 'Petrol' | 'Diesel' | 'Hybrid' | 'PluginHybrid'
  | 'Electric' | 'Lpg' | 'Cng' | 'Hydrogen';

export type Transmission =
  | 'Unknown' | 'Manual' | 'Automatic' | 'ContinuouslyVariable'
  | 'SemiAutomatic' | 'DualClutch';

export type MileageUnit = 'Unknown' | 'Kilometers' | 'Miles';

/** Incoterm. Rendered beside every price - FOB and CIF differ by the whole cost of shipping. */
export type PriceType = 'Unknown' | 'ExWorks' | 'FreeOnBoard' | 'CostAndFreight' | 'CostInsuranceFreight';

export type VehicleSearchSort =
  | 'RecentlySeen' | 'PriceAscending' | 'PriceDescending'
  | 'YearDescending' | 'MileageAscending';

export interface VehicleSummary {
  id: string;
  make: string | null;
  model: string | null;
  variant: string | null;
  year: number | null;
  mileage: number | null;
  mileageUnit: MileageUnit;
  steeringSide: SteeringSide;
  fuelType: FuelType;
  transmission: Transmission;
  price: number | null;
  currencyCode: string | null;
  priceBaseCurrency: number | null;
  baseCurrencyCode: string | null;
  priceType: PriceType;
  sourceName: string | null;
  sourceUrl: string | null;
  /** How many listings offer this car, and from how many distinct sources. */
  offerCount: number;
  sourceCount: number;
  imageUrl: string | null;
  lastSeenAtUtc: string;
  tenantPrice: number | null;
  tenantCurrencyCode: string | null;
}

export interface VehicleSearchResponse {
  items: VehicleSummary[];
  totalCount: number;
  page: number;
  pageSize: number;
  totalPages: number;
  /** Server-side query time. Shown in the UI because it is a measured POC criterion. */
  elapsedMilliseconds: number;
}

export interface TenantSummary {
  publicId: string;
  slug: string;
  name: string;
}

export interface LoginResponse {
  requiresTenantSelection: boolean;
  accessToken: string | null;
  refreshToken: string | null;
  activeTenant: TenantSummary | null;
  availableTenants: TenantSummary[];
  permissions: string[];
}

export interface VehicleSourceSummary {
  code: string;
  name: string;
  providerType: string;
  isShared: boolean;
  isActive: boolean;
  vehicleCount: number;
  /** Last run that actually brought data in - null if none ever has. */
  lastSyncAtUtc: string | null;
  /** Last run of any kind, so a source that only ever fails is distinguishable from a new one. */
  lastAttemptAtUtc: string | null;
  lastAttemptStatus: SyncJobStatus | null;
}

export type SyncJobStatus =
  | 'Unknown' | 'Pending' | 'Running' | 'Succeeded' | 'PartiallySucceeded' | 'Failed';

export interface SyncResult {
  syncJobId: number;
  status: string;
  totalRecords: number;
  created: number;
  updated: number;
  failed: number;
  autoMerged: number;
  /** How many records arrived with no strong identifier - what dedup cannot help with. */
  withoutStrongIdentifier: number;
  pagesFetched: number;
  requestCount: number;
  elapsedMs: number;
  errorMessage: string | null;
}

export interface VehicleDetailListing {
  sourceName: string | null;
  sourceUrl: string | null;
  externalListingId: string | null;
  price: number | null;
  currencyCode: string | null;
  priceBaseCurrency: number | null;
  baseCurrencyCode: string | null;
  priceType: PriceType;
  portOfLoading: string | null;
  locationCountryCode: string | null;
  isActive: boolean;
  firstSeenAtUtc: string;
  lastSeenAtUtc: string;
}

export type CanonicalHashSource = 'Unknown' | 'Vin' | 'ChassisNumber' | 'SourceLotNumber';

export interface VehicleDetail {
  id: string;
  make: string | null;
  model: string | null;
  variant: string | null;
  year: number | null;
  bodyType: string | null;
  engineDisplacementCc: number | null;
  mileage: number | null;
  mileageUnit: MileageUnit;
  steeringSide: SteeringSide;
  fuelType: FuelType;
  transmission: Transmission;
  drivetrain: string;
  status: string;
  exteriorColor: string | null;
  interiorColor: string | null;
  condition: string | null;
  auctionGrade: string | null;
  vin: string | null;
  chassisNumber: string | null;
  lotNumber: string | null;
  /** Which identifier deduplication matched on, or null when nothing could be matched. */
  canonicalHashSource: CanonicalHashSource | null;
  imageUrls: string[];
  listings: VehicleDetailListing[];
  tenantPrice: number | null;
  tenantCurrencyCode: string | null;
  internalNotes: string | null;
}

export interface ImportResult extends SyncResult {
  dryRun: boolean;
  recordsInFile: number;
  storageReference: string | null;
  skippedOutOfScope: number;
}

export interface MySource {
  code: string;
  name: string;
  providerType: string;
  isShared: boolean;
  vehicleCount: number;
  /** Last run that actually brought data in; null if none ever has. */
  lastSyncAtUtc: string | null;
  /** Status of the last attempt, whatever became of it, so a failure is visible. */
  lastAttemptStatus: string | null;
  /** Whether this source feeds *your* searches. Nobody else is affected by it. */
  isEnabled: boolean;
}

// --- Phase 1 CRM -------------------------------------------------------------------------

export type CustomerStatus = 'Unknown' | 'Lead' | 'Active' | 'Customer' | 'Dormant' | 'Closed';

export type LeadSource =
  | 'Unknown' | 'WalkIn' | 'Referral' | 'Website'
  | 'WhatsApp' | 'SocialMedia' | 'Marketplace' | 'Repeat';

export type RequirementStatus = 'Unknown' | 'Open' | 'OnHold' | 'Fulfilled' | 'Cancelled';

export interface CustomerListItem {
  publicId: string;
  firstName: string | null;
  lastName: string | null;
  phone: string | null;
  email: string | null;
  countryCode: string | null;
  city: string | null;
  status: CustomerStatus;
  leadSource: LeadSource;
  /** How many requirements are still being shopped, so a list row shows who needs work. */
  openRequirements: number;
  updatedAtUtc: string;
}

export interface CustomerListResponse {
  items: CustomerListItem[];
  totalCount: number;
  page: number;
  pageSize: number;
}

export interface Requirement {
  id: number;
  name: string | null;
  make: string | null;
  model: string | null;
  variant: string | null;
  bodyType: string | null;
  exteriorColor: string | null;
  minYear: number | null;
  maxYear: number | null;
  minMileage: number | null;
  maxMileage: number | null;
  transmission: Transmission | null;
  fuelType: FuelType | null;
  minPrice: number | null;
  maxPrice: number | null;
  currencyCode: string | null;
  destinationCountryCode: string | null;
  destinationCity: string | null;
  rawRequirementText: string | null;
  status: RequirementStatus;
  updatedAtUtc: string;
}

export interface CustomerDetail {
  publicId: string;
  firstName: string | null;
  lastName: string | null;
  phone: string | null;
  email: string | null;
  countryCode: string | null;
  city: string | null;
  preferredLanguage: string | null;
  status: CustomerStatus;
  leadSource: LeadSource;
  /**
   * The note log, newest first.
   *
   * Replaces the single free-text box this used to be — see the entity remarks. What that box
   * held was moved into the log by a migration, so nothing was lost.
   */
  notes: CustomerNote[];
  assignedUserId: number | null;
  createdAtUtc: string;
  updatedAtUtc: string;
  requirements: Requirement[];
}

/** What the API accepts for a customer. Every field optional; the API requires one of four. */
export interface CustomerInput {
  firstName?: string;
  lastName?: string;
  phone?: string;
  email?: string;
  countryCode?: string;
  city?: string;
  preferredLanguage?: string;
  status?: CustomerStatus;
  leadSource?: LeadSource;

  /**
   * Who holds this customer.
   *
   * No screen sets it yet, but it belongs on the input: the update endpoint applies every
   * field it is given, so a request that omits this clears it. An edit form has to send back
   * what it loaded.
   */
  assignedUserId?: number;

  /**
   * The opening note, on create only.
   *
   * Starts the customer's note log rather than filling a field on the record. The update path
   * ignores it — notes are edited through their own endpoints once the customer exists.
   */
  notes?: string;
}

export interface CustomerNote {
  id: number;
  body: string;
  createdAtUtc: string;
  /** Set when somebody changed the text afterwards, which is weaker evidence than the original. */
  editedAtUtc: string | null;
  /** Null for a note the platform moved here rather than a person typing it. */
  author: string | null;
}

export interface RequirementInput {
  name?: string;
  make?: string;
  model?: string;
  bodyType?: string;
  minYear?: number;
  maxYear?: number;
  minMileage?: number;
  maxMileage?: number;
  transmission?: Transmission;
  fuelType?: FuelType;
  minPrice?: number;
  maxPrice?: number;
  currencyCode?: string;
  destinationCountryCode?: string;
  rawRequirementText?: string;
  status?: RequirementStatus;
}

export interface RequirementMatches extends VehicleSearchResponse {
  requirementId: number;
  /**
   * Which of the requirement's fields actually narrowed the search, in the API's own words.
   *
   * Shown rather than inferred client-side, because the server is the only place that knows
   * what it applied - including what it deliberately did not, such as destination country.
   */
  matchedOn: string[];
}

/** One row the import could not use, or chose not to. */
export interface ImportProblem {
  /** 1-based position among the file's customers, not its text lines. */
  row: number;
  /** How the row identifies itself — a name, or failing that a phone or email. */
  label: string;
  message: string;
}

export interface CustomerImportResult {
  dryRun: boolean;
  totalRows: number;
  created: number;
  /** Rows skipped because that person is already on the books. Never overwritten. */
  duplicates: number;
  invalid: number;
  /** A few names that would be added, so a dry run is checkable at a glance. */
  sample: string[];
  problems: ImportProblem[];
  /** Problems past the reporting cap, counted rather than listed. */
  unreportedProblems: number;
}

/** A car that turned up after a customer asked for it (open item O11). */
export interface RequirementAlertItem {
  id: number;
  matchedAtUtc: string;
  /** Null until somebody has looked at it. */
  seenAtUtc: string | null;
  /** The price when the alert was raised, not the price now. */
  priceBaseAtMatch: number | null;
  baseCurrencyCode: string | null;
  customer: {
    publicId: string;
    firstName: string | null;
    lastName: string | null;
    phone: string | null;
  };
  requirement: {
    customerRequirementId: number;
    name: string | null;
    make: string | null;
    model: string | null;
  };
  vehicle: {
    publicId: string;
    make: string | null;
    model: string | null;
    variant: string | null;
    year: number | null;
    mileage: number | null;
    mileageUnit: MileageUnit;
    imageUrl: string | null;
  };
}

export interface AlertListResponse {
  items: RequirementAlertItem[];
  totalCount: number;
  page: number;
  pageSize: number;
}

export interface AlertScanResult {
  requirementsScanned: number;
  alertsRaised: number;
}

/**
 * A message prepared for a customer.
 *
 * `canSendDirectly` is false while the platform is on click-to-chat links: a person taps and
 * sends from their own phone. It flips to true when the WhatsApp Business API is in place, and
 * the screens read it rather than assuming — so the button can say what will actually happen.
 */
export interface MessageDraft {
  channel: string;
  canSendDirectly: boolean;
  canReceive: boolean;
  /** The number as stored on the customer. */
  to: string | null;
  /** Digits-only international form, or null when it could not be determined. */
  normalizedPhone: string | null;
  body: string;
  canSend: boolean;
  handoffUrl: string | null;
  /** Why no message could be prepared, in words a salesperson can act on. */
  reason: string | null;
  /**
   * The car's photos, for the salesperson to attach in WhatsApp.
   *
   * Beside the message rather than in it: click-to-chat carries text only, and putting the
   * exporter's image URL in the text would name the supplier to the customer.
   */
  photos: MessagePhoto[];
}

export interface MessagePhoto {
  index: number;
  /** The source's own address, for showing a thumbnail here. */
  url: string;
  /** Through our API, so the browser saves the file instead of opening it in a tab. */
  downloadUrl: string | null;
}

// --- Duplicate review (open item O15) -------------------------------------------------

/**
 * One side of a suggested duplicate: enough of the car to judge it without leaving the queue.
 */
export interface DuplicateSide {
  id: number;
  publicId: string;
  make: string | null;
  model: string | null;
  variant: string | null;
  modelYear: number | null;
  mileage: number | null;
  mileageUnit: MileageUnit;
  exteriorColor: string | null;
  engineDisplacementCc: number | null;
  fuelType: FuelType;
  transmission: Transmission;
  steeringSide: SteeringSide;
  status: string;
  createdAtUtc: string;
  offers: DuplicateOffer[];
  photo: string | null;
}

export interface DuplicateOffer {
  id: number;
  source: string | null;
  price: number | null;
  currencyCode: string | null;
  priceType: PriceType;
  sourceUrl: string | null;
}

/**
 * Why the platform thinks two rows are one car.
 *
 * A negative weight is a contradiction rather than a missing signal — "1598 cc vs 1798 cc"
 * counts against the pair, and the reviewer should see that as plainly as the agreements.
 */
export interface DuplicateSignal {
  name: string;
  weight: number;
  detail: string;
}

export interface DuplicateCandidate {
  id: number;
  /** 0 to 1. Nothing below 0.5 is ever written. */
  score: number;
  status: 'Pending' | 'Merged' | 'Rejected' | 'Unknown';
  createdAtUtc: string;
  reviewedAtUtc: string | null;
  signals: DuplicateSignal[];
  left: DuplicateSide;
  right: DuplicateSide;
}

export interface DuplicateQueue {
  totalCount: number;
  page: number;
  pageSize: number;
  items: DuplicateCandidate[];
}

export interface MergeRecord {
  id: number;
  mergedAtUtc: string;
  revertedAtUtc: string | null;
  mergedBy: string | null;
  surviving: { publicId: string; make: string | null; model: string | null; modelYear: number | null } | null;
  archived: { publicId: string; make: string | null; model: string | null; modelYear: number | null } | null;
}
