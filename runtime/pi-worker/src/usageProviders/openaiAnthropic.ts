/**
 * OpenAI Codex and Anthropic (Claude subscription) usage collectors for the
 * `omp usage` replacement sidecar. Adapted from the OMP readers
 * (`packages/ai/src/usage/openai-codex.ts`, `usage/claude.ts`) onto Pi's
 * resolved `AuthResult` and the injected bounded `UsageRequest`.
 *
 * Both endpoints only answer OAuth bearer tokens, which Pi resolves with
 * `source: "OAuth"`. Reports never carry raw payloads, identities, or the
 * upstream error text; failures surface as stable snake_case diagnostics.
 */
import { Buffer } from "node:buffer";
import {
  isRecord,
  type AuthResult,
  type UsageAmount,
  type UsageCollector,
  type UsageLimit,
  type UsageReport,
  type UsageRequest,
} from "../usageTypes.ts";

const CODEX_USAGE_URL = "https://chatgpt.com/backend-api/wham/usage";
const CODEX_JWT_AUTH_CLAIM = "https://api.openai.com/auth";
const CODEX_USER_AGENT = "devfleet-usage/1";

const CLAUDE_USAGE_URL = "https://api.anthropic.com/api/oauth/usage";
/** Claude Code release the OAuth usage endpoint expects to see. */
const CLAUDE_CODE_VERSION = "2.1.246";
const CLAUDE_HEADERS = {
  accept: "application/json, text/plain, */*",
  "anthropic-beta":
    "claude-code-20250219,oauth-2025-04-20,interleaved-thinking-2025-05-14,redact-thinking-2026-02-12,context-management-2025-06-27,prompt-caching-scope-2026-01-05,mid-conversation-system-2026-04-07,advanced-tool-use-2025-11-20,effort-2025-11-24,extended-cache-ttl-2025-04-11",
  "content-type": "application/json",
  "user-agent": `claude-cli/${CLAUDE_CODE_VERSION} (external, cli)`,
} as const;

/** `.message` values the injected `request` throws; forwarded verbatim, anything else is `request_failed`. */
const REQUEST_DIAGNOSTICS: Record<string, true> = {
  origin_not_allowed: true,
  redirect_refused: true,
  response_too_large: true,
  request_timeout: true,
  request_failed: true,
};

// ---------------------------------------------------------------------------
// Shared helpers
// ---------------------------------------------------------------------------

function toNumber(value: unknown): number | undefined {
  if (typeof value === "number" && Number.isFinite(value)) return value;
  if (typeof value === "string" && value.trim()) {
    const parsed = Number(value);
    if (Number.isFinite(parsed)) return parsed;
  }
  return undefined;
}

function parseIsoTimestamp(value: unknown): number | undefined {
  if (typeof value !== "string" || !value) return undefined;
  const parsed = Date.parse(value);
  return Number.isFinite(parsed) ? parsed : undefined;
}

/** The OAuth access token Pi resolved, or undefined for any other credential kind. */
function oauthToken(auth: AuthResult): string | undefined {
  return auth.source === "OAuth" && auth.auth.apiKey ? auth.auth.apiKey : undefined;
}

/** Percent-based amount clamped to 0..100. */
function percentAmount(usedPercent: number): UsageAmount {
  const clamped = Math.min(Math.max(usedPercent, 0), 100);
  const usedFraction = clamped / 100;
  return { usedFraction, remainingFraction: Math.max(0, 1 - usedFraction), unit: "percent" };
}

function report(
  provider: string,
  status: UsageReport["status"],
  limits: UsageLimit[],
  diagnostic?: string,
): UsageReport {
  const base = { provider, fetchedAt: Date.now(), limits, status };
  return diagnostic === undefined ? base : { ...base, diagnostic };
}

function errorReport(provider: string, diagnostic: string): UsageReport {
  return report(provider, "error", [], diagnostic);
}

function unavailableReport(provider: string, diagnostic: string): UsageReport {
  return report(provider, "unavailable", [], diagnostic);
}

/** Diagnostic for a non-2xx status, mirroring the shared collector vocabulary. */
function httpFailure(provider: string, status: number): UsageReport {
  if (status === 401 || status === 403) return unavailableReport(provider, "unauthorized");
  if (status === 429) return unavailableReport(provider, "rate_limited");
  return errorReport(provider, `http_${status}`);
}

/** Map a thrown request error onto its stable diagnostic; never the message text. */
function requestFailure(provider: string, error: unknown): UsageReport {
  const message = error instanceof Error ? error.message : "";
  return errorReport(provider, REQUEST_DIAGNOSTICS[message] ? message : "request_failed");
}

