/**
 * Model override resolution (SPEC.md section 25.1): a `provider/model`
 * override may only land on that provider, `provider/default` picks the first
 * authenticated model of that provider, and explicit ids stay exact.
 */
import assert from "node:assert/strict";
import { describe, it } from "node:test";
import { resolveConfiguredModel, type ModelCatalog } from "../src/sdk.ts";
import {
  mapModelsToCatalog,
  observeModelCatalog,
  type CatalogRuntime,
} from "../src/modelCatalog.ts";

interface FakeModel {
  provider: string;
  id: string;
}

/** Catalog of every known model plus the subset that is authenticated per provider. */
function fakeCatalog(catalog: FakeModel[], authenticated: FakeModel[]): ModelCatalog {
  return {
    getModel: (provider: string, id: string) =>
      catalog.find((model) => model.provider === provider && model.id === id),
    getAvailable: async (provider?: string) =>
      authenticated.filter((model) => provider === undefined || model.provider === provider),
  } as unknown as ModelCatalog;
}

const codexA = { provider: "openai-codex", id: "gpt-6-astra" };
const codexB = { provider: "openai-codex", id: "gpt-6-mini" };
const anthropic = { provider: "anthropic", id: "claude-opus-4-5" };

describe("model override resolution", () => {
  it("resolves provider/default to the first authenticated model of that provider", async () => {
    const catalog = fakeCatalog([anthropic, codexA, codexB], [anthropic, codexB, codexA]);
    assert.deepEqual(await resolveConfiguredModel(catalog, "openai-codex/default"), codexB);
  });

  it("never leaves the provider when only other providers are authenticated", async () => {
    const catalog = fakeCatalog([anthropic, codexA], [anthropic]);
    await assert.rejects(
      resolveConfiguredModel(catalog, "openai-codex/default"),
      /provider 'openai-codex' has no authenticated models/,
    );
  });

  it("keeps explicit ids exact instead of substituting a provider default", async () => {
    const catalog = fakeCatalog([codexA, codexB], [codexA, codexB]);
    assert.deepEqual(await resolveConfiguredModel(catalog, "openai-codex/gpt-6-mini"), codexB);
    await assert.rejects(
      resolveConfiguredModel(catalog, "openai-codex/gpt-7"),
      /not present in the node catalog/,
    );
  });

  it("rejects an explicit id whose provider is not authenticated", async () => {
    const catalog = fakeCatalog([codexA, anthropic], [anthropic]);
    await assert.rejects(
      resolveConfiguredModel(catalog, "openai-codex/gpt-6-astra"),
      /no configured authentication/,
    );
  });

  it("rejects overrides that are not provider/model", async () => {
    const catalog = fakeCatalog([codexA], [codexA]);
    for (const value of ["default", "openai-codex/", "/gpt-6-astra"]) {
      await assert.rejects(resolveConfiguredModel(catalog, value), /provider\/model format/);
    }
  });
});

describe("model catalog readiness", () => {
  it("combines exact catalog models with refreshed provider auth", async () => {
    const checkCalls: string[] = [];
    const authCalls: string[] = [];
    const runtime: CatalogRuntime = {
      getAvailable: async () => [
        codexA,
        anthropic,
        { provider: "opencode-go", id: "big-pickle" },
      ],
      checkAuth: async (provider) => {
        checkCalls.push(provider);
        return provider === "opencode-go" ? undefined : { type: "oauth" };
      },
      getAuth: async (provider) => {
        authCalls.push(provider);
        if (provider === "openai-codex") {
          return { auth: { apiKey: "refreshed-token" } };
        }
        throw new Error("expired OAuth could not be refreshed");
      },
    };

    const catalog = await observeModelCatalog(runtime, new AbortController().signal);

    assert.deepEqual(authCalls.sort(), ["anthropic", "openai-codex"]);
    assert.deepEqual(checkCalls.sort(), ["anthropic", "openai-codex", "opencode-go"]);
    assert.deepEqual(catalog, [
      {
        id: "anthropic/claude-opus-4-5",
        displayName: "claude-opus-4-5",
        provider: "anthropic",
        authStatus: "unknown",
      },
      {
        id: "codex/gpt-6-astra",
        displayName: "gpt-6-astra",
        provider: "openai-codex",
        authStatus: "ready",
      },
      {
        id: "opencode-go/big-pickle",
        displayName: "big-pickle",
        provider: "opencode-go",
        authStatus: "unavailable",
      },
    ]);
  });

  it("emits flat provider selectors with the OpenAI codex shorthand", () => {
    const catalog = mapModelsToCatalog(
      [
        { provider: "kimi-coding", id: "k3", name: "Kimi K3" },
        { provider: "openai-codex", id: "gpt-6-mini" },
        { provider: "opencode-go", id: "big-pickle" },
        { provider: "openai-codex", id: "gpt-6-astra", name: "GPT-6 Astra" },
        { provider: "kimi-coding", id: "k3" },
      ],
      new Map([
        ["kimi-coding", "ready"],
        ["openai-codex", "ready"],
        ["opencode-go", "ready"],
      ]),
    );
    assert.deepEqual(catalog, [
      {
        id: "codex/gpt-6-astra",
        displayName: "GPT-6 Astra",
        provider: "openai-codex",
        authStatus: "ready",
      },
      {
        id: "codex/gpt-6-mini",
        displayName: "gpt-6-mini",
        provider: "openai-codex",
        authStatus: "ready",
      },
      {
        id: "kimi-coding/k3",
        displayName: "Kimi K3",
        provider: "kimi-coding",
        authStatus: "ready",
      },
      {
        id: "opencode-go/big-pickle",
        displayName: "big-pickle",
        provider: "opencode-go",
        authStatus: "ready",
      },
    ]);
  });
});
