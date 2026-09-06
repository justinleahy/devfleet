/**
 * Usage sidecar contract: Pi provider ids map to emitted card ids,
 * unconfigured providers are omitted, every failure on a configured provider
 * is a stable snake_case diagnostic, the HTTP helper enforces its envelope,
 * and nothing secret or OMP-derived can reach stdout.
 */
import assert from "node:assert/strict";
import { execFile } from "node:child_process";
import { mkdtemp, readdir, readFile } from "node:fs/promises";
import { tmpdir } from "node:os";
import { join } from "node:path";
import { describe, it } from "node:test";
import { promisify } from "node:util";
import {
  DIAGNOSTIC_PATTERN,
  MAX_LIMITS,
  REQUEST_DEADLINE_MS,
  USAGE_PROVIDERS,
  collectUsage,
  type UsageAuthRuntime,
  type UsageProviderBinding,
} from "../src/usage.ts";
import {
  ALLOWED_ORIGINS,
  MAX_RESPONSE_BYTES,
  createUsageRequest,
  usageRequestFailure,
} from "../src/usageHttp.ts";
import type { AuthResult, UsageLimit, UsageReport, UsageRequest } from "../src/usageTypes.ts";

const SRC_DIR = join(import.meta.dirname, "..", "src");
const SECRET = "sk-live-0123456789abcdef";
const OAUTH_TOKEN = "eyJhbGciOiJSUzI1NiJ9.oauth-access-token";

const authOf = (piProvider: string): AuthResult => ({
  auth: { apiKey: `${SECRET}-${piProvider}`, headers: { Authorization: `Bearer ${OAUTH_TOKEN}` } },
  source: "OAuth",
});

/** Runtime where `configured` providers have credentials and everything else is unconfigured. */
function fakeRuntime(
  configured: readonly string[],
  overrides: Partial<UsageAuthRuntime> = {},
): UsageAuthRuntime {
  return {
    checkAuth: async (providerId) =>
      configured.includes(providerId) ? { type: "oauth", source: "OAuth" } : undefined,
    getAuth: async (providerId) =>
      configured.includes(providerId as string) ? authOf(providerId as string) : undefined,
    getProvider: (providerId) => ({
      baseUrl: providerId === "zai" ? "https://api.z.ai/api/coding/paas/v4" : undefined,
    }),
    ...overrides,
  } as UsageAuthRuntime;
}

const noRequest: UsageRequest = async () => {
  throw new Error("request_failed");
};

function limit(id: string): UsageLimit {
  return { id, label: id, window: { label: id }, amount: { usedFraction: 0.5, unit: "percent" } };
}

/** Bindings that mirror the production table but force the test collector, including static cards. */
function bindings(collect: NonNullable<UsageProviderBinding["collect"]>): UsageProviderBinding[] {
  return USAGE_PROVIDERS.map(({ unavailableDiagnostic: _, ...binding }) => ({ ...binding, collect }));
}

const available = (provider: string, limits: UsageLimit[] = [limit("5h")]): UsageReport => ({
  provider,
  fetchedAt: 1,
  limits,
  status: "available",
});

async function run(
  runtime: UsageAuthRuntime,
  providers: UsageProviderBinding[],
  signal = new AbortController().signal,
  request: UsageRequest = noRequest,
) {
  return collectUsage({ runtime, request, signal, providers, now: () => 1_700_000_000_000 });
}