/**
 * Perform one bounded request and classify transport/HTTP failures. Returns
 * the parsed body only for a 2xx response.
 */
async function fetchUsageBody(
  provider: string,
  request: UsageRequest,
  url: string,
  headers: Record<string, string>,
  signal: AbortSignal,
): Promise<{ body: unknown } | { failure: UsageReport }> {
  let response: { status: number; body: unknown };
  try {
    response = await request(url, { method: "GET", headers }, signal);
  } catch (error) {
    return { failure: requestFailure(provider, error) };
  }
  if (response.status < 200 || response.status >= 300) {
    return { failure: httpFailure(provider, response.status) };
  }
  return { body: response.body };
}

// ---------------------------------------------------------------------------
// OpenAI Codex
// ---------------------------------------------------------------------------

interface CodexWindow {
  usedPercent?: number;
  limitWindowSeconds?: number;
  resetAfterSeconds?: number;
  resetAt?: number;
}

interface CodexMeter {
  /** Limit id prefix; empty for the primary chat meter. */
  slug: string;
  /** Display suffix such as "Spark"; undefined for the primary chat meter. */
  displayName?: string;
  primary?: CodexWindow;
  secondary?: CodexWindow;
}

function base64UrlDecode(input: string): string {
  const base64 = input.replace(/-/g, "+").replace(/_/g, "/");
  const padded = base64 + "=".repeat((4 - (base64.length % 4)) % 4);
  return Buffer.from(padded, "base64").toString("utf8");
}

/** `chatgpt_account_id` from the bearer JWT; only ever sent back to OpenAI as a header. */
function extractCodexAccountId(token: string): string | undefined {
  const parts = token.split(".");
  if (parts.length !== 3 || !parts[1]) return undefined;
  try {
    const payload: unknown = JSON.parse(base64UrlDecode(parts[1]));
    if (!isRecord(payload)) return undefined;
    const claim = payload[CODEX_JWT_AUTH_CLAIM];
    if (!isRecord(claim)) return undefined;
    const accountId = claim["chatgpt_account_id"];
    return typeof accountId === "string" && accountId ? accountId : undefined;
  } catch {
    return undefined;
  }
}

function parseCodexWindow(payload: unknown): CodexWindow | undefined {
  if (!isRecord(payload)) return undefined;
  const window: CodexWindow = {};
  const usedPercent = toNumber(payload["used_percent"]);
  const limitWindowSeconds = toNumber(payload["limit_window_seconds"]);
  const resetAfterSeconds = toNumber(payload["reset_after_seconds"]);
  const resetAt = toNumber(payload["reset_at"]);
  if (usedPercent !== undefined) window.usedPercent = usedPercent;
  if (limitWindowSeconds !== undefined) window.limitWindowSeconds = limitWindowSeconds;
  if (resetAfterSeconds !== undefined) window.resetAfterSeconds = resetAfterSeconds;
  if (resetAt !== undefined) window.resetAt = resetAt;
  return Object.keys(window).length === 0 ? undefined : window;
}

function parseCodexMeterWindows(rateLimit: unknown): Pick<CodexMeter, "primary" | "secondary"> {
  if (!isRecord(rateLimit)) return {};
  const windows: Pick<CodexMeter, "primary" | "secondary"> = {};
  const primary = parseCodexWindow(rateLimit["primary_window"]);
  const secondary = parseCodexWindow(rateLimit["secondary_window"]);
  if (primary) windows.primary = primary;
  if (secondary) windows.secondary = secondary;
  return windows;
}

/** Slug for an additional meter; Spark is recognised by name or by its internal feature id. */
function codexMeterSlug(limitName: string | undefined, meteredFeature: string | undefined): string {
  const probe = `${limitName ?? ""} ${meteredFeature ?? ""}`.toLowerCase();
  if (probe.includes("spark") || probe.includes("bengalfox")) return "spark";
  const source = (meteredFeature ?? limitName ?? "extra").toLowerCase();
  return (
    source
      .replace(/^codex[-_]/, "")
      .replace(/[^a-z0-9]+/g, "-")
      .replace(/^-+|-+$/g, "") || "extra"
  );
}

function codexMeterDisplayName(slug: string, limitName: string | undefined): string {
  if (slug === "spark") return "Spark";
  if (limitName) return limitName;
  return slug.replace(/(^|-)([a-z])/g, (_match, sep: string, ch: string) =>
    `${sep === "-" ? " " : ""}${ch.toUpperCase()}`,
  );
}

