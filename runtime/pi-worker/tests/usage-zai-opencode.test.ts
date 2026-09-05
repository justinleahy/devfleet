/**
 * Z.AI and OpenCode Go usage collectors: fixture responses in, secret-free
 * reports out. Every fixture drives the collector through a fake
 * `UsageRequest`, so no network, auth storage, or SDK is touched.
 */
import assert from "node:assert/strict";
import { describe, it } from "node:test";
import type { AuthResult, UsageReport, UsageRequest } from "../src/usageTypes.ts";
import { opencodeGoUsage, zaiUsage } from "../src/usageProviders/zaiOpenCode.ts";

const SECRET = "sk-live-super-secret-key.abc123";
const auth: AuthResult = { auth: { apiKey: SECRET }, source: "ZAI_API_KEY" };

interface RecordedCall {
  url: string;
  init: RequestInit;
}

/** Fake request that records the call and replays one canned response or failure. */
function fakeRequest(
  response: { status: number; body: unknown } | Error,
  calls: RecordedCall[] = [],
): UsageRequest {
  return (url, init) => {
    calls.push({ url, init });
    return response instanceof Error ? Promise.reject(response) : Promise.resolve(response);
  };
}

function assertSecretFree(report: UsageReport, ...upstreamText: string[]) {
  const serialized = JSON.stringify(report);
  assert.ok(!serialized.includes(SECRET), "report leaks the api key");
  for (const text of upstreamText) {
    assert.ok(!serialized.includes(text), `report leaks upstream text: ${text}`);
  }
  if (report.diagnostic !== undefined) {
    assert.match(report.diagnostic, /^[a-z0-9_]{1,40}$/);
  }
}

const zaiFixture = {
  success: true,
  code: 200,
  msg: "operation successful; account owner alice@example.com",
  data: {
    limits: [
      {
        type: "TOKENS_LIMIT",
        unit: 3,
        number: 5,
        percentage: 42,
        usage: 1000,
        currentValue: 420,
        remaining: 580,
        nextResetTime: 1_800_000_000,
      },
      {
        type: "TOKENS_LIMIT",
        unit: 6,
        number: 1,
        percentage: 130,
        nextResetTime: 1_800_400_000_000,
      },
      {
        type: "TIME_LIMIT",
        unit: 5,
        number: 1,
        percentage: 12.5,
        usageDetails: [{ modelCode: "search-prime" }, { modelCode: "web-reader" }, { modelCode: "zread" }],
      },
      { type: "TIME_LIMIT", unit: 4, number: 1, percentage: 0, usageDetails: [{ modelCode: "glm-5" }] },
      { type: "SOMETHING_ELSE", percentage: "not-a-number" },
    ],
  },
};

