/**
 * Kimi Code and SuperGrok (xAI OAuth) usage collectors.
 *
 * Both endpoints are subscription surfaces: only OAuth-derived credentials
 * (`AuthResult.source === "OAuth"`) are ever sent to them. Paid API keys are
 * a separate product and must never reach these hosts. Reports carry only
 * ids, labels, normalized fractions, and reset timestamps: never tokens,
 * account identifiers, e-mail addresses, or raw payloads.
 */
import {
  isRecord,
  type AuthResult,
  type UsageAmount,
  type UsageCollector,
  type UsageLimit,
  type UsageReport,
  type UsageRequest,
  type UsageWindow,
} from "../usageTypes.ts";

const KIMI_PROVIDER = "kimi-code";
const KIMI_USAGE_URL = "https://api.kimi.com/coding/v1/usages";

const XAI_PROVIDER = "xai-oauth";
const XAI_BILLING_URL = "https://cli-chat-proxy.grok.com/v1/billing";
const XAI_CREDITS_URL = `${XAI_BILLING_URL}?format=credits`;

/** Upper bound on windows per report; the coordinator enforces the same cap. */
const MAX_LIMITS = 8;
/** Server-supplied labels are bounded and stripped of control characters. */
const MAX_LABEL_LENGTH = 64;

const MINUTE_MS = 60_000;
const HOUR_MS = 60 * MINUTE_MS;
const DAY_MS = 24 * HOUR_MS;

type JsonRecord = Record<string, unknown>;

type FetchOutcome = { ok: true; body: unknown } | { ok: false; report: UsageReport };

function toNumber(value: unknown): number | undefined {
  if (typeof value === "number") return Number.isFinite(value) ? value : undefined;
  if (typeof value === "string" && value.trim()) {
    const parsed = Number(value);
    return Number.isFinite(parsed) ? parsed : undefined;
  }
  return undefined;
}

function parseIsoTimestamp(value: unknown): number | undefined {
  if (typeof value !== "string" || !value) return undefined;
  const parsed = Date.parse(value);
  return Number.isFinite(parsed) && parsed > 0 ? parsed : undefined;
}

/** used/limit as bounded fractions; `undefined` when the pair cannot yield one. */
function fractionAmount(used: number, limit: number, unit: string): UsageAmount | undefined {
  if (limit <= 0) return undefined;
  const usedFraction = Math.min(Math.max(used / limit, 0), 1);
  return { usedFraction, remainingFraction: 1 - usedFraction, unit };
}

function sanitizeLabel(value: unknown): string | undefined {
  if (typeof value !== "string") return undefined;
  const cleaned = value.replace(/[\u0000-\u001f\u007f]/g, "").trim();
  return cleaned ? cleaned.slice(0, MAX_LABEL_LENGTH) : undefined;
}

function report(
  provider: string,
  status: UsageReport["status"],
  diagnostic: string,
): UsageReport {
  return { provider, fetchedAt: Date.now(), limits: [], status, diagnostic };
}

/** Maps a failed HTTP status to the shared diagnostic vocabulary. */
function httpFailure(provider: string, status: number): UsageReport {
  if (status === 401 || status === 403) return report(provider, "unavailable", "unauthorized");
  if (status === 429) return report(provider, "unavailable", "rate_limited");
  return report(provider, "error", `http_${status}`);
}

/** Forwards the injected request's stable failure token; anything else is `request_failed`. */
function requestFailure(provider: string, error: unknown): UsageReport {
  const message = error instanceof Error ? error.message : "";
  return report(provider, "error", /^[a-z0-9_]{1,40}$/.test(message) ? message : "request_failed");
}

async function fetchJson(
  provider: string,
  request: UsageRequest,
  url: string,
  headers: Record<string, string>,
  signal: AbortSignal,
): Promise<FetchOutcome> {
  let response: { status: number; body: unknown };
  try {
    response = await request(url, { method: "GET", headers }, signal);
  } catch (error) {
    return { ok: false, report: requestFailure(provider, error) };
  }
  if (response.status < 200 || response.status >= 300) {
    return { ok: false, report: httpFailure(provider, response.status) };
  }
  return { ok: true, body: response.body };
}

