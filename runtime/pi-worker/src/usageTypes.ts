/**
 * Shared contract between the `omp usage` replacement sidecar (`usage.ts`) and
 * the per-provider collectors under `usageProviders/`.
 *
 * Collectors receive already-resolved Pi credentials and a bounded HTTP
 * request function; they never touch auth storage or the network directly.
 * Everything they return is serialized to stdout for the C# host, so reports
 * must stay free of secrets, raw payloads, and personal data.
 */

/** Narrow an unknown JSON value to a plain object (non-null, non-array). */
export function isRecord(value: unknown): value is Record<string, unknown> {
  return typeof value === "object" && value !== null && !Array.isArray(value);
}

/**
 * Resolved provider credentials, structurally identical to the `AuthResult`
 * that `ModelRuntime.getAuth()` resolves. Declared here because
 * `@earendil-works/pi-coding-agent` does not re-export the pi-ai type and the
 * worker must not depend on `@earendil-works/pi-ai` directly.
 */
export interface AuthResult {
  auth: {
    apiKey?: string;
    headers?: Record<string, string | null>;
    baseUrl?: string;
  };
  /** Provider-scoped environment/config values resolved from credentials. */
  env?: Record<string, string>;
  /** Human-readable credential source label: "OAuth", "ANTHROPIC_API_KEY". Never a secret. */
  source?: string;
}

/** How much of one quota window is consumed; fractions are 0..1. */
export interface UsageAmount {
  usedFraction?: number;
  remainingFraction?: number;
  /** What the fraction measures: "percent", "tokens", "requests", "credits". */
  unit: string;
}

/** The time window a limit applies to. */
export interface UsageWindow {
  /** Human label such as "5h" or "weekly". */
  label?: string;
  /** Epoch milliseconds when the window resets. */
  resetsAt?: number;
}

/** One quota window reported by a provider. */
export interface UsageLimit {
  /** Stable per-provider id, e.g. "5h", "7d", "monthly". */
  id: string;
  label: string;
  window: UsageWindow;
  amount: UsageAmount;
}

/**
 * Per-provider result. `status` is explicit so the host never has to infer
 * failure from an empty `limits` array:
 * - `available`: limits were fetched.
 * - `unavailable`: the provider is configured but exposes no usage endpoint
 *   for this credential type (e.g. plain API key).
 * - `error`: the fetch or parse failed; `diagnostic` is a stable,
 *   secret-free explanation.
 */
export interface UsageReport {
  provider: string;
  fetchedAt: number;
  limits: UsageLimit[];
  status: "available" | "unavailable" | "error";
  diagnostic?: string;
}

/**
 * Bounded HTTP request supplied to collectors. Implementations enforce the
 * HTTPS origin allowlist, refuse redirects, cap the response body, and honour
 * the shared deadline `signal`. `body` is the parsed JSON response (or the
 * raw text when the response is not JSON).
 */
export type UsageRequest = (
  url: string,
  init: RequestInit,
  signal: AbortSignal,
) => Promise<{ status: number; body: unknown }>;

/** One provider's usage fetch, from resolved credentials to a report. */
export type UsageCollector = (
  auth: AuthResult,
  request: UsageRequest,
  signal: AbortSignal,
) => Promise<UsageReport>;