function parseCodexAdditionalMeter(payload: unknown): CodexMeter | undefined {
  if (!isRecord(payload)) return undefined;
  const limitName = typeof payload["limit_name"] === "string" ? payload["limit_name"] : undefined;
  const meteredFeature =
    typeof payload["metered_feature"] === "string" ? payload["metered_feature"] : undefined;
  const rateLimit = payload["rate_limit"];
  if (!isRecord(rateLimit)) return undefined;
  const windows = parseCodexMeterWindows(rateLimit);
  if (!windows.primary && !windows.secondary) return undefined;
  const slug = codexMeterSlug(limitName, meteredFeature);
  return { slug, displayName: codexMeterDisplayName(slug, limitName), ...windows };
}

/** Every meter in a `/wham/usage` payload: the chat meter first, then Spark and other extras. */
function parseCodexMeters(payload: Record<string, unknown>): CodexMeter[] {
  const meters: CodexMeter[] = [];
  const chatWindows = parseCodexMeterWindows(payload["rate_limit"]);
  if (chatWindows.primary || chatWindows.secondary) meters.push({ slug: "", ...chatWindows });
  const additional = payload["additional_rate_limits"];
  if (Array.isArray(additional)) {
    for (const entry of additional) {
      const meter = parseCodexAdditionalMeter(entry);
      if (meter) meters.push(meter);
    }
  }
  return meters;
}

function formatCodexWindowLabel(seconds: number): string {
  const daySeconds = 86_400;
  const [value, unit] =
    seconds >= daySeconds
      ? [Math.round(seconds / daySeconds), "day"]
      : [Math.max(1, Math.round(seconds / 3600)), "hour"];
  return `${value} ${value === 1 ? unit : `${unit}s`}`;
}

function resolveCodexReset(window: CodexWindow, nowMs: number): number | undefined {
  if (window.resetAt !== undefined) {
    return window.resetAt > 1_000_000_000_000 ? window.resetAt : window.resetAt * 1000;
  }
  if (window.resetAfterSeconds !== undefined) return nowMs + window.resetAfterSeconds * 1000;
  return undefined;
}

function buildCodexLimit(
  meter: CodexMeter,
  key: "primary" | "secondary",
  window: CodexWindow,
  nowMs: number,
): UsageLimit {
  const windowLabel =
    window.limitWindowSeconds !== undefined
      ? formatCodexWindowLabel(window.limitWindowSeconds)
      : key === "primary"
        ? "Primary window"
        : "Secondary window";
  const resetsAt = resolveCodexReset(window, nowMs);
  return {
    id: meter.slug ? `${meter.slug}:${key}` : key,
    label: meter.displayName ? `${windowLabel} (${meter.displayName})` : windowLabel,
    window: resetsAt === undefined ? { label: windowLabel } : { label: windowLabel, resetsAt },
    amount: window.usedPercent === undefined ? { unit: "percent" } : percentAmount(window.usedPercent),
  };
}

function buildCodexLimits(meters: CodexMeter[], nowMs: number): UsageLimit[] {
  const limits: UsageLimit[] = [];
  for (const meter of meters) {
    if (meter.primary) limits.push(buildCodexLimit(meter, "primary", meter.primary, nowMs));
    if (meter.secondary) limits.push(buildCodexLimit(meter, "secondary", meter.secondary, nowMs));
  }
  return limits;
}

/** ChatGPT `/wham/usage`: 5h/weekly chat windows plus Spark and other additional meters. */
export const openaiCodexUsage: UsageCollector = async (auth, request, signal) => {
  const provider = "openai-codex";
  const token = oauthToken(auth);
  if (!token) return unavailableReport(provider, "no_credential");

  const headers: Record<string, string> = {
    Authorization: `Bearer ${token}`,
    "User-Agent": CODEX_USER_AGENT,
  };
  const accountId = extractCodexAccountId(token);
  if (accountId) headers["ChatGPT-Account-Id"] = accountId;

  const result = await fetchUsageBody(provider, request, CODEX_USAGE_URL, headers, signal);
  if ("failure" in result) return result.failure;
  if (!isRecord(result.body)) return errorReport(provider, "response_malformed");

  const limits = buildCodexLimits(parseCodexMeters(result.body), Date.now());
  if (limits.length === 0) return unavailableReport(provider, "no_quota");
  return report(provider, "available", limits);
};