/**
 * Access token of an OAuth credential. Pi derives Kimi auth as an
 * `Authorization: Bearer` header and xAI auth as `apiKey`; either carries the
 * subscription access token. Non-OAuth credentials yield nothing.
 */
function oauthAccessToken(auth: AuthResult): string | undefined {
  if (auth.source !== "OAuth") return undefined;
  const header = auth.auth.headers?.["Authorization"] ?? auth.auth.headers?.["authorization"];
  if (typeof header === "string") {
    const match = /^Bearer\s+(\S+)$/i.exec(header.trim());
    if (match?.[1]) return match[1];
  }
  const apiKey = auth.auth.apiKey?.trim();
  return apiKey || undefined;
}

// ---------------------------------------------------------------------------
// Kimi Code
// ---------------------------------------------------------------------------

interface KimiRow {
  label: string;
  used?: number;
  limit?: number;
  resetsAt?: number;
  windowId?: string;
  windowLabel?: string;
}

/** Absolute reset instant from any of Kimi's reset spellings, in epoch ms. */
function parseKimiResetTime(data: JsonRecord, nowMs: number): number | undefined {
  for (const key of ["reset_at", "resetAt", "reset_time", "resetTime"]) {
    const value = data[key];
    if (typeof value === "string" && value.trim()) {
      const parsed = parseIsoTimestamp(value);
      if (parsed !== undefined) return parsed;
    }
    if (typeof value === "number" && Number.isFinite(value) && value > 0) {
      return value > 1_000_000_000_000 ? value : value * 1000;
    }
  }
  for (const key of ["reset_in", "resetIn", "ttl", "window"]) {
    const seconds = toNumber(data[key]);
    if (seconds !== undefined && seconds >= 0) return nowMs + seconds * 1000;
  }
  return undefined;
}

function kimiDurationMs(duration: number, timeUnit: string): number | undefined {
  const upper = timeUnit.toUpperCase();
  if (upper.includes("MINUTE")) return duration * MINUTE_MS;
  if (upper.includes("HOUR")) return duration * HOUR_MS;
  if (upper.includes("DAY")) return duration * DAY_MS;
  if (upper.includes("WEEK")) return duration * 7 * DAY_MS;
  if (upper.includes("SECOND")) return duration * 1000;
  return undefined;
}

/** Canonical window id: the 300-minute burst window surfaces as "5h", not "300m". */
function canonicalWindowId(durationMs: number): string | undefined {
  if (durationMs <= 0) return undefined;
  if (durationMs % DAY_MS === 0) return `${durationMs / DAY_MS}d`;
  if (durationMs % HOUR_MS === 0) return `${durationMs / HOUR_MS}h`;
  const minutes = Math.round(durationMs / MINUTE_MS);
  return minutes > 0 ? `${minutes}m` : undefined;
}

function kimiWindowLabel(durationMs: number): string {
  if (durationMs % DAY_MS === 0) return `${durationMs / DAY_MS}d limit`;
  if (durationMs % HOUR_MS === 0) return `${durationMs / HOUR_MS}h limit`;
  if (durationMs % MINUTE_MS === 0) return `${durationMs / MINUTE_MS}m limit`;
  return `${durationMs / 1000}s limit`;
}

function parseKimiRow(data: JsonRecord, fallbackLabel: string, nowMs: number): KimiRow | undefined {
  const limit = toNumber(data["limit"]);
  let used = toNumber(data["used"]);
  const remaining = toNumber(data["remaining"]);
  if (used === undefined && remaining !== undefined && limit !== undefined) {
    used = limit - remaining;
  }
  if (used === undefined && limit === undefined) return undefined;
  const row: KimiRow = {
    label: sanitizeLabel(data["name"]) ?? sanitizeLabel(data["title"]) ?? fallbackLabel,
  };
  if (used !== undefined) row.used = used;
  if (limit !== undefined) row.limit = limit;
  const resetsAt = parseKimiResetTime(data, nowMs);
  if (resetsAt !== undefined) row.resetsAt = resetsAt;
  return row;
}

