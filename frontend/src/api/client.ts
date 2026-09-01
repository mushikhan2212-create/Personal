import type {
  LoginResponse, SyncResult, VehicleSearchResponse, VehicleSearchSort, VehicleSourceSummary,
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
  minYear?: number;
  maxYear?: number;
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