// ---------------------------------------------------------------------------
// Anthropic (Claude Pro/Max subscription)
// ---------------------------------------------------------------------------

interface ClaudeBucket {
  utilization?: number;
  resetsAt?: number;
}

interface ClaudeApiLimitEntry {
  kind: string;
  bucket: ClaudeBucket;
  displayName?: string;
}

function claudeBucket(utilization: number | undefined, resetsAt: number | undefined): ClaudeBucket | undefined {
  if (utilization === undefined && resetsAt === undefined) return undefined;
  const bucket: ClaudeBucket = {};
  if (utilization !== undefined) bucket.utilization = utilization;
  if (resetsAt !== undefined) bucket.resetsAt = resetsAt;
  return bucket;
}

function parseClaudeBucket(value: unknown): ClaudeBucket | undefined {
  if (!isRecord(value)) return undefined;
  return claudeBucket(toNumber(value["utilization"]), parseIsoTimestamp(value["resets_at"]));
}

/**
 * Generic `limits[]` entries. Account-wide `session` / `weekly_all` rows back
 * the legacy buckets; `weekly_scoped` rows carry per-model-family caps
 * (Fable, Mythos) named by `scope.model.display_name`. `is_active` only marks
 * the currently binding row, so it is ignored rather than used as a filter.
 */
function parseClaudeApiLimitEntries(raw: unknown): ClaudeApiLimitEntry[] {
  if (!Array.isArray(raw)) return [];
  const entries: ClaudeApiLimitEntry[] = [];
  for (const entry of raw) {
    if (!isRecord(entry) || typeof entry["kind"] !== "string") continue;
    const bucket = claudeBucket(toNumber(entry["percent"]), parseIsoTimestamp(entry["resets_at"]));
    if (!bucket) continue;
    const parsed: ClaudeApiLimitEntry = { kind: entry["kind"], bucket };
    const scope = entry["scope"];
    const model = isRecord(scope) ? scope["model"] : undefined;
    const displayName = isRecord(model) ? model["display_name"] : undefined;
    if (typeof displayName === "string" && displayName.trim()) parsed.displayName = displayName.trim();
    entries.push(parsed);
  }
  return entries;
}

function buildClaudeLimit(
  id: string,
  label: string,
  windowLabel: string,
  bucket: ClaudeBucket | undefined,
): UsageLimit | undefined {
  if (!bucket || bucket.utilization === undefined) return undefined;
  return {
    id,
    label,
    window:
      bucket.resetsAt === undefined ? { label: windowLabel } : { label: windowLabel, resetsAt: bucket.resetsAt },
    amount: percentAmount(bucket.utilization),
  };
}

function slugifyClaudeDisplayName(displayName: string): string {
  return displayName
    .trim()
    .toLowerCase()
    .replace(/[^a-z0-9]+/g, "-")
    .replace(/^-+|-+$/g, "");
}

/** Per-model-family weekly rows (Fable, Mythos, ...), deduplicated by slug. */
function buildClaudeScopedWeeklyLimits(
  entries: readonly ClaudeApiLimitEntry[],
  emittedLegacySlugs: readonly string[],
): UsageLimit[] {
  const seen = new Set(emittedLegacySlugs);
  const limits: UsageLimit[] = [];
  for (const entry of entries) {
    if (entry.kind !== "weekly_scoped" || !entry.displayName) continue;
    const slug = slugifyClaudeDisplayName(entry.displayName);
    if (!slug || seen.has(slug)) continue;
    seen.add(slug);
    const limit = buildClaudeLimit(`7d:${slug}`, `Claude 7 Day (${entry.displayName})`, "7 Day", entry.bucket);
    if (limit) limits.push(limit);
  }
  return limits;
}

/** Non-negative dollar value from a minor-unit integer and decimal exponent; USD only. */
function parseDollarAmount(
  amountMinor: unknown,
  exponent: unknown,
  currency: unknown,
  currencyRequired: boolean,
): number | undefined {
  if (
    typeof amountMinor !== "number" ||
    !Number.isSafeInteger(amountMinor) ||
    amountMinor < 0 ||
    typeof exponent !== "number" ||
    !Number.isSafeInteger(exponent) ||
    exponent < 0
  ) {
    return undefined;
  }
  if (currency === undefined) {
    if (currencyRequired) return undefined;
  } else if (typeof currency !== "string" || currency.toUpperCase() !== "USD") {
    return undefined;
  }
  const dollars = amountMinor / 10 ** exponent;
  return Number.isFinite(dollars) ? dollars : undefined;
}