function parseKimiRows(payload: JsonRecord, nowMs: number): KimiRow[] {
  const rows: KimiRow[] = [];

  const usage = payload["usage"];
  if (isRecord(usage)) {
    const summary = parseKimiRow(usage, "Total quota", nowMs);
    if (summary) {
      // The aggregate quota resets weekly but the payload carries only a
      // reset time, so attach the canonical weekly window explicitly.
      summary.windowId = "7d";
      summary.windowLabel = "7 Day";
      rows.push(summary);
    }
  }

  const limits = payload["limits"];
  if (Array.isArray(limits)) {
    limits.forEach((item, index) => {
      if (!isRecord(item)) return;
      const detail = isRecord(item["detail"]) ? item["detail"] : item;
      const windowData = isRecord(item["window"]) ? item["window"] : {};
      const duration = toNumber(windowData["duration"]);
      const timeUnit = typeof windowData["timeUnit"] === "string" ? windowData["timeUnit"] : "";
      const durationMs =
        duration !== undefined && duration > 0 ? kimiDurationMs(duration, timeUnit) : undefined;
      const fallbackLabel =
        sanitizeLabel(item["name"]) ??
        sanitizeLabel(item["title"]) ??
        sanitizeLabel(item["scope"]) ??
        (durationMs !== undefined ? kimiWindowLabel(durationMs) : `Limit #${index + 1}`);
      const row = parseKimiRow(detail, fallbackLabel, nowMs);
      if (!row) return;
      if (durationMs !== undefined) {
        const windowId = canonicalWindowId(durationMs);
        if (windowId !== undefined) row.windowId = windowId;
        row.windowLabel = kimiWindowLabel(durationMs);
      }
      // Kimi puts `resetTime` on the limit detail, not on `window`; a
      // window-level reset still wins when present.
      const windowReset = parseKimiResetTime(windowData, nowMs);
      if (windowReset !== undefined) row.resetsAt = windowReset;
      rows.push(row);
    });
  }

  return rows;
}

function kimiRowToLimit(row: KimiRow, index: number, usedIds: Set<string>): UsageLimit | undefined {
  if (row.used === undefined || row.limit === undefined) return undefined;
  const amount = fractionAmount(row.used, row.limit, "unknown");
  if (!amount) return undefined;

  const baseId = row.windowId ?? `limit_${index}`;
  const id = usedIds.has(baseId) ? `${baseId}_${index}` : baseId;
  usedIds.add(id);

  const window: UsageWindow = {};
  if (row.windowLabel !== undefined) window.label = row.windowLabel;
  if (row.resetsAt !== undefined) window.resetsAt = row.resetsAt;

  return { id, label: row.label, window, amount };
}

/** Kimi Code subscription quota (`GET /coding/v1/usages`). */
export const kimiCodeUsage: UsageCollector = async (auth, request, signal) => {
  const accessToken = oauthAccessToken(auth);
  if (!accessToken) return report(KIMI_PROVIDER, "unavailable", "no_credential");

  const nowMs = Date.now();
  const fetched = await fetchJson(
    KIMI_PROVIDER,
    request,
    KIMI_USAGE_URL,
    {
      Authorization: `Bearer ${accessToken}`,
      Accept: "application/json",
      "X-Msh-Platform": "kimi_cli",
    },
    signal,
  );
  if (!fetched.ok) return fetched.report;
  if (!isRecord(fetched.body)) return report(KIMI_PROVIDER, "error", "response_malformed");

  const usedIds = new Set<string>();
  const limits: UsageLimit[] = [];
  parseKimiRows(fetched.body, nowMs).forEach((row, index) => {
    const limit = kimiRowToLimit(row, index, usedIds);
    if (limit) limits.push(limit);
  });
  if (limits.length === 0) return report(KIMI_PROVIDER, "unavailable", "no_quota");

  return { provider: KIMI_PROVIDER, fetchedAt: nowMs, limits: limits.slice(0, MAX_LIMITS), status: "available" };
};

