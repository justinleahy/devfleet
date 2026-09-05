/**
 * Z.AI and OpenCode Go usage collectors.
 *
 * Both providers authenticate with a plain API key that Pi resolves into
 * `auth.auth.apiKey`; Z.AI sends it verbatim as `Authorization`, OpenCode Go
 * as a bearer token. Endpoints are fixed HTTPS URLs (no base-URL derivation),
 * response bodies are validated once against a TypeBox schema, and quotas are
 * reduced to used/remaining fractions. Reports never carry upstream text, raw
 * payloads, or credentials.
 */
import { Type, type Static } from "typebox";
import { Value } from "typebox/value";
import type {
  AuthResult,
  UsageCollector,
  UsageLimit,
  UsageReport,
  UsageRequest,
  UsageWindow,
} from "../usageTypes.ts";

const ZAI_PROVIDER = "zai";
const ZAI_QUOTA_URL = "https://api.z.ai/api/monitor/usage/quota/limit";

const OPENCODE_GO_PROVIDER = "opencode-go";
const OPENCODE_GO_USAGE_URL = "https://opencode.ai/zen/go/v1/usage";

/** Diagnostics the injected `request` throws as `Error.message`; forwarded verbatim. */
const REQUEST_DIAGNOSTICS: Record<string, true> = {
  origin_not_allowed: true,
  redirect_refused: true,
  response_too_large: true,
  request_timeout: true,
  request_failed: true,
};

type Decoded = { limits: UsageLimit[] } | { diagnostic: string };

function report(
  provider: string,
  status: UsageReport["status"],
  limits: UsageLimit[],
  diagnostic?: string,
): UsageReport {
  const result: UsageReport = { provider, fetchedAt: Date.now(), limits, status };
  if (diagnostic !== undefined) result.diagnostic = diagnostic;
  return result;
}

/**
 * Run one GET against a fixed URL and turn the outcome into a report:
 * transport failures and non-2xx statuses map to stable diagnostics, 2xx
 * bodies go through `decode`, and a well-formed body with no windows is
 * "unavailable" rather than an empty success.
 */
async function collect(
  provider: string,
  url: string,
  headers: Record<string, string>,
  request: UsageRequest,
  signal: AbortSignal,
  decode: (body: unknown) => Decoded,
): Promise<UsageReport> {
  let status: number;
  let body: unknown;
  try {
    ({ status, body } = await request(url, { method: "GET", headers }, signal));
  } catch (error) {
    const message = error instanceof Error ? error.message : "";
    if (REQUEST_DIAGNOSTICS[message]) return report(provider, "error", [], message);
    return report(provider, "error", [], signal.aborted ? "request_timeout" : "request_failed");
  }
  if (status === 401 || status === 403) return report(provider, "unavailable", [], "unauthorized");
  if (status === 429) return report(provider, "unavailable", [], "rate_limited");
  if (status < 200 || status >= 300) return report(provider, "error", [], `http_${status}`);

  const decoded = decode(body);
  if ("diagnostic" in decoded) return report(provider, "error", [], decoded.diagnostic);
  if (decoded.limits.length === 0) return report(provider, "unavailable", [], "no_quota");
  return report(provider, "available", decoded.limits);
}

// ---------------------------------------------------------------------------
// Z.AI — GET /api/monitor/usage/quota/limit
// ---------------------------------------------------------------------------

const ZAI_RENDERED_TYPES = ["TOKENS_LIMIT", "TIME_LIMIT", "CREDIT_LIMIT"] as const;

/** A `data.limits` entry we render: token, credit, or request quota with a percentage. */
const ZaiRenderedLimit = Type.Object({
  type: Type.Union(ZAI_RENDERED_TYPES.map((type) => Type.Literal(type))),
  percentage: Type.Number({ minimum: 0 }),
  /** Window unit: 3 hours, 4 days, 5 months, 6 weeks. */
  unit: Type.Optional(Type.Number()),
  /** Window length in `unit`s. */
  number: Type.Optional(Type.Number()),
  /** Epoch seconds or milliseconds. */
  nextResetTime: Type.Optional(Type.Number()),
  usageDetails: Type.Optional(Type.Array(Type.Object({ modelCode: Type.Optional(Type.String()) }))),
});
type ZaiRenderedLimit = Static<typeof ZaiRenderedLimit>;

/** Any other limit kind is skipped without inspecting its fields. */
const ZaiIgnoredLimit = Type.Object({
  type: Type.String({ pattern: `^(?!(${ZAI_RENDERED_TYPES.join("|")})$)` }),
});

const ZaiQuotaPayload = Type.Object({
  success: Type.Literal(true),
  data: Type.Object({
    limits: Type.Optional(Type.Array(Type.Union([ZaiRenderedLimit, ZaiIgnoredLimit]))),
  }),
});

function zaiWindow(item: ZaiRenderedLimit): { id: string; label: string } {
  const count = item.number !== undefined && item.number > 0 ? item.number : 1;
  const plural = (singular: string) => `${count} ${singular}${count === 1 ? "" : "s"}`;
  switch (item.unit) {
    case 3:
      return { id: `${count}h`, label: plural("Hour") };
    case 4:
      return { id: `${count}d`, label: plural("Day") };
    case 5:
      return { id: `${count}mo`, label: count === 1 ? "Monthly" : plural("Month") };
    case 6:
      return { id: "1w", label: "Weekly" };
    default:
      return { id: item.unit === undefined ? "quota" : `${count}u${item.unit}`, label: "Quota" };
  }
}

