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