// ---------------------------------------------------------------------------
// xAI (SuperGrok)
// ---------------------------------------------------------------------------

interface XaiProductUsage {
  product: string;
  usagePercent: number;
}

/** Legacy SuperGrok weekly credits (`?format=credits`). */
interface XaiWeeklyConfig {
  kind: "weekly";
  periodEnd: number;
  creditUsagePercent: number;
  productUsage: XaiProductUsage[];
  onDemandCap?: number;
  onDemandUsed?: number;
  /** creditUsagePercent was omitted and defaulted to 0 for an active period. */
  inferredPercent: boolean;
}

/**
 * Unified-billing monthly included quota. Live `isUnifiedBillingUser`
 * accounts omit creditUsagePercent on `?format=credits` and expose
 * monthlyLimit/used on the default billing URL.
 */
interface XaiMonthlyConfig {
  kind: "monthly";
  periodStart: number;
  periodEnd: number;
  used: number;
  limit: number;
  onDemandCap?: number;
  onDemandUsed?: number;
}

function parsePercent(value: unknown): number | undefined {
  const percent = toNumber(value);
  return percent !== undefined && percent >= 0 && percent <= 100 ? percent : undefined;
}

/** xAI wraps money-like amounts as `{ val: number }`. */
function parseXaiAmount(value: unknown): number | undefined {
  if (!isRecord(value)) return undefined;
  const amount = toNumber(value["val"]);
  return amount !== undefined && amount >= 0 ? amount : undefined;
}

/** `usagePercent` is already validated to 0..100 by `parsePercent`. */
function percentAmount(usagePercent: number): UsageAmount {
  const usedFraction = usagePercent / 100;
  return { usedFraction, remainingFraction: 1 - usedFraction, unit: "percent" };
}

function slugifyProduct(product: string): string {
  return product
    .toLowerCase()
    .replace(/[^a-z0-9]+/g, "_")
    .replace(/^_+|_+$/g, "");
}

function xaiProductLabel(product: string): string {
  if (product === "GrokBuild") return "Grok Build (Weekly)";
  if (product === "Api") return "API (Weekly)";
  return `${product} (Weekly)`;
}

function parseXaiWeeklyConfig(raw: JsonRecord, nowMs: number): XaiWeeklyConfig | undefined {
  const currentPeriod = raw["currentPeriod"];
  if (!isRecord(currentPeriod)) return undefined;
  const start = parseIsoTimestamp(currentPeriod["start"]);
  const end = parseIsoTimestamp(currentPeriod["end"]);
  const type = typeof currentPeriod["type"] === "string" ? currentPeriod["type"] : "";
  // Recently-ended weekly windows are kept so the card survives period
  // rollover; only inverted ranges and non-weekly period types are rejected.
  if (start === undefined || end === undefined || end <= start || !type.toUpperCase().includes("WEEK")) {
    return undefined;
  }

  // Fresh periods (or zero-usage accounts) omit creditUsagePercent; default
  // to 0 only while the period is active. Expired periods without explicit
  // usage are rejected.
  const rawPercent = raw["creditUsagePercent"];
  const inferredPercent = rawPercent === undefined || rawPercent === null;
  const creditUsagePercent = inferredPercent
    ? end > nowMs
      ? 0
      : undefined
    : parsePercent(rawPercent);
  if (creditUsagePercent === undefined) return undefined;

  const productUsage: XaiProductUsage[] = [];
  const rawProducts = raw["productUsage"];
  if (rawProducts !== undefined) {
    if (!Array.isArray(rawProducts)) return undefined;
    for (const item of rawProducts) {
      if (!isRecord(item)) continue;
      const product = sanitizeLabel(item["product"]);
      const rawUsage = item["usagePercent"];
      const usagePercent =
        rawUsage === undefined || rawUsage === null ? 0 : parsePercent(rawUsage);
      if (!product || usagePercent === undefined) continue;
      productUsage.push({ product, usagePercent });
    }
  }

  const config: XaiWeeklyConfig = {
    kind: "weekly",
    periodEnd: end,
    creditUsagePercent,
    productUsage,
    inferredPercent,
  };
  const onDemandCap = parseXaiAmount(raw["onDemandCap"]);
  const onDemandUsed = parseXaiAmount(raw["onDemandUsed"]);
  if (onDemandCap !== undefined) config.onDemandCap = onDemandCap;
  if (onDemandUsed !== undefined) config.onDemandUsed = onDemandUsed;
  return config;
}

