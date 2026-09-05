/**
 * Kimi Code and xAI (SuperGrok) usage collectors: fixture payloads exercised
 * through an injected `UsageRequest`, asserting bounded shapes, normalized
 * fractions/resets, stable diagnostics, and secret-free serialization.
 */
import assert from "node:assert/strict";
import { describe, it } from "node:test";
import type { AuthResult, UsageReport, UsageRequest } from "../src/usageTypes.ts";
import { kimiCodeUsage, xaiUsage } from "../src/usageProviders/kimiXai.ts";

const KIMI_TOKEN = "kimi-secret-access-token-0123456789";
const XAI_TOKEN = "xai-secret-access-token-0123456789";

const kimiOauth: AuthResult = {
  auth: { headers: { Authorization: `Bearer ${KIMI_TOKEN}` } },
  source: "OAuth",
};
const xaiOauth: AuthResult = { auth: { apiKey: XAI_TOKEN }, source: "OAuth" };

const KIMI_URL = "https://api.kimi.com/coding/v1/usages";
const XAI_CREDITS_URL = "https://cli-chat-proxy.grok.com/v1/billing?format=credits";
const XAI_MONTHLY_URL = "https://cli-chat-proxy.grok.com/v1/billing";

interface Call {
  url: string;
  init: RequestInit;
}

type Route = { status: number; body: unknown } | { throws: string };

/** Fake request: answers per URL from `routes` and records every call. */
function fakeRequest(routes: Record<string, Route>): { request: UsageRequest; calls: Call[] } {
  const calls: Call[] = [];
  const request: UsageRequest = async (url, init) => {
    calls.push({ url, init });
    const route = routes[url];
    if (!route) throw new Error(`unexpected url ${url}`);
    if ("throws" in route) throw new Error(route.throws);
    return { status: route.status, body: route.body };
  };
  return { request, calls };
}

const signal = new AbortController().signal;

const FAR_FUTURE = "2999-01-08T00:00:00.000Z";
const FAR_FUTURE_MS = Date.parse(FAR_FUTURE);

function assertSecretFree(report: UsageReport, ...secrets: string[]): void {
  const serialized = JSON.stringify(report);
  for (const secret of secrets) assert.ok(!serialized.includes(secret), `serialized report leaks ${secret}`);
  assert.ok(!serialized.includes("Bearer"));
  assert.ok(!/@/.test(serialized), "serialized report contains an e-mail-like value");
}

function assertBoundedReport(report: UsageReport): void {
  assert.ok(report.limits.length <= 8);
  for (const limit of report.limits) {
    assert.deepEqual(Object.keys(limit).sort(), ["amount", "id", "label", "window"]);
    assert.ok(limit.id.length > 0 && limit.label.length > 0);
    if (limit.amount.usedFraction !== undefined) {
      assert.ok(limit.amount.usedFraction >= 0 && limit.amount.usedFraction <= 1);
      assert.ok(limit.amount.remainingFraction !== undefined);
      assert.ok(Math.abs(limit.amount.usedFraction + limit.amount.remainingFraction - 1) < 1e-9);
    }
    if (limit.window.resetsAt !== undefined) assert.ok(limit.window.resetsAt > 1_000_000_000_000);
  }
  if (report.diagnostic !== undefined) assert.match(report.diagnostic, /^[a-z0-9_]{1,40}$/);
}

