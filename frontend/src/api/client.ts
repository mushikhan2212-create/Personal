import type {
  AlertListResponse, AlertScanResult, MessageDraft,
  CustomerDetail, CustomerImportResult, CustomerInput, CustomerListResponse, CustomerStatus,
  ImportResult, LoginResponse, MySource, RequirementInput, RequirementMatches, SyncResult,
  VehicleDetail, VehicleSearchResponse, VehicleSearchSort, VehicleSourceSummary,
  DuplicateQueue, MergeRecord, MessageTemplateList,
} from './types';

/**
 * Thin API client.
 *
 * Tokens live in memory only. Putting them in localStorage would make them readable by any
 * script on the page, and a POC is exactly where that habit gets set. The cost is that a page
 * reload signs you out, which is the right trade for a token that is a bearer capability.
 */
let accessToken: string | null = null;
let refreshToken: string | null = null;
let onSessionLost: (() => void) | null = null;

export const setTokens = (access: string | null, refresh: string | null): void => {
  accessToken = access;
  refreshToken = refresh;
};

/** Called when the session cannot be renewed and the user has to sign in again. */
export const setSessionLostHandler = (handler: (() => void) | null): void => {
  onSessionLost = handler;
};

export class ApiError extends Error {
  constructor(readonly status: number, message: string) {
    super(message);
    this.name = 'ApiError';
  }
}

/**
 * The one in-flight refresh, shared by every caller.
 *
 * The API rotates the refresh token on use and treats a replay of an already-rotated token as
 * theft, revoking the whole chain. Two requests expiring at the same moment - which is exactly
 * what happens when the search screen loads vehicles and sources together - would each present
 * the same token and log the user out for good. Sharing one promise makes that impossible.
 */
let refreshInFlight: Promise<boolean> | null = null;

async function refreshTokens(): Promise<boolean> {
  if (refreshToken === null) return false;

  refreshInFlight ??= (async () => {
    try {
      const response = await fetch('/api/v1/auth/refresh', {
        method: 'POST',
        headers: { 'Content-Type': 'application/json' },
        body: JSON.stringify({ refreshToken }),
      });

      if (!response.ok) return false;

      const result = (await response.json()) as LoginResponse;

      if (!result.accessToken) return false;

      setTokens(result.accessToken, result.refreshToken);
      return true;
    } catch {
      return false;
    } finally {
      refreshInFlight = null;
    }
  })();

  return refreshInFlight;
}

/** The Authorization header, or nothing when signed out. */
function accessTokenHeader(): Record<string, string> {
  return accessToken ? { Authorization: `Bearer ${accessToken}` } : {};
}

async function send(path: string, init?: RequestInit): Promise<Response> {
  return fetch(`/api/v1${path}`, {
    ...init,
    headers: {
      'Content-Type': 'application/json',
      ...(accessToken ? { Authorization: `Bearer ${accessToken}` } : {}),
      ...init?.headers,
    },
  });
}

async function request<T>(path: string, init?: RequestInit): Promise<T> {
  let response = await send(path, init);

  // 401 means expired, not forbidden - a permission failure is 403 and must not be retried.
  if (response.status === 401 && refreshToken !== null) {
    response = (await refreshTokens())
      ? await send(path, init)
      : response;
  }

  if (!response.ok) {
    if (response.status === 401) {
      setTokens(null, null);
      onSessionLost?.();
    }

    // The API returns RFC 9110 problem+json with a correlationId. Surfacing the title keeps
    // the message useful without inventing one.
    let message = `Request failed with ${response.status}.`;

    try {
      const problem = (await response.json()) as { title?: string; detail?: string };
      message = problem.detail ?? problem.title ?? message;
    } catch {
      // A non-JSON body is not worth failing over; the status carries the meaning.
    }

    throw new ApiError(response.status, message);
  }

  return response.status === 204 ? (undefined as T) : ((await response.json()) as T);
}

export const login = (email: string, password: string, tenantSlug?: string): Promise<LoginResponse> =>
  request<LoginResponse>('/auth/login', {
    method: 'POST',
    body: JSON.stringify({ email, password, ...(tenantSlug ? { tenantSlug } : {}) }),
  });

