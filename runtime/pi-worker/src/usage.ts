/**
 * Provider usage sidecar: the DevFleet replacement for `omp usage`.
 *
 * Run directly under Node (`node usage.ts`). Resolves each supported
 * provider's credentials through Pi's public `ModelRuntime` API, hands them to
 * the provider collector, and prints `{ reports: UsageReport[] }` as one JSON
 * line. Every report carries an explicit `status`; the host never infers
 * failure from missing data.
 *
 * Bounds enforced here, independent of collector behaviour: one shared
 * deadline for the whole run, at most `MAX_LIMITS` windows per provider, and
 * a snake_case diagnostic vocabulary so no upstream text, error message, or
 * credential can reach stdout.
 */
import { ModelRuntime } from "@earendil-works/pi-coding-agent";
import { createUsageRequest } from "./usageHttp.ts";
import { anthropicUsage, openaiCodexUsage } from "./usageProviders/openaiAnthropic.ts";
import { kimiCodeUsage, xaiUsage } from "./usageProviders/kimiXai.ts";
import { opencodeGoUsage, zaiUsage } from "./usageProviders/zaiOpenCode.ts";
import type {
  AuthResult,
  UsageCollector,
  UsageLimit,
  UsageReport,
  UsageRequest,
  UsageWindow,
} from "./usageTypes.ts";

/** Single deadline covering runtime creation, auth resolution and every fetch. */
export const REQUEST_DEADLINE_MS = 8_000;

/** Maximum quota windows forwarded per provider. */
export const MAX_LIMITS = 8;

/** Diagnostics are stable tokens only; the host rejects anything else. */
export const DIAGNOSTIC_PATTERN = /^[a-z0-9_]{1,40}$/;

/** One supported provider: Pi credential id, card id, collector, and optional endpoint guard. */
export interface UsageProviderBinding {
  /** Provider id as registered in Pi (`ModelRuntime.checkAuth`/`getAuth`). */
  piProvider: string;
  /** Provider id emitted in the report and shown on the DevFleet card. */
  provider: string;
  collect: UsageCollector;
  /** Expected effective provider base URL. `null` means Pi's builtin has none. */
  expectedBaseUrl?: string | null;
}

export const USAGE_PROVIDERS: readonly UsageProviderBinding[] = [
  { piProvider: "openai-codex", provider: "openai-codex", collect: openaiCodexUsage },
  { piProvider: "anthropic", provider: "anthropic", collect: anthropicUsage },
  { piProvider: "kimi-coding", provider: "kimi-code", collect: kimiCodeUsage },
  {
    piProvider: "zai",
    provider: "zai",
    collect: zaiUsage,
    expectedBaseUrl: "https://api.z.ai/api/coding/paas/v4",
  },
  { piProvider: "xai", provider: "xai-oauth", collect: xaiUsage },
  {
    piProvider: "opencode-go",
    provider: "opencode-go",
    collect: opencodeGoUsage,
    expectedBaseUrl: null,
  },
];

/** The slice of `ModelRuntime` the coordinator needs; kept narrow so tests can fake it. */
export type UsageAuthRuntime = Pick<ModelRuntime, "checkAuth" | "getAuth"> & {
  getProvider(providerId: string): { readonly baseUrl?: string } | undefined;
};

/** Injectable dependencies; production wires the real runtime, HTTP, and clock. */
export interface CollectUsageOptions {
  runtime: UsageAuthRuntime;
  request: UsageRequest;
  signal: AbortSignal;
  providers?: readonly UsageProviderBinding[];
  now?: () => number;
}

/** Stdout payload consumed by the C# host. */
export interface UsageOutput {
  reports: UsageReport[];
}

function errorReport(provider: string, fetchedAt: number, diagnostic: string): UsageReport {
  return { provider, fetchedAt, limits: [], status: "error", diagnostic };
}

function unavailableReport(provider: string, fetchedAt: number, diagnostic: string): UsageReport {
  return { provider, fetchedAt, limits: [], status: "unavailable", diagnostic };
}

function finiteNumber(value: unknown): number | undefined {
  return typeof value === "number" && Number.isFinite(value) ? value : undefined;
}

const MAX_LABEL_LENGTH = 40;
const NON_PRINTABLE_ASCII = /[^\x20-\x7e]/g;

function sanitizeLabel(value: unknown): string | undefined {
  if (typeof value !== "string") return undefined;

  const label = value
    .replace(NON_PRINTABLE_ASCII, "")
    .trim()
    .slice(0, MAX_LABEL_LENGTH)
    .trimEnd();
  return label || undefined;
}