describe("kimiCodeUsage", () => {
  const kimiPayload = {
    usage: { limit: 1000, used: 250, resetTime: FAR_FUTURE },
    limits: [
      {
        window: { duration: 300, timeUnit: "TIME_UNIT_MINUTE" },
        detail: { limit: 100, used: 90, resetTime: FAR_FUTURE },
      },
      {
        name: "Daily bonus",
        window: { duration: 1, timeUnit: "TIME_UNIT_DAY" },
        detail: { limit: 50, remaining: 10, reset_in: 3600 },
      },
    ],
  };

  it("sends only the bearer token to the exact usages endpoint", async () => {
    const { request, calls } = fakeRequest({ [KIMI_URL]: { status: 200, body: kimiPayload } });
    await kimiCodeUsage(kimiOauth, request, signal);
    assert.equal(calls.length, 1);
    assert.equal(calls[0]?.url, KIMI_URL);
    assert.equal(calls[0]?.init.method, "GET");
    const headers = calls[0]?.init.headers as Record<string, string>;
    assert.equal(headers["Authorization"], `Bearer ${KIMI_TOKEN}`);
    assert.equal(headers["X-Msh-Platform"], "kimi_cli");
  });

  it("normalizes the weekly summary and canonical limit windows", async () => {
    const before = Date.now();
    const { request } = fakeRequest({ [KIMI_URL]: { status: 200, body: kimiPayload } });
    const report = await kimiCodeUsage(kimiOauth, request, signal);
    assertBoundedReport(report);
    assertSecretFree(report, KIMI_TOKEN);

    assert.equal(report.provider, "kimi-code");
    assert.equal(report.status, "available");
    assert.equal(report.diagnostic, undefined);
    assert.deepEqual(
      report.limits.map((limit) => [limit.id, limit.label, limit.window.label]),
      [
        ["7d", "Total quota", "7 Day"],
        ["5h", "5h limit", "5h limit"],
        ["1d", "Daily bonus", "1d limit"],
      ],
    );

    const [summary, burst, daily] = report.limits;
    assert.deepEqual(summary?.amount, { usedFraction: 0.25, remainingFraction: 0.75, unit: "unknown" });
    assert.equal(summary?.window.resetsAt, FAR_FUTURE_MS);
    assert.equal(burst?.amount.usedFraction, 0.9);
    assert.equal(burst?.amount.unit, "unknown");
    assert.equal(burst?.window.resetsAt, FAR_FUTURE_MS);
    // remaining=10 of 50 → used 40; reset_in is relative to now.
    assert.equal(daily?.amount.usedFraction, 0.8);
    assert.ok(daily?.window.resetsAt !== undefined);
    assert.ok(daily.window.resetsAt >= before + 3_600_000 && daily.window.resetsAt <= Date.now() + 3_600_000);
  });

  it("clamps over-consumed windows to a full fraction and skips rows without a limit", async () => {
    const { request } = fakeRequest({
      [KIMI_URL]: {
        status: 200,
        body: {
          usage: { limit: 100, used: 150 },
          limits: [{ detail: { used: 3 } }, { detail: { limit: 0, used: 0 } }],
        },
      },
    });
    const report = await kimiCodeUsage(kimiOauth, request, signal);
    assertBoundedReport(report);
    assert.equal(report.status, "available");
    assert.equal(report.limits.length, 1);
    assert.deepEqual(report.limits[0]?.amount, { usedFraction: 1, remainingFraction: 0, unit: "unknown" });
    assert.deepEqual(report.limits[0]?.window, { label: "7 Day" }, "no reset key when none was reported");
  });

  it("bounds server-supplied labels and never emits more than eight windows", async () => {
    const limits = Array.from({ length: 12 }, (_, index) => ({
      name: `${"x".repeat(200)}\u0007${index}`,
      detail: { limit: 10, used: index },
    }));
    const { request } = fakeRequest({ [KIMI_URL]: { status: 200, body: { limits } } });
    const report = await kimiCodeUsage(kimiOauth, request, signal);
    assertBoundedReport(report);
    assert.equal(report.limits.length, 8);
    assert.equal(report.limits[0]?.label.length, 64);
    assert.ok(!report.limits[0]?.label.includes("\u0007"));
    assert.equal(new Set(report.limits.map((limit) => limit.id)).size, 8, "ids stay unique");
  });

  it("reports no_credential for non-OAuth credentials without touching the network", async () => {
    const { request, calls } = fakeRequest({});
    const report = await kimiCodeUsage({ auth: { apiKey: KIMI_TOKEN }, source: "KIMI_API_KEY" }, request, signal);
    assert.equal(calls.length, 0);
    assert.equal(report.status, "unavailable");
    assert.equal(report.diagnostic, "no_credential");
    assert.deepEqual(report.limits, []);
    assertSecretFree(report, KIMI_TOKEN);
  });

  it("maps unauthorized and rate-limited responses to stable unavailable diagnostics", async () => {
    for (const [status, diagnostic] of [
      [401, "unauthorized"],
      [403, "unauthorized"],
      [429, "rate_limited"],
    ] as const) {
      const { request } = fakeRequest({
        [KIMI_URL]: { status, body: { error: `token ${KIMI_TOKEN} rejected`, email: "user@example.com" } },
      });
      const report = await kimiCodeUsage(kimiOauth, request, signal);
      assert.equal(report.status, "unavailable");
      assert.equal(report.diagnostic, diagnostic);
      assert.deepEqual(report.limits, []);
      assertSecretFree(report, KIMI_TOKEN);
    }
  });

  it("reports other HTTP failures, malformed bodies, and empty quota distinctly", async () => {
    const cases: Array<[Route, UsageReport["status"], string]> = [
      [{ status: 503, body: "upstream down" }, "error", "http_503"],
      [{ status: 200, body: "<html>not json</html>" }, "error", "response_malformed"],
      [{ status: 200, body: [1, 2, 3] }, "error", "response_malformed"],
      [{ status: 200, body: { usage: "nope", limits: "nope" } }, "unavailable", "no_quota"],
      [{ status: 200, body: {} }, "unavailable", "no_quota"],
    ];
    for (const [route, status, diagnostic] of cases) {
      const { request } = fakeRequest({ [KIMI_URL]: route });
      const report = await kimiCodeUsage(kimiOauth, request, signal);
      assertBoundedReport(report);
      assert.equal(report.status, status, diagnostic);
      assert.equal(report.diagnostic, diagnostic);
      assert.deepEqual(report.limits, []);
    }
  });

  it("forwards the request layer's stable failure tokens and hides everything else", async () => {
    for (const thrown of ["request_timeout", "response_too_large", "redirect_refused", "origin_not_allowed"]) {
      const { request } = fakeRequest({ [KIMI_URL]: { throws: thrown } });
      const report = await kimiCodeUsage(kimiOauth, request, signal);
      assert.equal(report.status, "error");
      assert.equal(report.diagnostic, thrown);
    }
    const { request } = fakeRequest({ [KIMI_URL]: { throws: `ECONNRESET talking to ${KIMI_TOKEN}` } });
    const report = await kimiCodeUsage(kimiOauth, request, signal);
    assert.equal(report.status, "error");
    assert.equal(report.diagnostic, "request_failed");
    assertSecretFree(report, KIMI_TOKEN);
  });
});