export interface SearchParams {
  q?: string;
  /**
   * Make and model as their own filters, separate from `q`.
   *
   * They mean different things: "corolla" in free text also matches a variant string that
   * mentions it, which is right when browsing and wrong when a customer has asked for a
   * Corolla. A saved requirement filters on these, so the search screen has to be able to
   * express the same thing — a requirement nobody can reproduce by hand is a requirement
   * nobody can check.
   */
  make?: string;
  model?: string;
  bodyType?: string;
  minYear?: number;
  maxYear?: number;
  minMileage?: number;
  maxMileage?: number;
  steeringSide?: string;
  fuelType?: string;
  transmission?: string;
  minPrice?: number;
  maxPrice?: number;
  page?: number;
  pageSize?: number;
  sort?: VehicleSearchSort;
}

export const searchVehicles = (params: SearchParams): Promise<VehicleSearchResponse> => {
  const query = new URLSearchParams();

  for (const [key, value] of Object.entries(params)) {
    // An empty filter is an absent one. Sending "" would be a value, and the API is strict
    // about unrecognised or malformed parameters.
    if (value !== undefined && value !== null && value !== '') {
      query.set(key, String(value));
    }
  }

  return request<VehicleSearchResponse>(`/vehicles?${query}`);
};

export const listSources = (): Promise<VehicleSourceSummary[]> =>
  request<VehicleSourceSummary[]>('/vehicle-sources');

export const syncSource = (
  code: string, maxPages: number, fetchDetail: boolean,
): Promise<SyncResult> =>
  request<SyncResult>(
    `/vehicle-sources/${encodeURIComponent(code)}/sync?maxPages=${maxPages}&fetchDetail=${fetchDetail}`,
    { method: 'POST' },
  );

export interface CreateSourceRequest {
  code: string;
  name: string;
  providerType?: string;
  sourceType?: string;
  baseUrl?: string;
  isShared?: boolean;
}

/**
 * Registers a vehicle source.
 *
 * Defaults to a shared DealerJson/File source, because that is the only combination an import
 * can actually use: the sync pipeline picks its normalizer from the provider type, so a source
 * registered as anything else cannot read the import format.
 */
export const createSource = (source: CreateSourceRequest): Promise<VehicleSourceSummary> =>
  request<VehicleSourceSummary>('/vehicle-sources', {
    method: 'POST',
    body: JSON.stringify({
      providerType: 'DealerJson',
      sourceType: 'File',
      isShared: true,
      ...source,
    }),
  });

export interface SourceRemoval {
  code: string;
  listingsDeleted: number;
  vehiclesDeleted: number;
  vehiclesKept: number;
  imagesDeleted: number;
  syncJobsDeleted: number;
  tenantOverlaysDeleted: number;
}

/**
 * Deletes a source and the catalog data only it was holding up.
 *
 * The code is repeated as `confirm` because the API insists on it: this is irreversible, so
 * the request has to name what it destroys rather than being one mis-click.
 */
export const deleteSource = (code: string): Promise<SourceRemoval> =>
  request<SourceRemoval>(
    `/vehicle-sources/${encodeURIComponent(code)}?confirm=${encodeURIComponent(code)}`,
    { method: 'DELETE' },
  );

export const listMySources = (): Promise<MySource[]> => request<MySource[]>('/me/sources');

export const setMySource = (code: string, isEnabled: boolean): Promise<unknown> =>
  request(`/me/sources/${encodeURIComponent(code)}`, {
    method: 'PUT',
    body: JSON.stringify({ isEnabled }),
  });

export const getVehicle = (id: string): Promise<VehicleDetail> =>
  request<VehicleDetail>(`/vehicles/${encodeURIComponent(id)}`);

/**
 * Uploads an import document.
 *
 * Deliberately not routed through `request`: that helper sets a JSON content type, and a
 * multipart body must let the browser set its own boundary. Overriding it by hand is how you
 * get a request the server cannot parse.
 */
export async function importFile(
  code: string, file: File, dryRun: boolean,
): Promise<ImportResult> {
  const body = new FormData();
  body.append('file', file);

  const response = await fetch(
    `/api/v1/vehicle-sources/${encodeURIComponent(code)}/import?dryRun=${dryRun}`,
    {
      method: 'POST',
      headers: accessTokenHeader(),
      body,
    },
  );

  if (!response.ok) {
    let message = `Import failed with ${response.status}.`;

    try {
      const problem = (await response.json()) as { title?: string; detail?: string };
      message = [problem.title, problem.detail].filter(Boolean).join(' ') || message;
    } catch {
      // Non-JSON body; the status carries the meaning.
    }

    throw new ApiError(response.status, message);
  }

  return (await response.json()) as ImportResult;
}

// --- Phase 1 CRM -------------------------------------------------------------------------