function parseXaiMonthlyConfig(raw: JsonRecord): XaiMonthlyConfig | undefined {
  const periodStart = parseIsoTimestamp(raw["billingPeriodStart"]);
  const periodEnd = parseIsoTimestamp(raw["billingPeriodEnd"]);
  if (periodStart === undefined || periodEnd === undefined || periodEnd <= periodStart) {
    return undefined;
  }
  const limit = parseXaiAmount(raw["monthlyLimit"]);
  const used = parseXaiAmount(raw["used"]);
  // A positive included quota is required; zero/missing is not a usable report.
  if (limit === undefined || limit <= 0 || used === undefined) return undefined;

  const config: XaiMonthlyConfig = { kind: "monthly", periodStart, periodEnd, used, limit };
  const onDemandCap = parseXaiAmount(raw["onDemandCap"]);
  const onDemandUsed = parseXaiAmount(raw["onDemandUsed"]);
  if (onDemandCap !== undefined) config.onDemandCap = onDemandCap;
  if (onDemandUsed !== undefined) config.onDemandUsed = onDemandUsed;
  return config;
}

/** True when the monthly payload positively states this account has no monthly quota. */
function confirmsNoMonthlyQuota(raw: JsonRecord, nowMs: number): boolean {
  const limit = parseXaiAmount(raw["monthlyLimit"]);
  if (limit !== undefined) return limit === 0;
  // Some weekly accounts return the credits shape from the default endpoint too.
  return parseXaiWeeklyConfig(raw, nowMs)?.inferredPercent === true;
}

function xaiOnDemandLimit(
  onDemandCap: number | undefined,
  onDemandUsed: number | undefined,
): UsageLimit | undefined {
  if (onDemandCap === undefined || onDemandUsed === undefined) return undefined;
  const amount = fractionAmount(onDemandUsed, onDemandCap, "unknown");
  return amount ? { id: "on_demand", label: "On-demand", window: {}, amount } : undefined;
}

function xaiWeeklyLimits(config: XaiWeeklyConfig): UsageLimit[] {
  const window: UsageWindow = { label: "Weekly", resetsAt: config.periodEnd };
  const limits: UsageLimit[] = [
    {
      id: "credits_1w",
      label: "SuperGrok Weekly Credits",
      window,
      amount: percentAmount(config.creditUsagePercent),
    },
  ];
  for (const item of config.productUsage) {
    const slug = slugifyProduct(item.product);
    if (!slug) continue;
    limits.push({
      id: `product_${slug}_1w`,
      label: xaiProductLabel(item.product),
      window,
      amount: percentAmount(item.usagePercent),
    });
  }
  const onDemand = xaiOnDemandLimit(config.onDemandCap, config.onDemandUsed);
  if (onDemand) limits.push(onDemand);
  return limits;
}

