/**
 * OpenAI Codex and Anthropic usage collectors over a fake bounded request:
 * exact endpoints and auth headers, normalized fractions and resets, Spark and
 * Fable additional windows, stable diagnostics for every failure class, and
 * secret-free serialization.
 */
import assert from "node:assert/strict";
import { describe, it } from "node:test";
import type { AuthResult, UsageReport, UsageRequest } from "../src/usageTypes.ts";
import { anthropicUsage, openaiCodexUsage } from "../src/usageProviders/openaiAnthropic.ts";

const ACCOUNT_ID = "acct_12345";
const EMAIL = "someone@example.com";
const REFRESH_TOKEN = "rt-super-secret-refresh";

/** Unsigned JWT carrying the ChatGPT account id and profile email claims. */
function fakeJwt(claims: Record<string, unknown>): string {
  const encode = (value: unknown) => Buffer.from(JSON.stringify(value)).toString("base64url");
  return `${encode({ alg: "none" })}.${encode(claims)}.sig`;
}

const CODEX_TOKEN = fakeJwt({
  "https://api.openai.com/auth": { chatgpt_account_id: ACCOUNT_ID },
  "https://api.openai.com/profile": { email: EMAIL },
});
const CLAUDE_TOKEN = "sk-ant-oat01-secret-access-token";

const codexOAuth: AuthResult = { auth: { apiKey: CODEX_TOKEN }, source: "OAuth" };
const claudeOAuth: AuthResult = { auth: { apiKey: CLAUDE_TOKEN }, source: "OAuth" };
const claudeApiKey: AuthResult = { auth: { apiKey: "sk-ant-api03-secret" }, source: "ANTHROPIC_API_KEY" };

interface Captured {
  url: string;
  init: RequestInit;
}

/** Request stub returning one canned response and recording what was asked. */
function fakeRequest(
  response: { status: number; body: unknown } | Error,
): { request: UsageRequest; calls: Captured[] } {
  const calls: Captured[] = [];
  const request: UsageRequest = async (url, init) => {
    calls.push({ url, init });
    if (response instanceof Error) throw response;
    return response;
  };
  return { request, calls };
}

function headersOf(call: Captured): Record<string, string> {
  return call.init.headers as Record<string, string>;
}

const signal = new AbortController().signal;

/** Every string that must never appear in a serialized report. */
const SECRETS = [CODEX_TOKEN, CLAUDE_TOKEN, ACCOUNT_ID, EMAIL, REFRESH_TOKEN, "sk-ant-api03-secret"];

function assertSecretFree(report: UsageReport): void {
  const serialized = JSON.stringify(report);
  for (const secret of SECRETS) {
    assert.ok(!serialized.includes(secret), `report leaks ${secret}`);
  }
  if (report.diagnostic !== undefined) {
    assert.match(report.diagnostic, /^[a-z0-9_]{1,40}$/);
  }
}

const NOW = 1_800_000_000_000;

const codexPayload = {
  plan_type: "pro",
  rate_limit: {
    allowed: true,
    limit_reached: false,
    primary_window: { used_percent: 42.5, limit_window_seconds: 18_000, reset_after_seconds: 3_600 },
    secondary_window: { used_percent: 130, limit_window_seconds: 604_800, reset_at: 1_800_500_000 },
  },
  additional_rate_limits: [
    {
      limit_name: "Spark",
      metered_feature: "codex_bengalfox",
      rate_limit: {
        allowed: true,
        limit_reached: false,
        primary_window: { used_percent: 10, limit_window_seconds: 18_000, reset_at: NOW + 60_000 },
        secondary_window: { used_percent: 0, limit_window_seconds: 604_800 },
      },
    },
  ],
  rate_limit_reset_credits: { available_count: 2 },
  email: EMAIL,
  refresh_token: REFRESH_TOKEN,
};

