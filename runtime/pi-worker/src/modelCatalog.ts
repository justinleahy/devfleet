import { pathToFileURL } from "node:url";
import { ModelRuntime } from "@earendil-works/pi-coding-agent";

// DevFleet routes Pi models through flat `<provider>/<model>` selectors.
// OpenAI Codex keeps the shorthand `codex/<id>` form; every other Pi provider selects
// as `<provider>/<id>` with the real Pi provider id. Each entry also carries the
// provider-scoped result of Pi's current credential resolution.
const OPENAI_PROVIDER = "openai-codex";
const CODEX_PROVIDER = "codex";

export type CatalogAuthStatus = "ready" | "unavailable" | "unknown";

export interface CatalogModel {
  id: string;
  displayName: string;
  provider: string;
  authStatus: CatalogAuthStatus;
}

interface PiModel {
  id: string;
  name?: string;
  provider: string;
}

export interface CatalogRuntime {
  getAvailable(
    providerId?: string,
    options?: { signal?: AbortSignal },
  ): Promise<readonly PiModel[]>;
  checkAuth(
    providerId: string,
    options?: { signal?: AbortSignal },
  ): Promise<unknown | undefined>;
  getAuth(
    providerId: string,
    options?: { signal?: AbortSignal },
  ): Promise<unknown | undefined>;
}

/** Map Pi models into DevFleet selector form with provider-scoped auth evidence. */
export function mapModelsToCatalog(
  models: readonly PiModel[],
  authByProvider: ReadonlyMap<string, CatalogAuthStatus>,
): CatalogModel[] {
  const seen = new Set<string>();
  const result: CatalogModel[] = [];
  for (const model of models) {
    const id =
      model.provider === OPENAI_PROVIDER
        ? `${CODEX_PROVIDER}/${model.id}`
        : `${model.provider}/${model.id}`;
    if (seen.has(id)) continue;
    seen.add(id);
    result.push({
      id,
      displayName: model.name ?? model.id,
      provider: model.provider,
      authStatus: authByProvider.get(model.provider) ?? "unknown",
    });
  }
  return result.sort((left, right) => left.id.localeCompare(right.id));
}

/** Validate or refresh each catalog provider through Pi's request-auth API. */
export async function observeModelCatalog(
  runtime: CatalogRuntime,
  signal: AbortSignal,
): Promise<CatalogModel[]> {
  const models = await runtime.getAvailable(undefined, { signal });
  const providers = [...new Set(models.map((model) => model.provider))];
  const statuses = await Promise.all(
    providers.map(async (provider): Promise<readonly [string, CatalogAuthStatus]> => {
      try {
        const configured = await runtime.checkAuth(provider, { signal });
        if (configured === undefined) return [provider, "unavailable"];
        const auth = await runtime.getAuth(provider, { signal });
        return [provider, auth === undefined ? "unavailable" : "ready"];
      } catch {
        signal.throwIfAborted();
        return [provider, "unknown"];
      }
    }),
  );
  return mapModelsToCatalog(models, new Map(statuses));
}

async function main(): Promise<void> {
  const signal = AbortSignal.timeout(25_000);
  const runtime = await ModelRuntime.create({ signal, allowModelNetwork: false });
  const catalog = await observeModelCatalog(runtime, signal);
  process.stdout.write(`${JSON.stringify(catalog)}\n`);
}

if (process.argv[1] !== undefined && import.meta.url === pathToFileURL(process.argv[1]).href) {
  try {
    await main();
  } catch {
    process.stderr.write("Pi model readiness probe failed.\n");
    process.exitCode = 1;
  }
}