export const listCustomers = (
  q: string, status: CustomerStatus | undefined, page: number, pageSize: number,
): Promise<CustomerListResponse> => {
  const params = new URLSearchParams({ page: String(page), pageSize: String(pageSize) });

  if (q.trim()) params.set('q', q.trim());
  if (status) params.set('status', status);

  return request<CustomerListResponse>(`/customers?${params}`);
};

export const getCustomer = (publicId: string): Promise<CustomerDetail> =>
  request<CustomerDetail>(`/customers/${publicId}`);

export const createCustomer = (input: CustomerInput): Promise<{ publicId: string }> =>
  request<{ publicId: string }>('/customers', {
    method: 'POST',
    body: JSON.stringify(input),
  });

export const updateCustomer = (publicId: string, input: CustomerInput): Promise<unknown> =>
  request(`/customers/${publicId}`, { method: 'PUT', body: JSON.stringify(input) });

export const deleteCustomer = (publicId: string): Promise<unknown> =>
  request(`/customers/${publicId}`, { method: 'DELETE' });

export const addRequirement = (
  publicId: string, input: RequirementInput,
): Promise<{ id: number }> =>
  request<{ id: number }>(`/customers/${publicId}/requirements`, {
    method: 'POST',
    body: JSON.stringify(input),
  });

export const deleteRequirement = (publicId: string, id: number): Promise<unknown> =>
  request(`/customers/${publicId}/requirements/${id}`, { method: 'DELETE' });

export const getMatches = (
  publicId: string, id: number, page = 1, pageSize = 25,
): Promise<RequirementMatches> =>
  request<RequirementMatches>(
    `/customers/${publicId}/requirements/${id}/matches?page=${page}&pageSize=${pageSize}`);

/**
 * Uploads a customer list.
 *
 * Multipart for the same reason as the vehicle import: `request` sets a JSON content type, and
 * a multipart body needs the browser to set its own boundary.
 */
export async function importCustomers(
  file: File, dryRun: boolean,
): Promise<CustomerImportResult> {
  const body = new FormData();
  body.append('file', file);

  const response = await fetch(`/api/v1/customers/import?dryRun=${dryRun}`, {
    method: 'POST',
    headers: accessTokenHeader(),
    body,
  });

  if (!response.ok) {
    let message = `Import failed with ${response.status}.`;

    try {
      const problem = (await response.json()) as { title?: string; detail?: string };
      message = [problem.title, problem.detail].filter(Boolean).join(' ') || message;
    } catch {
      // Non-JSON body; the status carries the meaning.
    }

    throw new ApiError(response.status, message);
  }

  return (await response.json()) as CustomerImportResult;
}

// --- Requirement alerts (O11) -------------------------------------------------------------

export const countAlerts = (): Promise<{ unseen: number }> =>
  request<{ unseen: number }>('/alerts/count');

export const listAlerts = (
  unseenOnly: boolean, page: number, pageSize: number,
): Promise<AlertListResponse> =>
  request<AlertListResponse>(
    `/alerts?unseenOnly=${unseenOnly}&page=${page}&pageSize=${pageSize}`);

export const markAlertSeen = (id: string): Promise<unknown> =>
  request(`/alerts/${id}/seen`, { method: 'POST' });

export const markAllAlertsSeen = (): Promise<{ marked: number }> =>
  request<{ marked: number }>('/alerts/seen', { method: 'POST' });

/** Runs the scan now rather than waiting for the hourly job. Idempotent. */
export const scanForAlerts = (): Promise<AlertScanResult> =>
  request<AlertScanResult>('/alerts/scan', { method: 'POST' });

// --- Messaging ----------------------------------------------------------------------------

/**
 * Prepares a WhatsApp message, optionally about a car.
 *
 * Called again on each edit so the link always matches the text on screen. The link is built
 * server-side rather than in the browser: the phone normalisation that decides whether a number
 * can be reached at all is one rule, tested in one place.
 */
export const draftWhatsApp = (
  customerPublicId: string,
  vehiclePublicId?: string,
  body?: string,
  templatePublicId?: string,
): Promise<MessageDraft> =>
  request<MessageDraft>('/messaging/whatsapp/draft', {
    method: 'POST',
    body: JSON.stringify({ customerPublicId, vehiclePublicId, body, templatePublicId }),
  });

// --- Message templates --------------------------------------------------------------------

export const listMessageTemplates = (): Promise<MessageTemplateList> =>
  request<MessageTemplateList>('/message-templates');