describe("openaiCodexUsage", () => {
  it("fetches /wham/usage with the bearer and account header and normalizes every window", async () => {
    const { request, calls } = fakeRequest({ status: 200, body: codexPayload });
    const before = Date.now();
    const report = await openaiCodexUsage(codexOAuth, request, signal);

    assert.equal(calls.length, 1);
    assert.equal(calls[0]?.url, "https://chatgpt.com/backend-api/wham/usage");
    assert.equal(calls[0]?.init.method, "GET");
    const headers = headersOf(calls[0]!);
    assert.equal(headers["Authorization"], `Bearer ${CODEX_TOKEN}`);
    assert.equal(headers["ChatGPT-Account-Id"], ACCOUNT_ID);
    assert.ok(headers["User-Agent"]);

    assert.equal(report.provider, "openai-codex");
    assert.equal(report.status, "available");
    assert.equal(report.diagnostic, undefined);
    assert.ok(report.fetchedAt >= before);
    assert.deepEqual(
      report.limits.map((limit) => [limit.id, limit.label, limit.window.label]),
      [
        ["primary", "5 hours", "5 hours"],
        ["secondary", "7 days", "7 days"],
        ["spark:primary", "5 hours (Spark)", "5 hours"],
        ["spark:secondary", "7 days (Spark)", "7 days"],
      ],
    );

    const [primary, secondary, sparkPrimary, sparkSecondary] = report.limits;
    assert.deepEqual(primary?.amount, { usedFraction: 0.425, remainingFraction: 0.575, unit: "percent" });
    // reset_after_seconds is relative to now.
    assert.ok(primary!.window.resetsAt! >= before + 3_600_000);
    assert.ok(primary!.window.resetsAt! <= Date.now() + 3_600_000);
    // Over-100 percentages clamp; second-precision reset_at is scaled to ms.
    assert.deepEqual(secondary?.amount, { usedFraction: 1, remainingFraction: 0, unit: "percent" });
    assert.equal(secondary?.window.resetsAt, 1_800_500_000_000);
    // Millisecond reset_at passes through untouched; a window without a reset omits the key.
    assert.equal(sparkPrimary?.window.resetsAt, NOW + 60_000);
    assert.deepEqual(sparkSecondary?.window, { label: "7 days" });
    assert.deepEqual(sparkSecondary?.amount, { usedFraction: 0, remainingFraction: 1, unit: "percent" });
    assertSecretFree(report);
  });

  it("labels non-Spark additional meters from their feature id and keeps unknown percentages fraction-free", async () => {
    const { request } = fakeRequest({
      status: 200,
      body: {
        rate_limit: { primary_window: { limit_window_seconds: 3_600 } },
        additional_rate_limits: [
          { metered_feature: "codex_image_gen", rate_limit: { primary_window: { used_percent: 5 } } },
          { limit_name: "flags only", rate_limit: { allowed: true, limit_reached: false } },
          "garbage",
        ],
      },
    });
    const report = await openaiCodexUsage(codexOAuth, request, signal);
    assert.equal(report.status, "available");
    assert.deepEqual(
      report.limits.map((limit) => [limit.id, limit.label, limit.amount]),
      [
        ["primary", "1 hour", { unit: "percent" }],
        ["image-gen:primary", "Primary window (Image Gen)", { usedFraction: 0.05, remainingFraction: 0.95, unit: "percent" }],
      ],
    );
    assertSecretFree(report);
  });

  it("omits the account header when the token carries no account claim", async () => {
    const { request, calls } = fakeRequest({ status: 200, body: codexPayload });
    await openaiCodexUsage({ auth: { apiKey: "opaque-token" }, source: "OAuth" }, request, signal);
    assert.equal(headersOf(calls[0]!)["ChatGPT-Account-Id"], undefined);
    assert.equal(headersOf(calls[0]!)["Authorization"], "Bearer opaque-token");
  });

  it("reports no_credential without a network call for non-OAuth or empty credentials", async () => {
    for (const auth of [
      { auth: { apiKey: "sk-something" }, source: "OPENAI_API_KEY" },
      { auth: {}, source: "OAuth" },
      { auth: { headers: { Authorization: "Bearer x" } } },
    ] satisfies AuthResult[]) {
      const { request, calls } = fakeRequest({ status: 200, body: codexPayload });
      const report = await openaiCodexUsage(auth, request, signal);
      assert.equal(calls.length, 0);
      assert.deepEqual([report.status, report.diagnostic, report.limits], ["unavailable", "no_credential", []]);
      assertSecretFree(report);
    }
  });

  it("maps unauthorized, rate-limited, and other HTTP failures to stable diagnostics", async () => {
    const cases: Array<[number, UsageReport["status"], string]> = [
      [401, "unavailable", "unauthorized"],
      [403, "unavailable", "unauthorized"],
      [429, "unavailable", "rate_limited"],
      [500, "error", "http_500"],
      [302, "error", "http_302"],
    ];
    for (const [status, expectedStatus, diagnostic] of cases) {
      const { request } = fakeRequest({
        status,
        body: { error: { message: `token ${CODEX_TOKEN} for ${EMAIL} rejected` } },
      });
      const report = await openaiCodexUsage(codexOAuth, request, signal);
      assert.deepEqual([report.status, report.diagnostic, report.limits], [expectedStatus, diagnostic, []]);
      assertSecretFree(report);
    }
  });

  it("forwards known request failures and hides everything else behind request_failed", async () => {
    for (const message of ["request_timeout", "response_too_large", "origin_not_allowed", "redirect_refused"]) {
      const { request } = fakeRequest(new Error(message));
      const report = await openaiCodexUsage(codexOAuth, request, signal);
      assert.deepEqual([report.status, report.diagnostic], ["error", message]);
    }
    const { request } = fakeRequest(new Error(`ECONNRESET while sending ${CODEX_TOKEN}`));
    const report = await openaiCodexUsage(codexOAuth, request, signal);
    assert.deepEqual([report.status, report.diagnostic], ["error", "request_failed"]);
    assertSecretFree(report);
  });

  it("flags malformed bodies and reports no_quota when the payload carries no windows", async () => {
    for (const body of ["<html>", null, 42, ["rate_limit"]]) {
      const { request } = fakeRequest({ status: 200, body });
      const report = await openaiCodexUsage(codexOAuth, request, signal);
      assert.deepEqual([report.status, report.diagnostic], ["error", "response_malformed"]);
    }
    for (const body of [{}, { plan_type: "free", rate_limit: null }, { rate_limit: { allowed: true } }]) {
      const { request } = fakeRequest({ status: 200, body });
      const report = await openaiCodexUsage(codexOAuth, request, signal);
      assert.deepEqual([report.status, report.diagnostic, report.limits], ["unavailable", "no_quota", []]);
    }
  });
});

