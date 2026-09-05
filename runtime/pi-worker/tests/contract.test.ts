/**
 * Compile-level contract tests against the pinned official SDK (0.85.0)
 * and the worker's root tool surface (SPEC.md sections 13.3, 24.2, 25.1).
 * Skips with a loud message when the pinned package is not installed.
 */
import assert from "node:assert/strict";
import { describe, it } from "node:test";
import { access, mkdir, mkdtemp, rm, writeFile } from "node:fs/promises";
import { tmpdir } from "node:os";
import { join } from "node:path";

let sdk: Record<string, unknown> | undefined;
let loadError: string | undefined;
try {
  sdk = (await import("../src/sdk.ts")) as Record<string, unknown>;
} catch (cause) {
  loadError = (cause as Error).message;
}
let pi: Record<string, unknown> | undefined;
try {
  pi = (await import("@earendil-works/pi-coding-agent")) as Record<string, unknown>;
} catch {
  pi = undefined;
}
const installed = sdk !== undefined && pi !== undefined;

describe("pinned SDK export contract", () => {
  it("loads the official SDK through the adapter", () => {
    if (!installed) {
      it.skip(`@earendil-works/pi-coding-agent not installed: ${loadError}`, () => {});
      return;
    }
    assert.ok(pi !== undefined);
    for (const name of [
      "createAgentSession",
      "defineTool",
      "ModelRuntime",
      "SessionManager",
      "SettingsManager",
      "DefaultResourceLoader",
    ]) {
      assert.ok(name in pi, `SDK export missing: ${name}`);
    }
    const factoryFns = pi as Record<string, unknown>;
    assert.equal(typeof factoryFns["createAgentSession"], "function");
    assert.equal(typeof factoryFns["defineTool"], "function");
    assert.equal(
      typeof (factoryFns["ModelRuntime"] as Record<string, unknown>)["create"],
      "function",
    );
    const sessions = factoryFns["SessionManager"] as Record<string, unknown>;
    for (const name of ["inMemory", "create", "open", "continueRecent"]) {
      assert.equal(typeof sessions[name], "function", `SessionManager.${name}`);
    }
    const settings = factoryFns["SettingsManager"] as Record<string, unknown>;
    assert.equal(typeof settings["inMemory"], "function");
    assert.equal(typeof settings["create"], "function");
  });

  it("exposes the worker adapter factory", { skip: !installed }, () => {
    assert.equal(
      typeof (sdk as Record<string, unknown>)["createSdkSessionFactory"],
      "function",
    );
  });

  it("does not execute repository-provided Pi extensions", { skip: !installed }, async () => {
    const root = await mkdtemp(join(tmpdir(), "pi-command-center-sdk-"));
    const repository = join(root, "repository");
    const agentDir = join(root, "agent");
    const marker = join(root, "extension-ran");
    try {
      await mkdir(join(repository, ".pi", "extensions"), { recursive: true });
      await mkdir(agentDir, { recursive: true });
      await writeFile(
        join(repository, ".pi", "extensions", "malicious.mjs"),
        `import { writeFileSync } from "node:fs"; writeFileSync(${JSON.stringify(marker)}, "ran");`,
      );

      const createLoader = (sdk as Record<string, unknown>)["createRestrictedResourceLoader"] as
        (cwd: string, dataDir: string, prompt: string) => Promise<unknown>;
      await createLoader(repository, agentDir, "restricted");
      await assert.rejects(access(marker));
    } finally {
      await rm(root, { recursive: true, force: true });
    }
  });
});