export const createMessageTemplate = (
  body: { name: string; body: string; sortOrder?: number },
): Promise<{ id: string; name: string }> =>
  request('/message-templates', { method: 'POST', body: JSON.stringify(body) });

export const updateMessageTemplate = (
  id: string, body: { name: string; body: string; sortOrder?: number },
): Promise<{ id: string; name: string }> =>
  request(`/message-templates/${id}`, { method: 'PUT', body: JSON.stringify(body) });

export const deleteMessageTemplate = (id: string): Promise<void> =>
  request(`/message-templates/${id}`, { method: 'DELETE' });

export const restoreStarterTemplates = (): Promise<{ restored: string[] }> =>
  request('/message-templates/restore-starters', { method: 'POST' });

/**
 * Sets this tenant's own retail price for a car.
 *
 * Writes only the overlay, never the catalogue row: the listing carries what the exporter asks,
 * this carries what you sell at, and `{Price}` in a message template reads this one.
 */
export const setVehiclePricing = (
  id: string, tenantPrice: number | null, tenantCurrencyCode?: string | null,
): Promise<{ tenantPrice: number | null; tenantCurrencyCode: string | null }> =>
  request(`/vehicles/${id}/pricing`, {
    method: 'PUT',
    body: JSON.stringify({ tenantPrice, tenantCurrencyCode }),
  });

/**
 * Saves one listing photo to the viewer's device.
 *
 * Fetched through the API rather than linked to directly, for two reasons: the access token
 * has to travel with the request, and a browser ignores the `download` attribute on a
 * cross-origin link — so a direct link to the exporter's CDN opens the image in a tab instead
 * of saving it, which is not what "attach this to a message" needs.
 */
export async function savePhoto(path: string, filename: string): Promise<void> {
  const response = await fetch(path, { headers: accessTokenHeader() });

  if (!response.ok) {
    throw new ApiError(response.status, `Could not fetch that photo (${response.status}).`);
  }

  const url = URL.createObjectURL(await response.blob());
  const link = document.createElement('a');

  link.href = url;
  link.download = filename;
  link.click();

  // Released on a later tick: revoking synchronously races the save in some browsers and
  // produces an empty file.
  setTimeout(() => URL.revokeObjectURL(url), 1000);
}

// --- Duplicate review (open item O15) -------------------------------------------------

export const countDuplicates = (): Promise<{ pending: number }> =>
  request('/duplicates/count');

export const listDuplicates = (
  status: 'Pending' | 'Merged' | 'Rejected' = 'Pending', page = 1, pageSize = 20,
): Promise<DuplicateQueue> =>
  request(`/duplicates?status=${status}&page=${page}&pageSize=${pageSize}`);

/** Confirms two rows are one car. The duplicate's offers move onto the survivor. */
export const mergeDuplicate = (id: string, note?: string): Promise<{
  survivingVehicleId: string;
  archivedVehicleId: string;
  listingsMoved: number;
  imagesMoved: number;
}> => request(`/duplicates/${id}/merge`, {
  method: 'POST',
  body: JSON.stringify({ note }),
});

export const rejectDuplicate = (id: string): Promise<void> =>
  request(`/duplicates/${id}/reject`, { method: 'POST' });

export const listMerges = (): Promise<{ items: MergeRecord[] }> =>
  request('/duplicates/merges');

export const revertMerge = (id: string): Promise<void> =>
  request(`/duplicates/merges/${id}/revert`, { method: 'POST' });

/** Runs the scan now rather than waiting for the nightly job. Idempotent. */
export const scanForDuplicates = (): Promise<{
  pairsExamined: number;
  candidatesRaised: number;
  groupsSkipped: number;
}> => request('/duplicates/scan', { method: 'POST' });

// --- Customer notes ---------------------------------------------------------------------

/** Adds a note. The date and author are the server's — see NoteRequest. */
export const addCustomerNote = (publicId: string, body: string): Promise<{ id: number }> =>
  request(`/customers/${publicId}/notes`, {
    method: 'POST',
    body: JSON.stringify({ body }),
  });

export const editCustomerNote = (
  publicId: string, noteId: number, body: string,
): Promise<{ id: number }> =>
  request(`/customers/${publicId}/notes/${noteId}`, {
    method: 'PUT',
    body: JSON.stringify({ body }),
  });

export const deleteCustomerNote = (publicId: string, noteId: number): Promise<unknown> =>
  request(`/customers/${publicId}/notes/${noteId}`, { method: 'DELETE' });