describe("xaiUsage", () => {
  const weeklyCredits = {
    config: {
      currentPeriod: { start: "2998-12-25T00:00:00.000Z", end: FAR_FUTURE, type: "PERIOD_TYPE_WEEKLY" },
      creditUsagePercent: 42.5,
      productUsage: [
        { product: "GrokBuild", usagePercent: 10 },
        { product: "Api" },
        { product: "Grok Voice!", usagePercent: 100 },
      ],
      onDemandCap: { val: 20 },
      onDemandUsed: { val: 5 },
    },
  };

  it("sends the OAuth token with the Grok CLI headers to the credits endpoint first", async () => {
    const { request, calls } = fakeRequest({ [XAI_CREDITS_URL]: { status: 200, body: weeklyCredits } });
    await xaiUsage(xaiOauth, request, signal);
    assert.equal(calls.length, 1, "weekly-only accounts never probe the monthly endpoint");
    assert.equal(calls[0]?.url, XAI_CREDITS_URL);
    const headers = calls[0]?.init.headers as Record<string, string>;
    assert.equal(headers["Authorization"], `Bearer ${XAI_TOKEN}`);
    assert.equal(headers["X-XAI-Token-Auth"], "xai-grok-cli");
    assert.equal(headers["Accept"], "application/json");
  });

  it("normalizes weekly credits, per-product percentages, and on-demand spend", async () => {
    const { request } = fakeRequest({ [XAI_CREDITS_URL]: { status: 200, body: weeklyCredits } });
    const report = await xaiUsage(xaiOauth, request, signal);
    assertBoundedReport(report);
    assertSecretFree(report, XAI_TOKEN);

    assert.equal(report.provider, "xai-oauth");
    assert.equal(report.status, "available");
    assert.deepEqual(
      report.limits.map((limit) => [limit.id, limit.label]),
      [
        ["credits_1w", "SuperGrok Weekly Credits"],
        ["product_grokbuild_1w", "Grok Build (Weekly)"],
        ["product_api_1w", "API (Weekly)"],
        ["product_grok_voice_1w", "Grok Voice! (Weekly)"],
        ["on_demand", "On-demand"],
      ],
    );
    const [credits, build, api, voice, onDemand] = report.limits;
    assert.deepEqual(credits?.window, { label: "Weekly", resetsAt: FAR_FUTURE_MS });
    assert.deepEqual(credits?.amount, { usedFraction: 0.425, remainingFraction: 0.575, unit: "percent" });
    assert.equal(build?.amount.usedFraction, 0.1);
    assert.equal(api?.amount.usedFraction, 0, "omitted product percent defaults to zero");
    assert.deepEqual(voice?.amount, { usedFraction: 1, remainingFraction: 0, unit: "percent" });
    assert.deepEqual(onDemand?.window, {});
    assert.deepEqual(onDemand?.amount, { usedFraction: 0.25, remainingFraction: 0.75, unit: "unknown" });
  });

  it("falls back to the unified monthly quota when credits omit usage", async () => {
    const unifiedCredits = {
      config: {
        isUnifiedBillingUser: true,
        currentPeriod: { start: "2998-12-25T00:00:00.000Z", end: FAR_FUTURE, type: "PERIOD_TYPE_WEEKLY" },
      },
    };
    const monthly = {
      config: {
        billingPeriodStart: "2998-12-09T00:00:00.000Z",
        billingPeriodEnd: FAR_FUTURE,
        monthlyLimit: { val: 300 },
        used: { val: 75 },
        onDemandCap: { val: 100 },
        onDemandUsed: { val: 150 },
      },
    };
    const { request, calls } = fakeRequest({
      [XAI_CREDITS_URL]: { status: 200, body: unifiedCredits },
      [XAI_MONTHLY_URL]: { status: 200, body: monthly },
    });
    const report = await xaiUsage(xaiOauth, request, signal);
    assertBoundedReport(report);
    assertSecretFree(report, XAI_TOKEN);
    assert.deepEqual(
      calls.map((call) => call.url),
      [XAI_CREDITS_URL, XAI_MONTHLY_URL],
    );
    assert.equal(report.status, "available");
    assert.deepEqual(
      report.limits.map((limit) => limit.id),
      ["included_1mo", "on_demand"],
      "inferred weekly is replaced by the positive monthly quota",
    );
    assert.deepEqual(report.limits[0]?.window, { label: "Monthly", resetsAt: FAR_FUTURE_MS });
    assert.deepEqual(report.limits[0]?.amount, { usedFraction: 0.25, remainingFraction: 0.75, unit: "unknown" });
    assert.deepEqual(report.limits[1]?.amount, { usedFraction: 1, remainingFraction: 0, unit: "unknown" });
  });

  it("keeps inferred weekly credits when the monthly payload confirms no monthly quota", async () => {
    const unifiedCredits = {
      config: {
        isUnifiedBillingUser: true,
        currentPeriod: { start: "2998-12-25T00:00:00.000Z", end: FAR_FUTURE, type: "PERIOD_TYPE_WEEKLY" },
      },
    };
    const { request } = fakeRequest({
      [XAI_CREDITS_URL]: { status: 200, body: unifiedCredits },
      [XAI_MONTHLY_URL]: { status: 200, body: { config: { monthlyLimit: { val: 0 } } } },
    });
    const report = await xaiUsage(xaiOauth, request, signal);
    assert.equal(report.status, "available");
    assert.deepEqual(report.limits.map((limit) => limit.id), ["credits_1w"]);
    assert.equal(report.limits[0]?.amount.usedFraction, 0);
  });

  it("reports no_quota when neither shape yields a usable window", async () => {
    const { request, calls } = fakeRequest({
      [XAI_CREDITS_URL]: {
        status: 200,
        body: { config: { currentPeriod: { start: FAR_FUTURE, end: "2998-12-25T00:00:00.000Z", type: "PERIOD_TYPE_WEEKLY" } } },
      },
      [XAI_MONTHLY_URL]: { status: 200, body: { config: { monthlyLimit: { val: 0 }, used: { val: 0 } } } },
    });
    const report = await xaiUsage(xaiOauth, request, signal);
    assertBoundedReport(report);
    assert.equal(calls.length, 2);
    assert.equal(report.status, "unavailable");
    assert.equal(report.diagnostic, "no_quota");
    assert.deepEqual(report.limits, []);
  });

  it("rejects an expired weekly period that omits its usage percent", async () => {
    const { request } = fakeRequest({
      [XAI_CREDITS_URL]: {
        status: 200,
        body: {
          config: {
            currentPeriod: { start: "2000-01-01T00:00:00.000Z", end: "2000-01-08T00:00:00.000Z", type: "PERIOD_TYPE_WEEKLY" },
          },
        },
      },
      [XAI_MONTHLY_URL]: { status: 200, body: { config: {} } },
    });
    const report = await xaiUsage(xaiOauth, request, signal);
    assert.equal(report.status, "unavailable");
    assert.equal(report.diagnostic, "no_quota");
  });

  it("reports malformed bodies from both endpoints as response_malformed", async () => {
    const { request } = fakeRequest({
      [XAI_CREDITS_URL]: { status: 200, body: "<html>login</html>" },
      [XAI_MONTHLY_URL]: { status: 200, body: { unexpected: true } },
    });
    const report = await xaiUsage(xaiOauth, request, signal);
    assert.equal(report.status, "error");
    assert.equal(report.diagnostic, "response_malformed");
    assert.deepEqual(report.limits, []);
  });

  it("maps unauthorized and rate-limited credits responses even when monthly also fails", async () => {
    for (const [status, diagnostic] of [
      [401, "unauthorized"],
      [403, "unauthorized"],
      [429, "rate_limited"],
    ] as const) {
      const { request, calls } = fakeRequest({
        [XAI_CREDITS_URL]: { status, body: { error: `bad token ${XAI_TOKEN}`, email: "user@example.com" } },
        [XAI_MONTHLY_URL]: { status: 500, body: "boom" },
      });
      const report = await xaiUsage(xaiOauth, request, signal);
      assert.equal(calls.length, 2);
      assert.equal(report.status, "unavailable");
      assert.equal(report.diagnostic, diagnostic);
      assert.deepEqual(report.limits, []);
      assertSecretFree(report, XAI_TOKEN);
    }
  });

  it("reports other HTTP failures and request-layer errors with stable tokens", async () => {
    const http = fakeRequest({
      [XAI_CREDITS_URL]: { status: 502, body: "" },
      [XAI_MONTHLY_URL]: { status: 502, body: "" },
    });
    const httpReport = await xaiUsage(xaiOauth, http.request, signal);
    assert.equal(httpReport.status, "error");
    assert.equal(httpReport.diagnostic, "http_502");

    const timeout = fakeRequest({
      [XAI_CREDITS_URL]: { throws: "request_timeout" },
      [XAI_MONTHLY_URL]: { throws: "request_timeout" },
    });
    const timeoutReport = await xaiUsage(xaiOauth, timeout.request, signal);
    assert.equal(timeoutReport.status, "error");
    assert.equal(timeoutReport.diagnostic, "request_timeout");

    const opaque = fakeRequest({
      [XAI_CREDITS_URL]: { throws: `TypeError: fetch failed for ${XAI_TOKEN}` },
      [XAI_MONTHLY_URL]: { throws: `TypeError: fetch failed for ${XAI_TOKEN}` },
    });
    const opaqueReport = await xaiUsage(xaiOauth, opaque.request, signal);
    assert.equal(opaqueReport.status, "error");
    assert.equal(opaqueReport.diagnostic, "request_failed");
    assertSecretFree(opaqueReport, XAI_TOKEN);
  });

  it("never sends paid API keys to the billing host", async () => {
    const { request, calls } = fakeRequest({});
    const report = await xaiUsage({ auth: { apiKey: XAI_TOKEN }, source: "XAI_API_KEY" }, request, signal);
    assert.equal(calls.length, 0);
    assert.equal(report.status, "unavailable");
    assert.equal(report.diagnostic, "no_credential");
    assertSecretFree(report, XAI_TOKEN);
  });
});