/** Extra-usage spend as `{used, limit}` dollars, from the `spend` block or the legacy `extra_usage` block. */
function parseClaudeExtraUsage(payload: Record<string, unknown>): { used: number; limit: number } | undefined {
  const spend = payload["spend"];
  if (spend !== null && spend !== undefined) {
    if (!isRecord(spend) || spend["enabled"] !== true || !isRecord(spend["used"]) || !isRecord(spend["limit"])) {
      return undefined;
    }
    const used = spend["used"];
    const limit = spend["limit"];
    const usedDollars = parseDollarAmount(used["amount_minor"], used["exponent"], used["currency"], true);
    const limitDollars = parseDollarAmount(limit["amount_minor"], limit["exponent"], limit["currency"], true);
    if (usedDollars === undefined || limitDollars === undefined || limitDollars <= 0) return undefined;
    return { used: usedDollars, limit: limitDollars };
  }
  const legacy = payload["extra_usage"];
  if (!isRecord(legacy) || legacy["is_enabled"] !== true) return undefined;
  const decimalPlaces = legacy["decimal_places"] === undefined ? 2 : legacy["decimal_places"];
  const used = parseDollarAmount(legacy["used_credits"], decimalPlaces, legacy["currency"], false);
  const limit = parseDollarAmount(legacy["monthly_limit"], decimalPlaces, legacy["currency"], false);
  if (used === undefined || limit === undefined || limit <= 0) return undefined;
  return { used, limit };
}

/** Capped extra-usage spend as a fraction; uncapped spend has no fraction to show and is omitted. */
function buildClaudeExtraUsageLimit(payload: Record<string, unknown>): UsageLimit | undefined {
  const parsed = parseClaudeExtraUsage(payload);
  if (!parsed) return undefined;
  const rawUsedFraction = parsed.used / parsed.limit;
  if (!Number.isFinite(rawUsedFraction)) return undefined;
  const usedFraction = Math.min(1, rawUsedFraction);
  const remainingFraction = 1 - usedFraction;
  return {
    id: "extra",
    label: "Claude Extra Usage",
    window: { label: "Monthly" },
    amount: { usedFraction, remainingFraction, unit: "usd" },
  };
}

function buildClaudeLimits(payload: Record<string, unknown>): UsageLimit[] {
  const entries = parseClaudeApiLimitEntries(payload["limits"]);
  const fiveHour =
    parseClaudeBucket(payload["five_hour"]) ?? entries.find((entry) => entry.kind === "session")?.bucket;
  const sevenDay =
    parseClaudeBucket(payload["seven_day"]) ?? entries.find((entry) => entry.kind === "weekly_all")?.bucket;
  const opus = buildClaudeLimit(
    "7d:opus",
    "Claude 7 Day (Opus)",
    "7 Day",
    parseClaudeBucket(payload["seven_day_opus"]),
  );
  const sonnet = buildClaudeLimit(
    "7d:sonnet",
    "Claude 7 Day (Sonnet)",
    "7 Day",
    parseClaudeBucket(payload["seven_day_sonnet"]),
  );
  const emittedLegacySlugs = [opus && "opus", sonnet && "sonnet"].filter(
    (slug): slug is string => slug !== undefined,
  );
  return [
    buildClaudeLimit("5h", "Claude 5 Hour", "5 Hour", fiveHour),
    buildClaudeLimit("7d", "Claude 7 Day", "7 Day", sevenDay),
    opus,
    sonnet,
    ...buildClaudeScopedWeeklyLimits(entries, emittedLegacySlugs),
    buildClaudeExtraUsageLimit(payload),
  ].filter((limit): limit is UsageLimit => limit !== undefined);
}

/**
 * Anthropic `/api/oauth/usage`: 5h/7d subscription windows, per-model weekly
 * caps, and capped extra usage. Plain API keys have no subscription usage.
 */
export const anthropicUsage: UsageCollector = async (auth, request, signal) => {
  const provider = "anthropic";
  const token = oauthToken(auth);
  if (!token) return unavailableReport(provider, "no_credential");

  const headers: Record<string, string> = { ...CLAUDE_HEADERS, authorization: `Bearer ${token}` };
  const result = await fetchUsageBody(provider, request, CLAUDE_USAGE_URL, headers, signal);
  if ("failure" in result) return result.failure;
  if (!isRecord(result.body)) return errorReport(provider, "response_malformed");

  const limits = buildClaudeLimits(result.body);
  if (limits.length === 0) return unavailableReport(provider, "no_quota");
  return report(provider, "available", limits);
};