describe("usage coordinator", () => {
  it("maps the nine Pi provider ids to the emitted card ids in table order", async () => {
    const piIds = USAGE_PROVIDERS.map((binding) => binding.piProvider);
    assert.deepEqual(piIds, [
      "openai-codex",
      "anthropic",
      "kimi-coding",
      "zai",
      "xai",
      "opencode-go",
      "qwen-token-plan",
      "qwen-token-plan-individual",
      "qwen-token-plan-cn",
    ]);

    const seen: string[] = [];
    const output = await run(
      fakeRuntime(piIds),
      bindings(async (auth) => {
        seen.push(auth.auth.apiKey ?? "");
        return available("wrong-id-from-collector");
      }),
    );
    assert.deepEqual(
      output.reports.map((report) => report.provider),
      [
        "openai-codex",
        "anthropic",
        "kimi-code",
        "zai",
        "xai-oauth",
        "opencode-go",
        "qwen-token-plan",
        "qwen-token-plan-individual",
        "qwen-token-plan-cn",
      ],
    );
    assert.deepEqual(
      seen,
      piIds.map((id) => `${SECRET}-${id}`),
      "each collector receives the credential of its own Pi provider",
    );
    for (const report of output.reports) {
      assert.equal(report.status, "available");
      assert.equal(report.fetchedAt, 1_700_000_000_000);
      assert.equal("diagnostic" in report, false);
    }
  });

  it("emits unavailable Qwen Token Plan cards only for configured native Pi auth", async () => {
    let requestCalls = 0;
    let getAuthCalls = 0;
    const request: UsageRequest = async () => {
      requestCalls += 1;
      throw new Error("request_failed");
    };
    const runtime = fakeRuntime(["qwen-token-plan"], {
      getAuth: async () => {
        getAuthCalls += 1;
        throw new Error("getAuth must not run for Qwen Token Plan");
      },
    });
    const output = await run(runtime, [...USAGE_PROVIDERS], new AbortController().signal, request);
    assert.equal(requestCalls, 0);
    assert.equal(getAuthCalls, 0);
    assert.deepEqual(
      output.reports.map((report) => report.provider),
      ["qwen-token-plan"],
    );
    const [qwen] = output.reports;
    assert.ok(qwen);
    assert.equal(qwen.status, "unavailable");
    assert.equal(qwen.diagnostic, "quota_console_only");
    assert.deepEqual(qwen.limits, []);
    const serialized = JSON.stringify(output);
    assert.equal(serialized.includes(SECRET), false);
    assert.equal(serialized.includes(`${SECRET}-qwen-token-plan`), false);
  });

  it("omits providers Pi has no credential for without calling their collector", async () => {
    let calls = 0;
    const output = await run(
      fakeRuntime(["anthropic", "zai"]),
      bindings(async () => {
        calls += 1;
        return available("x");
      }),
    );
    assert.deepEqual(
      output.reports.map((report) => report.provider),
      ["anthropic", "zai"],
    );
    assert.equal(calls, 2);
  });

  it("never resolves or sends API keys when a fixed-origin provider endpoint is overridden", async () => {
    let authCalls = 0;
    let collectorCalls = 0;
    const guardedProviders = bindings(async () => {
      collectorCalls += 1;
      return available("unexpected");
    }).filter(({ piProvider }) => piProvider === "zai" || piProvider === "opencode-go");
    const runtime = fakeRuntime(["zai", "opencode-go"], {
      getProvider: (providerId) => ({
        baseUrl:
          providerId === "zai"
            ? "https://gateway.example/zai"
            : "https://gateway.example/opencode",
      }),
      getAuth: async () => {
        authCalls += 1;
        return authOf("unexpected");
      },
    });

    const output = await run(runtime, guardedProviders);

    assert.equal(authCalls, 0);
    assert.equal(collectorCalls, 0);
    assert.deepEqual(
      output.reports.map(({ provider, status, diagnostic }) => [provider, status, diagnostic]),
      [
        ["zai", "unavailable", "provider_endpoint_overridden"],
        ["opencode-go", "unavailable", "provider_endpoint_overridden"],
      ],
    );
  });

  it("emits stable error reports for configured providers whose auth or collector fails", async () => {
    const runtime = fakeRuntime(["openai-codex", "anthropic", "kimi-coding", "zai"], {
      checkAuth: async (providerId) => {
        if (providerId === "openai-codex") throw new Error(`boom ${SECRET}`);
        return providerId === "xai"
          || providerId === "opencode-go"
          || providerId.startsWith("qwen-token-plan")
          ? undefined
          : { type: "oauth", source: "OAuth" };
      },
      getAuth: async (providerId) => {
        if (providerId === "anthropic") throw new Error(`refresh failed: ${OAUTH_TOKEN}`);
        if (providerId === "kimi-coding") return undefined;
        return authOf(providerId as string);
      },
    });
    const output = await run(
      runtime,
      bindings(async () => {
        throw new Error(`collector exploded with ${SECRET}`);
      }),
    );
    assert.deepEqual(
      output.reports.map((report) => [report.provider, report.status, report.diagnostic]),
      [
        ["openai-codex", "error", "auth_check_failed"],
        ["anthropic", "error", "auth_refresh_failed"],
        ["kimi-code", "error", "auth_unresolved"],
        ["zai", "error", "collector_failed"],
      ],
    );
    for (const report of output.reports) assert.deepEqual(report.limits, []);
    const serialized = JSON.stringify(output);
    assert.equal(serialized.includes(SECRET), false);
    assert.equal(serialized.includes(OAUTH_TOKEN), false);
    assert.equal(serialized.includes("boom"), false);
  });

  it("reports request_timeout once the shared deadline has passed", async () => {
    const controller = new AbortController();
    const output = await run(
      fakeRuntime(["zai"]),
      bindings(async (_auth, _request, signal) => {
        controller.abort();
        signal.throwIfAborted();
        return available("zai");
      }),
      controller.signal,
    );
    assert.deepEqual(
      output.reports.map((report) => [report.provider, report.status, report.diagnostic]),
      [["zai", "error", "request_timeout"]],
    );
  });

  it("keeps the sidecar deadline below the host's fifteen-second process timeout", () => {
    assert.equal(REQUEST_DEADLINE_MS, 8_000);
  });

  it("normalizes every stdout-bound label to the host's printable ASCII contract", async () => {
    const output = await run(
      fakeRuntime(["zai"]),
      bindings(async () =>
        available("zai", [
          {
            ...limit("long"),
            label: `  ${"L".repeat(64)}  `,
            window: { label: ` ${"W".repeat(41)} ` },
          },
          {
            ...limit("mixed"),
            label: "  plan\u0000-\u2603-alpha  ",
            window: { label: " \t5h\u007f " },
          },
          { ...limit("empty"), label: "\u0000\u2603" },
          { ...limit("empty-window"), window: { label: "\u0000\u2603" } },
        ]),
      ),
    );

    const limits = output.reports[0]?.limits;
    assert.ok(limits);
    assert.deepEqual(
      limits.map(({ label, window }) => [label, window.label]),
      [
        ["L".repeat(40), "W".repeat(40)],
        ["plan--alpha", "5h"],
        ["empty-window", undefined],
      ],
    );
    for (const { label, window } of limits) {
      assert.match(label, /^[\x20-\x7e]{1,40}$/);
      assert.equal(label, label.trim());
      if (window.label !== undefined) {
        assert.match(window.label, /^[\x20-\x7e]{1,40}$/);
        assert.equal(window.label, window.label.trim());
      }
    }
  });

  it("caps limits, strips off-schema fields, and forces diagnostics into the token vocabulary", async () => {
    const limits = Array.from({ length: MAX_LIMITS + 3 }, (_, index) => limit(`w${index}`));
    limits[0] = {
      id: "5h",
      label: "5 hour",
      window: { label: "5h", resetsAt: Number.NaN },
      amount: { usedFraction: Number.POSITIVE_INFINITY, remainingFraction: 0.25, unit: "percent" },
    };
    const malformed = { id: 42, label: "bad" } as unknown as UsageLimit;
    limits[1] = malformed;

    const output = await run(
      fakeRuntime(["openai-codex", "anthropic", "zai", "kimi-coding"]),
      bindings(async (auth) => {
        switch (auth.auth.apiKey) {
          case `${SECRET}-openai-codex`:
            return { ...available("x", limits), diagnostic: "should_be_dropped" };
          case `${SECRET}-anthropic`:
            return {
              provider: "x",
              fetchedAt: Number.NaN,
              limits: [],
              status: "unavailable",
              diagnostic: "HTTP 403 Forbidden: token abc",
            };
          case `${SECRET}-zai`:
            return { provider: "x", fetchedAt: 5, limits: [], status: "error" };
          default:
            return { provider: "x", fetchedAt: 5, limits: [], status: "bogus" as "error" };
        }
      }),
    );
    const [codex, anthropic, zai, kimi] = output.reports;
    assert.ok(codex && anthropic && zai && kimi);

    assert.equal(codex.limits.length, MAX_LIMITS - 1, "cap applies before dropping malformed rows");
    assert.deepEqual(codex.limits[0], {
      id: "5h",
      label: "5 hour",
      window: { label: "5h" },
      amount: { remainingFraction: 0.25, unit: "percent" },
    });
    assert.equal("diagnostic" in codex, false);

    assert.equal(anthropic.status, "unavailable");
    assert.equal(anthropic.diagnostic, "provider_unavailable");
    assert.equal(anthropic.fetchedAt, 1_700_000_000_000);

    assert.deepEqual([zai.status, zai.diagnostic], ["error", "provider_error"]);
    assert.deepEqual([kimi.status, kimi.diagnostic], ["error", "provider_error"]);
    assert.equal(kimi.fetchedAt, 1_700_000_000_000, "the coordinator clock stamps every report");

    for (const report of output.reports) {
      if (report.diagnostic !== undefined) assert.match(report.diagnostic, DIAGNOSTIC_PATTERN);
    }
  });

  it("drops any report that echoes a credential into a string field", async () => {
    const output = await run(
      fakeRuntime(["zai", "opencode-go"]),
      bindings(async (auth) => {
        const leakedLimit = { ...limit("5h"), label: `key ${auth.auth.apiKey}` };
        return auth.auth.apiKey === `${SECRET}-zai`
          ? available("zai", [leakedLimit])
          : { ...available("opencode-go"), limits: [{ ...limit("5h"), id: OAUTH_TOKEN }] };
      }),
    );
    assert.deepEqual(
      output.reports.map((report) => [report.provider, report.status, report.diagnostic]),
      [
        ["zai", "error", "report_redacted"],
        ["opencode-go", "error", "report_redacted"],
      ],
    );
    const serialized = JSON.stringify(output);
    assert.equal(serialized.includes(SECRET), false);
    assert.equal(serialized.includes(OAUTH_TOKEN), false);
  });

  it("never imports OMP code: only Pi's coding-agent package and Node built-ins", async () => {
    const files = [
      ...["usage.ts", "usageHttp.ts", "usageTypes.ts"].map((name) => join(SRC_DIR, name)),
      ...(await readdir(join(SRC_DIR, "usageProviders"))).map((name) =>
        join(SRC_DIR, "usageProviders", name),
      ),
    ];
    assert.ok(files.length >= 6);
    for (const file of files) {
      const source = await readFile(file, "utf8");
      const specifiers = [...source.matchAll(/from\s+"([^"]+)"|import\s*\(\s*"([^"]+)"\s*\)/g)].map(
        (match) => match[1] ?? match[2] ?? "",
      );
      for (const specifier of specifiers) {
        const allowed =
          specifier.startsWith("./") ||
          specifier.startsWith("../") ||
          specifier.startsWith("node:") ||
          specifier === "@earendil-works/pi-coding-agent" ||
          /^typebox(\/|$)/.test(specifier);
        assert.ok(allowed, `${file} imports ${specifier}`);
      }
      assert.equal(/oh-my-pi|auth\.json/.test(source), false, `${file} references OMP or auth.json`);
    }
  });
});

