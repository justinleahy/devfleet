/**
 * Model override resolution (SPEC.md section 25.1): a `provider/model`
 * override may only land on that provider, `provider/default` picks the first
 * authenticated model of that provider, and explicit ids stay exact.
 */
import assert from "node:assert/strict";
import { describe, it } from "node:test";
import { resolveConfiguredModel, type ModelCatalog } from "../src/sdk.ts";

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