const claudePayload = {
  five_hour: { utilization: 37.5, resets_at: "2027-01-01T05:00:00Z" },
  seven_day: { utilization: 88, resets_at: "2027-01-05T00:00:00Z" },
  seven_day_opus: null,
  seven_day_sonnet: null,
  limits: [
    { kind: "session", percent: 37.5, resets_at: "2027-01-01T05:00:00Z", is_active: true },
    { kind: "weekly_all", percent: 88, resets_at: "2027-01-05T00:00:00Z", is_active: false },
    {
      kind: "weekly_scoped",
      percent: 100,
      resets_at: "2027-01-05T00:00:00Z",
      scope: { model: { display_name: "Fable" } },
      is_active: false,
    },
    { kind: "weekly_scoped", percent: 12, scope: { model: { display_name: "Fable" } } },
    { kind: "weekly_scoped", percent: 3, scope: { model: null } },
  ],
  spend: {
    enabled: true,
    used: { amount_minor: 2_550, currency: "USD", exponent: 2 },
    limit: { amount_minor: 10_000, currency: "USD", exponent: 2 },
  },
  account: { uuid: ACCOUNT_ID, email: EMAIL },
  email: EMAIL,
};

const claudeOverspendPayload = {
  five_hour: { utilization: 25 },
  seven_day: { utilization: 75 },
  spend: {
    enabled: true,
    used: { amount_minor: 12_500, currency: "USD", exponent: 2 },
    limit: { amount_minor: 10_000, currency: "USD", exponent: 2 },
  },
};

const claudeMixedLegacyAndScopedPayload = {
  seven_day_opus: { utilization: 25 },
  seven_day_sonnet: { utilization: 35 },
  limits: [
    { kind: "weekly_scoped", percent: 80, scope: { model: { display_name: "Opus" } } },
    { kind: "weekly_scoped", percent: 90, scope: { model: { display_name: "Sonnet" } } },
    { kind: "weekly_scoped", percent: 45, scope: { model: { display_name: "Haiku" } } },
  ],
};