/** The request-count limit shared by search-prime, web-reader and zread is Z.AI's "Zread" quota. */
function isZaiZreadLimit(item: ZaiRenderedLimit): boolean {
  const codes = new Set<string>();
  for (const detail of item.usageDetails ?? []) {
    if (detail.modelCode !== undefined) codes.add(detail.modelCode);
  }
  return codes.has("search-prime") && codes.has("web-reader") && codes.has("zread");
}

function zaiLimit(item: ZaiRenderedLimit): UsageLimit {
  const { id: windowId, label: windowLabel } = zaiWindow(item);
  const window: UsageWindow = { label: windowLabel };
  if (item.nextResetTime !== undefined && item.nextResetTime > 0) {
    window.resetsAt =
      item.nextResetTime > 1_000_000_000_000 ? item.nextResetTime : item.nextResetTime * 1000;
  }
  // Z.AI may report more than 100% once a window is exhausted.
  const percent = Math.min(item.percentage, 100);
  const amount = { usedFraction: percent / 100, remainingFraction: (100 - percent) / 100 };

  if (item.type === "TOKENS_LIMIT") {
    return {
      id: `zai:tokens:${windowId}`,
      label: `${windowLabel} token quota`,
      window,
      amount: { ...amount, unit: "tokens" },
    };
  }
  if (item.type === "CREDIT_LIMIT") {
    return {
      id: `zai:credits:${windowId}`,
      label: `${windowLabel} credit quota`,
      window,
      amount: { ...amount, unit: "credits" },
    };
  }
  const zread = isZaiZreadLimit(item);
  return {
    id: zread ? `zai:features:zread:${windowId}` : `zai:requests:${windowId}`,
    label: zread ? "Zread quota" : "Request quota",
    window,
    amount: { ...amount, unit: "requests" },
  };
}

function decodeZai(body: unknown): Decoded {
  if (!Value.Check(ZaiQuotaPayload, body)) return { diagnostic: "response_malformed" };
  const limits: UsageLimit[] = [];
  for (const item of body.data.limits ?? []) {
    // The union admits ignored kinds with arbitrary fields, so re-check rather than key-sniff.
    if (Value.Check(ZaiRenderedLimit, item)) limits.push(zaiLimit(item));
  }
  return { limits };
}

export const zaiUsage: UsageCollector = (auth: AuthResult, request, signal) => {
  const apiKey = auth.auth.apiKey;
  if (!apiKey) return Promise.resolve(report(ZAI_PROVIDER, "unavailable", [], "no_credential"));
  // Z.AI expects the raw key as the Authorization value, without a Bearer prefix.
  const headers = { Authorization: apiKey, Accept: "application/json" };
  return collect(ZAI_PROVIDER, ZAI_QUOTA_URL, headers, request, signal, decodeZai);
};

// ---------------------------------------------------------------------------
// OpenCode Go — GET /zen/go/v1/usage
// ---------------------------------------------------------------------------

/**
 * One `usage.<window>` entry of the first-party but undocumented route:
 * `percent` is a clamped integer 0-100 and `resetsAt` an ISO timestamp
 * computed server side. The monthly window anchors on the subscription
 * anniversary rather than a rolling span.
 */
const OpencodeGoWindow = Type.Object({
  status: Type.Union([Type.Literal("ok"), Type.Literal("rate-limited")]),
  percent: Type.Number({ minimum: 0, maximum: 100 }),
  resetsAt: Type.String(),
});
type OpencodeGoWindow = Static<typeof OpencodeGoWindow>;

/** All three windows are required: a partial report would silently hide the missing window. */
const OpencodeGoPayload = Type.Object({
  usage: Type.Object({ rolling: OpencodeGoWindow, weekly: OpencodeGoWindow, monthly: OpencodeGoWindow }),
});

const OPENCODE_GO_WINDOWS = [
  { key: "rolling", id: "rolling-5h", label: "5 Hour" },
  { key: "weekly", id: "weekly", label: "Weekly" },
  { key: "monthly", id: "monthly", label: "Monthly" },
] as const;

function opencodeGoLimit(
  descriptor: (typeof OPENCODE_GO_WINDOWS)[number],
  payload: OpencodeGoWindow,
): UsageLimit | undefined {
  const resetsAt = Date.parse(payload.resetsAt);
  if (!Number.isFinite(resetsAt)) return undefined;
  // A rate-limited window is exhausted regardless of the (floored) percent;
  // the report has no per-limit status, so the fraction carries that fact.
  const percent = payload.status === "rate-limited" ? 100 : payload.percent;
  return {
    id: descriptor.id,
    label: `${descriptor.label} limit`,
    window: { label: descriptor.label, resetsAt },
    amount: { usedFraction: percent / 100, remainingFraction: (100 - percent) / 100, unit: "percent" },
  };
}

function decodeOpencodeGo(body: unknown): Decoded {
  if (!Value.Check(OpencodeGoPayload, body)) return { diagnostic: "response_malformed" };
  const limits: UsageLimit[] = [];
  for (const descriptor of OPENCODE_GO_WINDOWS) {
    const limit = opencodeGoLimit(descriptor, body.usage[descriptor.key]);
    if (!limit) return { diagnostic: "response_malformed" };
    limits.push(limit);
  }
  return { limits };
}

export const opencodeGoUsage: UsageCollector = (auth: AuthResult, request, signal) => {
  const apiKey = auth.auth.apiKey;
  if (!apiKey) return Promise.resolve(report(OPENCODE_GO_PROVIDER, "unavailable", [], "no_credential"));
  const headers = { Authorization: `Bearer ${apiKey}`, Accept: "application/json" };
  return collect(OPENCODE_GO_PROVIDER, OPENCODE_GO_USAGE_URL, headers, request, signal, decodeOpencodeGo);
};