function xaiMonthlyLimits(config: XaiMonthlyConfig): UsageLimit[] {
  // Real calendar months vary; label from the observed period length.
  const approxDays = Math.max(1, Math.round((config.periodEnd - config.periodStart) / DAY_MS));
  const amount = fractionAmount(config.used, config.limit, "unknown");
  if (!amount) return [];
  const limits: UsageLimit[] = [
    {
      id: "included_1mo",
      label: "SuperGrok Monthly Included",
      window: {
        label: approxDays === 30 || approxDays === 31 ? "Monthly" : `${approxDays}d`,
        resetsAt: config.periodEnd,
      },
      amount,
    },
  ];
  const onDemand = xaiOnDemandLimit(config.onDemandCap, config.onDemandUsed);
  if (onDemand) limits.push(onDemand);
  return limits;
}

function xaiConfig(body: unknown): JsonRecord | undefined {
  if (!isRecord(body)) return undefined;
  const config = body["config"];
  return isRecord(config) ? config : undefined;
}

/**
 * SuperGrok subscription usage. Probes the legacy weekly credits payload
 * first; unified-billing accounts additionally expose a monthly included
 * quota on the default billing URL.
 */
export const xaiUsage: UsageCollector = async (auth, request, signal) => {
  const accessToken = oauthAccessToken(auth);
  if (!accessToken) return report(XAI_PROVIDER, "unavailable", "no_credential");

  const nowMs = Date.now();
  const headers = {
    Authorization: `Bearer ${accessToken}`,
    Accept: "application/json",
    "X-XAI-Token-Auth": "xai-grok-cli",
  };

  const credits = await fetchJson(XAI_PROVIDER, request, XAI_CREDITS_URL, headers, signal);
  const creditsConfig = credits.ok ? xaiConfig(credits.body) : undefined;
  const weekly = creditsConfig ? parseXaiWeeklyConfig(creditsConfig, nowMs) : undefined;
  const creditsLooksUnified = creditsConfig?.["isUnifiedBillingUser"] === true;

  // Fetch the monthly shape when credits is missing/unusable, or when credits
  // marks the account unified (live responses sometimes include both shapes).
  let monthlyFetch: FetchOutcome | undefined;
  let monthlyConfig: JsonRecord | undefined;
  let monthly: XaiMonthlyConfig | undefined;
  if (!weekly || creditsLooksUnified) {
    monthlyFetch = await fetchJson(XAI_PROVIDER, request, XAI_BILLING_URL, headers, signal);
    monthlyConfig = monthlyFetch.ok ? xaiConfig(monthlyFetch.body) : undefined;
    monthly = monthlyConfig ? parseXaiMonthlyConfig(monthlyConfig) : undefined;
  }

  // Unified account whose weekly percent was only inferred: a positive monthly
  // quota replaces it; a monthly payload confirming no monthly quota keeps it;
  // anything else (failed monthly fetch) rejects the inferred weekly.
  let effectiveWeekly = weekly;
  if (weekly?.inferredPercent && creditsLooksUnified) {
    if (monthly || !monthlyConfig || !confirmsNoMonthlyQuota(monthlyConfig, nowMs)) {
      effectiveWeekly = undefined;
    }
  }

  if (!effectiveWeekly && !monthly) {
    if (!credits.ok) return credits.report;
    if (monthlyFetch && !monthlyFetch.ok) return monthlyFetch.report;
    if (!creditsConfig && !monthlyConfig) return report(XAI_PROVIDER, "error", "response_malformed");
    return report(XAI_PROVIDER, "unavailable", "no_quota");
  }

  const limits: UsageLimit[] = [];
  if (effectiveWeekly) limits.push(...xaiWeeklyLimits(effectiveWeekly));
  if (monthly) limits.push(...xaiMonthlyLimits(monthly));
  // Both shapes may carry the same on-demand cap; keep the first.
  const seen = new Set<string>();
  const deduped = limits.filter((limit) => {
    if (seen.has(limit.id)) return false;
    seen.add(limit.id);
    return true;
  });
  if (deduped.length === 0) return report(XAI_PROVIDER, "unavailable", "no_quota");

  return { provider: XAI_PROVIDER, fetchedAt: nowMs, limits: deduped.slice(0, MAX_LIMITS), status: "available" };
};