describe("anthropicUsage", () => {
  it("fetches /api/oauth/usage with Claude Code headers and normalizes buckets, Fable, and extra usage", async () => {
    const { request, calls } = fakeRequest({ status: 200, body: claudePayload });
    const report = await anthropicUsage(claudeOAuth, request, signal);

    assert.equal(calls.length, 1);
    assert.equal(calls[0]?.url, "https://api.anthropic.com/api/oauth/usage");
    const headers = headersOf(calls[0]!);
    assert.equal(headers["authorization"], `Bearer ${CLAUDE_TOKEN}`);
    assert.match(headers["anthropic-beta"] ?? "", /oauth-2025-04-20/);
    assert.match(headers["user-agent"] ?? "", /^claude-cli\//);

    assert.equal(report.provider, "anthropic");
    assert.equal(report.status, "available");
    assert.deepEqual(report.limits, [
      {
        id: "5h",
        label: "Claude 5 Hour",
        window: { label: "5 Hour", resetsAt: Date.parse("2027-01-01T05:00:00Z") },
        amount: { usedFraction: 0.375, remainingFraction: 0.625, unit: "percent" },
      },
      {
        id: "7d",
        label: "Claude 7 Day",
        window: { label: "7 Day", resetsAt: Date.parse("2027-01-05T00:00:00Z") },
        amount: { usedFraction: 0.88, remainingFraction: 0.12, unit: "percent" },
      },
      {
        id: "7d:fable",
        label: "Claude 7 Day (Fable)",
        window: { label: "7 Day", resetsAt: Date.parse("2027-01-05T00:00:00Z") },
        amount: { usedFraction: 1, remainingFraction: 0, unit: "percent" },
      },
      {
        id: "extra",
        label: "Claude Extra Usage",
        window: { label: "Monthly" },
        amount: { usedFraction: 0.255, remainingFraction: 0.745, unit: "usd" },
      },
    ]);
    assertSecretFree(report);
  });

  it("clamps overspent extra usage while retaining the normal Claude windows", async () => {
    const { request } = fakeRequest({ status: 200, body: claudeOverspendPayload });
    const report = await anthropicUsage(claudeOAuth, request, signal);

    assert.deepEqual(report.limits, [
      {
        id: "5h",
        label: "Claude 5 Hour",
        window: { label: "5 Hour" },
        amount: { usedFraction: 0.25, remainingFraction: 0.75, unit: "percent" },
      },
      {
        id: "7d",
        label: "Claude 7 Day",
        window: { label: "7 Day" },
        amount: { usedFraction: 0.75, remainingFraction: 0.25, unit: "percent" },
      },
      {
        id: "extra",
        label: "Claude Extra Usage",
        window: { label: "Monthly" },
        amount: { usedFraction: 1, remainingFraction: 0, unit: "usd" },
      },
    ]);
  });

  it("prefers legacy Opus and Sonnet buckets over equivalent scoped rows", async () => {
    const { request } = fakeRequest({ status: 200, body: claudeMixedLegacyAndScopedPayload });
    const report = await anthropicUsage(claudeOAuth, request, signal);

    assert.deepEqual(
      report.limits.map((limit) => [limit.id, limit.amount.usedFraction]),
      [
        ["7d:opus", 0.25],
        ["7d:sonnet", 0.35],
        ["7d:haiku", 0.45],
      ],
    );
  });

  it("falls back to limits[] session/weekly_all rows and legacy buckets when the top-level buckets are null", async () => {
    const { request } = fakeRequest({
      status: 200,
      body: {
        five_hour: null,
        seven_day: null,
        seven_day_opus: { utilization: 250, resets_at: "not a date" },
        seven_day_sonnet: { utilization: "15" },
        limits: [
          { kind: "session", percent: 20 },
          { kind: "weekly_all", percent: 50, resets_at: "2027-02-01T00:00:00Z" },
        ],
        extra_usage: { is_enabled: true, used_credits: 500, monthly_limit: 2000, decimal_places: 2 },
      },
    });
    const report = await anthropicUsage(claudeOAuth, request, signal);
    assert.equal(report.status, "available");
    assert.deepEqual(
      report.limits.map((limit) => [limit.id, limit.window, limit.amount.usedFraction]),
      [
        ["5h", { label: "5 Hour" }, 0.2],
        ["7d", { label: "7 Day", resetsAt: Date.parse("2027-02-01T00:00:00Z") }, 0.5],
        ["7d:opus", { label: "7 Day" }, 1],
        ["7d:sonnet", { label: "7 Day" }, 0.15],
        ["extra", { label: "Monthly" }, 0.25],
      ],
    );
  });

  it("drops extra usage that is disabled, uncapped, non-USD, or malformed", async () => {
    const bodies = [
      { five_hour: { utilization: 1 }, spend: { enabled: false, used: { amount_minor: 1, exponent: 2, currency: "USD" }, limit: null } },
      { five_hour: { utilization: 1 }, spend: { enabled: true, used: { amount_minor: 1, exponent: 2, currency: "USD" }, limit: null } },
      { five_hour: { utilization: 1 }, spend: { enabled: true, used: { amount_minor: 1, exponent: 2, currency: "EUR" }, limit: { amount_minor: 100, exponent: 2, currency: "EUR" } } },
      { five_hour: { utilization: 1 }, spend: { enabled: true, used: { amount_minor: 1, exponent: 2, currency: "USD" }, limit: { amount_minor: 0, exponent: 2, currency: "USD" } } },
      { five_hour: { utilization: 1 }, extra_usage: { is_enabled: true, used_credits: 5, monthly_limit: null } },
      { five_hour: { utilization: 1 }, extra_usage: { is_enabled: true, used_credits: "5", monthly_limit: 10 } },
    ];
    for (const body of bodies) {
      const { request } = fakeRequest({ status: 200, body });
      const report = await anthropicUsage(claudeOAuth, request, signal);
      assert.deepEqual(report.limits.map((limit) => limit.id), ["5h"], JSON.stringify(body));
    }
  });

  it("treats API-key credentials as having no subscription usage without calling the network", async () => {
    const { request, calls } = fakeRequest({ status: 200, body: claudePayload });
    const report = await anthropicUsage(claudeApiKey, request, signal);
    assert.equal(calls.length, 0);
    assert.deepEqual([report.status, report.diagnostic, report.limits], ["unavailable", "no_credential", []]);
    assertSecretFree(report);
  });

  it("maps unauthorized and rate-limited responses to stable diagnostics", async () => {
    const cases: Array<[number, UsageReport["status"], string]> = [
      [401, "unavailable", "unauthorized"],
      [403, "unavailable", "unauthorized"],
      [429, "unavailable", "rate_limited"],
      [503, "error", "http_503"],
    ];
    for (const [status, expectedStatus, diagnostic] of cases) {
      const { request } = fakeRequest({
        status,
        body: { type: "error", error: { type: "authentication_error", message: `bad token ${CLAUDE_TOKEN}` } },
      });
      const report = await anthropicUsage(claudeOAuth, request, signal);
      assert.deepEqual([report.status, report.diagnostic, report.limits], [expectedStatus, diagnostic, []]);
      assertSecretFree(report);
    }
  });

  it("flags malformed bodies, forwards request failures, and reports no_quota for empty payloads", async () => {
    const malformed = await anthropicUsage(claudeOAuth, fakeRequest({ status: 200, body: "usage: 5" }).request, signal);
    assert.deepEqual([malformed.status, malformed.diagnostic], ["error", "response_malformed"]);

    const timedOut = await anthropicUsage(claudeOAuth, fakeRequest(new Error("request_timeout")).request, signal);
    assert.deepEqual([timedOut.status, timedOut.diagnostic], ["error", "request_timeout"]);

    const crashed = await anthropicUsage(claudeOAuth, fakeRequest(new Error(`boom ${EMAIL}`)).request, signal);
    assert.deepEqual([crashed.status, crashed.diagnostic], ["error", "request_failed"]);
    assertSecretFree(crashed);

    for (const body of [{}, { five_hour: null, seven_day: { resets_at: "2027-01-01T00:00:00Z" }, limits: [] }]) {
      const report = await anthropicUsage(claudeOAuth, fakeRequest({ status: 200, body }).request, signal);
      assert.deepEqual([report.status, report.diagnostic, report.limits], ["unavailable", "no_quota", []]);
    }
  });
});