describe("usage HTTP envelope", () => {
  const signal = new AbortController().signal;
  const ok = (body: string, headers: Record<string, string> = {}) =>
    new Response(body, { status: 200, headers: { "content-type": "application/json", ...headers } });

  it("allowlists exactly the six collector origins", () => {
    assert.deepEqual(Object.keys(ALLOWED_ORIGINS), [
      "https://chatgpt.com",
      "https://api.anthropic.com",
      "https://api.kimi.com",
      "https://cli-chat-proxy.grok.com",
      "https://api.z.ai",
      "https://opencode.ai",
    ]);
  });

  it("refuses non-HTTPS, unknown, and merely similar origins before any network call", async () => {
    let calls = 0;
    const request = createUsageRequest(async () => {
      calls += 1;
      return ok("{}");
    });
    for (const url of [
      "http://api.anthropic.com/api/oauth/usage",
      "https://api.anthropic.com.evil.example/usage",
      "https://api.anthropic.com:8443/usage",
      "https://evil.example/api.anthropic.com",
      "not a url",
    ]) {
      await assert.rejects(request(url, {}, signal), { message: "origin_not_allowed" });
    }
    assert.equal(calls, 0);
  });

  it("passes the allowlisted URL, manual redirect mode, and the deadline signal to fetch", async () => {
    let seen: { url: string; init: RequestInit } | undefined;
    const request = createUsageRequest(async (input, init) => {
      seen = { url: String(input), init: init ?? {} };
      return ok('{"used":1}');
    });
    const result = await request(
      "https://api.anthropic.com/api/oauth/usage",
      { headers: { Authorization: "Bearer x" } },
      signal,
    );
    assert.deepEqual(result, { status: 200, body: { used: 1 } });
    assert.equal(seen?.url, "https://api.anthropic.com/api/oauth/usage");
    assert.equal(seen?.init.redirect, "manual");
    assert.equal(seen?.init.signal, signal);
    assert.deepEqual(seen?.init.headers, { Authorization: "Bearer x" });
  });

  it("refuses redirects instead of following them", async () => {
    const request = createUsageRequest(
      async () => new Response(null, { status: 302, headers: { location: "https://evil.example" } }),
    );
    await assert.rejects(request("https://chatgpt.com/backend-api/wham/usage", {}, signal), {
      message: "redirect_refused",
    });
  });

  it("caps the body at 64KiB by declared length and by streamed bytes", async () => {
    const declared = createUsageRequest(async () =>
      ok("{}", { "content-length": String(MAX_RESPONSE_BYTES + 1) }),
    );
    await assert.rejects(declared("https://api.z.ai/api/monitor/usage/quota/limit", {}, signal), {
      message: "response_too_large",
    });

    const chunk = new Uint8Array(16 * 1024).fill(0x61);
    let pulled = 0;
    const stream = new ReadableStream<Uint8Array>({
      pull(controller) {
        pulled += 1;
        controller.enqueue(chunk);
      },
    });
    const streamed = createUsageRequest(async () => new Response(stream, { status: 200 }));
    await assert.rejects(streamed("https://api.kimi.com/coding/v1/usages", {}, signal), {
      message: "response_too_large",
    });
    assert.ok(pulled <= 6, `stopped reading shortly after the cap (pulled ${pulled} chunks)`);

    const exact = createUsageRequest(async () => ok("a".repeat(MAX_RESPONSE_BYTES)));
    const result = await exact("https://opencode.ai/zen/go/v1/usage", {}, signal);
    assert.equal(result.status, 200);
    assert.equal((result.body as string).length, MAX_RESPONSE_BYTES);
  });

  it("maps aborted deadlines to request_timeout and other transport errors to request_failed", async () => {
    const controller = new AbortController();
    const timedOut = createUsageRequest(async () => {
      controller.abort();
      throw new DOMException("aborted", "AbortError");
    });
    await assert.rejects(
      timedOut("https://api.anthropic.com/api/oauth/usage", {}, controller.signal),
      { message: "request_timeout" },
    );

    const failed = createUsageRequest(async () => {
      throw new TypeError(`fetch failed: connect ECONNREFUSED ${SECRET}`);
    });
    await assert.rejects(failed("https://api.anthropic.com/api/oauth/usage", {}, signal), {
      message: "request_failed",
    });

    const aborted = new AbortController();
    aborted.abort();
    await assert.rejects(
      createUsageRequest(async () => ok("{}"))(
        "https://cli-chat-proxy.grok.com/v1/billing",
        {},
        aborted.signal,
      ),
      { message: "request_timeout" },
    );
  });

  it("returns parsed JSON, raw text for non-JSON, and undefined for empty bodies", async () => {
    const json = createUsageRequest(async () => ok('{"a":[1]}'));
    const text = createUsageRequest(async () => new Response("<html>", { status: 502 }));
    const empty = createUsageRequest(async () => new Response(null, { status: 204 }));
    const url = "https://cli-chat-proxy.grok.com/v1/billing";
    assert.deepEqual(await json(url, {}, signal), { status: 200, body: { a: [1] } });
    assert.deepEqual(await text(url, {}, signal), { status: 502, body: "<html>" });
    assert.deepEqual(await empty(url, {}, signal), { status: 204, body: undefined });
  });

  it("classifies only its own failure tokens", () => {
    assert.equal(usageRequestFailure(new Error("response_too_large")), "response_too_large");
    assert.equal(usageRequestFailure(new Error(`ECONNRESET ${SECRET}`)), undefined);
    assert.equal(usageRequestFailure("request_timeout"), undefined);
  });
});

describe("usage entrypoint", () => {
  it("runs directly under Node against Pi with no credentials and prints an empty report set", async () => {
    const agentDir = await mkdtemp(join(tmpdir(), "pi-usage-test-"));
    const { stdout, stderr } = await promisify(execFile)(
      process.execPath,
      [join(SRC_DIR, "usage.ts")],
      {
        env: {
          ...process.env,
          PI_CODING_AGENT_DIR: agentDir,
          PI_OFFLINE: "1",
        },
        timeout: 30_000,
      },
    );
    assert.equal(stderr.trim(), "");
    assert.deepEqual(JSON.parse(stdout), { reports: [] });
  });
});