/** Keep only well-typed fields so a buggy collector cannot emit an off-schema limit. */
function sanitizeLimit(limit: UsageLimit): UsageLimit | undefined {
  if (typeof limit.id !== "string") return undefined;
  const label = sanitizeLabel(limit.label);
  if (label === undefined || typeof limit.amount?.unit !== "string") return undefined;

  const window: UsageWindow = {};
  const windowLabel = sanitizeLabel(limit.window?.label);
  if (windowLabel !== undefined) window.label = windowLabel;
  const resetsAt = finiteNumber(limit.window?.resetsAt);
  if (resetsAt !== undefined) window.resetsAt = Math.trunc(resetsAt);

  const amount: UsageLimit["amount"] = { unit: limit.amount.unit };
  const used = finiteNumber(limit.amount.usedFraction);
  if (used !== undefined) amount.usedFraction = used;
  const remaining = finiteNumber(limit.amount.remainingFraction);
  if (remaining !== undefined) amount.remainingFraction = remaining;

  return { id: limit.id, label, window, amount };
}

/**
 * Normalize a collector's report to the output contract: the emitted provider
 * id, an integer timestamp, at most `MAX_LIMITS` sanitized windows, and a
 * diagnostic only when it is a stable token on a non-available report.
 */
function sanitizeReport(provider: string, fetchedAt: number, report: UsageReport): UsageReport {
  const status =
    report.status === "available" || report.status === "unavailable" ? report.status : "error";
  const limits = Array.isArray(report.limits)
    ? report.limits.slice(0, MAX_LIMITS).flatMap((limit) => sanitizeLimit(limit) ?? [])
    : [];
  const sanitized: UsageReport = {
    provider,
    fetchedAt: Math.trunc(fetchedAt),
    limits,
    status,
  };
  if (status === "available") return sanitized;

  sanitized.diagnostic =
    typeof report.diagnostic === "string" && DIAGNOSTIC_PATTERN.test(report.diagnostic)
      ? report.diagnostic
      : status === "unavailable"
        ? "provider_unavailable"
        : "provider_error";
  return sanitized;
}

/**
 * Collect one provider. Returns undefined when Pi has no credential for it
 * (the provider is not configured on this node, so no card). Every failure
 * after that point is a stable error report: the operator configured the
 * provider and must see that its usage could not be read.
 */
async function collectProvider(
  binding: UsageProviderBinding,
  options: CollectUsageOptions,
): Promise<UsageReport | undefined> {
  const { runtime, request, signal } = options;
  const now = options.now ?? Date.now;
  const fetchedAt = now();
  const fail = (diagnostic: string) =>
    errorReport(binding.provider, fetchedAt, signal.aborted ? "request_timeout" : diagnostic);

  let configured: boolean;
  try {
    configured = (await runtime.checkAuth(binding.piProvider, { signal })) !== undefined;
  } catch {
    return fail("auth_check_failed");
  }
  if (!configured) return undefined;

  if (binding.expectedBaseUrl !== undefined) {
    const effectiveBaseUrl = runtime.getProvider(binding.piProvider)?.baseUrl;
    const expectedBaseUrl = binding.expectedBaseUrl ?? undefined;
    if (effectiveBaseUrl !== expectedBaseUrl) {
      return unavailableReport(binding.provider, fetchedAt, "provider_endpoint_overridden");
    }
  }

  let auth: AuthResult | undefined;
  try {
    auth = await runtime.getAuth(binding.piProvider, { signal });
  } catch {
    return fail("auth_refresh_failed");
  }
  if (auth === undefined) return fail("auth_unresolved");

  let report: UsageReport;
  try {
    report = sanitizeReport(binding.provider, now(), await binding.collect(auth, request, signal));
  } catch {
    return fail("collector_failed");
  }
  // Last line of defence: a collector that copies a credential into any
  // string field loses the whole report rather than leaking it to stdout.
  const serialized = JSON.stringify(report);
  return authSecrets(auth).some((secret) => serialized.includes(secret))
    ? fail("report_redacted")
    : report;
}

/** Credential tokens (8+ chars) that must never appear anywhere in a report. */
const MIN_SECRET_LENGTH = 8;

function authSecrets(auth: AuthResult): string[] {
  const tokens = [auth.auth.apiKey ?? ""];
  for (const value of Object.values(auth.auth.headers ?? {})) {
    if (value) tokens.push(...value.split(/\s+/));
  }
  return tokens.filter((token) => token.length >= MIN_SECRET_LENGTH);
}

/** Collect every configured provider concurrently under one shared deadline. */
export async function collectUsage(options: CollectUsageOptions): Promise<UsageOutput> {
  const providers = options.providers ?? USAGE_PROVIDERS;
  const reports = await Promise.all(providers.map((binding) => collectProvider(binding, options)));
  return { reports: reports.filter((report) => report !== undefined) };
}

if (import.meta.main) {
  const signal = AbortSignal.timeout(REQUEST_DEADLINE_MS);
  const runtime = await ModelRuntime.create({ signal, refreshOnCreate: false });
  const output = await collectUsage({ runtime, request: createUsageRequest(), signal });
  process.stdout.write(`${JSON.stringify(output)}\n`);
}