describe("zaiUsage", () => {
  it("renders 5h and weekly token quotas plus request quotas from a valid payload", async () => {
    const calls: RecordedCall[] = [];
    const report = await zaiUsage(auth, fakeRequest({ status: 200, body: zaiFixture }, calls), AbortSignal.timeout(1000));

    assert.equal(calls.length, 1);
    assert.equal(calls[0]!.url, "https://api.z.ai/api/monitor/usage/quota/limit");
    assert.equal(calls[0]!.init.method, "GET");
    // Z.AI takes the raw key as the Authorization value, never a bearer token.
    assert.deepEqual(calls[0]!.init.headers, { Authorization: SECRET, Accept: "application/json" });

    assert.equal(report.provider, "zai");
    assert.equal(report.status, "available");
    assert.equal(report.diagnostic, undefined);
    assert.deepEqual(report.limits, [
      {
        id: "zai:tokens:5h",
        label: "5 Hours token quota",
        window: { label: "5 Hours", resetsAt: 1_800_000_000_000 },
        amount: { usedFraction: 0.42, remainingFraction: 0.58, unit: "tokens" },
      },
      {
        id: "zai:tokens:1w",
        label: "Weekly token quota",
        window: { label: "Weekly", resetsAt: 1_800_400_000_000 },
        amount: { usedFraction: 1, remainingFraction: 0, unit: "tokens" },
      },
      {
        id: "zai:features:zread:1mo",
        label: "Zread quota",
        window: { label: "Monthly" },
        amount: { usedFraction: 0.125, remainingFraction: 0.875, unit: "requests" },
      },
      {
        id: "zai:requests:1d",
        label: "Request quota",
        window: { label: "1 Day" },
        amount: { usedFraction: 0, remainingFraction: 1, unit: "requests" },
      },
    ]);
    assertSecretFree(report, "alice@example.com", "operation successful", "currentValue");
  });

  it("rejects malformed payloads with a stable diagnostic", async () => {
    const malformed: unknown[] = [
      "<html>not json</html>",
      null,
      { success: false, code: 401, msg: "unauthorized" },
      { success: true },
      { success: true, data: { limits: {} } },
      { success: true, data: { limits: [{ type: "TOKENS_LIMIT", unit: 3 }] } },
      { success: true, data: { limits: [{ type: "TOKENS_LIMIT", percentage: -1 }] } },
      { success: true, data: { limits: [{ type: "TOKENS_LIMIT", percentage: "40" }] } },
      { success: true, data: { limits: [{ percentage: 40 }] } },
    ];
    for (const body of malformed) {
      const report = await zaiUsage(auth, fakeRequest({ status: 200, body }), new AbortController().signal);
      assert.equal(report.status, "error", JSON.stringify(body));
      assert.equal(report.diagnostic, "response_malformed", JSON.stringify(body));
      assert.deepEqual(report.limits, []);
      assertSecretFree(report, "unauthorized", "<html>");
    }
  });

  it("maps unauthorized and rate-limited responses to unavailable", async () => {
    const cases: Array<[number, UsageReport["status"], string]> = [
      [401, "unavailable", "unauthorized"],
      [403, "unavailable", "unauthorized"],
      [429, "unavailable", "rate_limited"],
      [500, "error", "http_500"],
      [302, "error", "http_302"],
    ];
    for (const [status, expectedStatus, diagnostic] of cases) {
      const body = { success: false, msg: `denied for key ${SECRET}` };
      const report = await zaiUsage(auth, fakeRequest({ status, body }), new AbortController().signal);
      assert.equal(report.status, expectedStatus, String(status));
      assert.equal(report.diagnostic, diagnostic);
      assert.deepEqual(report.limits, []);
      assertSecretFree(report, "denied");
    }
  });

  it("reports no_quota when the account exposes no limits", async () => {
    for (const body of [{ success: true, data: {} }, { success: true, data: { limits: [] } }]) {
      const report = await zaiUsage(auth, fakeRequest({ status: 200, body }), new AbortController().signal);
      assert.equal(report.status, "unavailable");
      assert.equal(report.diagnostic, "no_quota");
      assert.deepEqual(report.limits, []);
    }
    // Only limit kinds we do not render count as no quota too.
    const report = await zaiUsage(
      auth,
      fakeRequest({ status: 200, body: { success: true, data: { limits: [{ type: "OTHER" }] } } }),
      new AbortController().signal,
    );
    assert.equal(report.diagnostic, "no_quota");
  });

  it("does not call the network without an api key", async () => {
    const calls: RecordedCall[] = [];
    const report = await zaiUsage({ auth: {} }, fakeRequest({ status: 200, body: zaiFixture }, calls), new AbortController().signal);
    assert.equal(calls.length, 0);
    assert.equal(report.status, "unavailable");
    assert.equal(report.diagnostic, "no_credential");
  });

  it("forwards request diagnostics and hides arbitrary error text", async () => {
    const forwarded = await zaiUsage(auth, fakeRequest(new Error("response_too_large")), new AbortController().signal);
    assert.equal(forwarded.status, "error");
    assert.equal(forwarded.diagnostic, "response_too_large");

    const opaque = await zaiUsage(auth, fakeRequest(new Error(`ECONNRESET while sending ${SECRET}`)), new AbortController().signal);
    assert.equal(opaque.status, "error");
    assert.equal(opaque.diagnostic, "request_failed");
    assertSecretFree(opaque, "ECONNRESET");

    const aborted = await zaiUsage(auth, fakeRequest(new Error("The operation was aborted")), AbortSignal.abort());
    assert.equal(aborted.diagnostic, "request_timeout");
  });
});

const opencodeFixture = {
  usage: {
    rolling: { status: "ok", percent: 37, resetsAt: "2026-09-05T20:00:00.000Z" },
    weekly: { status: "rate-limited", percent: 99, resetsAt: "2026-09-08T00:00:00.000Z" },
    monthly: { status: "ok", percent: 0, resetsAt: "2026-10-01T00:00:00.000Z" },
  },
  account: { email: "bob@example.com" },
};

describe("opencodeGoUsage", () => {
  it("renders rolling 5h, weekly, and monthly windows from a valid payload", async () => {
    const calls: RecordedCall[] = [];
    const report = await opencodeGoUsage(auth, fakeRequest({ status: 200, body: opencodeFixture }, calls), AbortSignal.timeout(1000));

    assert.equal(calls.length, 1);
    assert.equal(calls[0]!.url, "https://opencode.ai/zen/go/v1/usage");
    assert.deepEqual(calls[0]!.init.headers, { Authorization: `Bearer ${SECRET}`, Accept: "application/json" });

    assert.equal(report.provider, "opencode-go");
    assert.equal(report.status, "available");
    assert.deepEqual(report.limits, [
      {
        id: "rolling-5h",
        label: "5 Hour limit",
        window: { label: "5 Hour", resetsAt: Date.parse("2026-09-05T20:00:00.000Z") },
        amount: { usedFraction: 0.37, remainingFraction: 0.63, unit: "percent" },
      },
      {
        // rate-limited means exhausted even though the floored percent reads 99.
        id: "weekly",
        label: "Weekly limit",
        window: { label: "Weekly", resetsAt: Date.parse("2026-09-08T00:00:00.000Z") },
        amount: { usedFraction: 1, remainingFraction: 0, unit: "percent" },
      },
      {
        id: "monthly",
        label: "Monthly limit",
        window: { label: "Monthly", resetsAt: Date.parse("2026-10-01T00:00:00.000Z") },
        amount: { usedFraction: 0, remainingFraction: 1, unit: "percent" },
      },
    ]);
    assertSecretFree(report, "bob@example.com");
  });

  it("rejects malformed or partial payloads all-or-nothing", async () => {
    const window = { status: "ok", percent: 10, resetsAt: "2026-09-05T20:00:00.000Z" };
    const malformed: unknown[] = [
      "rate limit exceeded",
      {},
      { usage: { rolling: window, weekly: window } },
      { usage: { rolling: window, weekly: window, monthly: { ...window, percent: 101 } } },
      { usage: { rolling: window, weekly: window, monthly: { ...window, percent: "10" } } },
      { usage: { rolling: window, weekly: window, monthly: { ...window, status: "exhausted" } } },
      { usage: { rolling: window, weekly: window, monthly: { ...window, resetsAt: "tomorrow" } } },
      { usage: { rolling: window, weekly: window, monthly: { ...window, resetsAt: 1_800_000_000 } } },
    ];
    for (const body of malformed) {
      const report = await opencodeGoUsage(auth, fakeRequest({ status: 200, body }), new AbortController().signal);
      assert.equal(report.status, "error", JSON.stringify(body));
      assert.equal(report.diagnostic, "response_malformed", JSON.stringify(body));
      assert.deepEqual(report.limits, []);
      assertSecretFree(report, "rate limit exceeded");
    }
  });

  it("maps unauthorized, forbidden, and rate-limited responses to unavailable", async () => {
    const cases: Array<[number, UsageReport["status"], string]> = [
      [401, "unavailable", "unauthorized"],
      [403, "unavailable", "unauthorized"],
      [429, "unavailable", "rate_limited"],
      [503, "error", "http_503"],
    ];
    for (const [status, expectedStatus, diagnostic] of cases) {
      const body = { error: { message: `Insufficient balance for ${SECRET}` } };
      const report = await opencodeGoUsage(auth, fakeRequest({ status, body }), new AbortController().signal);
      assert.equal(report.status, expectedStatus, String(status));
      assert.equal(report.diagnostic, diagnostic);
      assert.deepEqual(report.limits, []);
      assertSecretFree(report, "Insufficient balance");
    }
  });

  it("does not call the network without an api key", async () => {
    const calls: RecordedCall[] = [];
    const report = await opencodeGoUsage({ auth: { baseUrl: "https://opencode.ai/zen/go" } }, fakeRequest({ status: 200, body: opencodeFixture }, calls), new AbortController().signal);
    assert.equal(calls.length, 0);
    assert.equal(report.status, "unavailable");
    assert.equal(report.diagnostic, "no_credential");
  });

  it("forwards request diagnostics and hides arbitrary error text", async () => {
    const forwarded = await opencodeGoUsage(auth, fakeRequest(new Error("redirect_refused")), new AbortController().signal);
    assert.equal(forwarded.diagnostic, "redirect_refused");

    const opaque = await opencodeGoUsage(auth, fakeRequest(new Error(`TLS failure for ${SECRET}`)), new AbortController().signal);
    assert.equal(opaque.status, "error");
    assert.equal(opaque.diagnostic, "request_failed");
    assertSecretFree(opaque, "TLS failure");
  });
});
