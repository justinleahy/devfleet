import { pathToFileURL } from "node:url";
import { ModelRuntime } from "@earendil-works/pi-coding-agent";

// DevFleet routes Pi-authenticated models through flat `<provider>/<model>` selectors.
// OpenAI Codex keeps the shorthand `codex/<id>` form; every other Pi provider selects
// as `<provider>/<id>` with the real Pi provider id. `Provider` always carries the real
// Pi provider id so routing can recover the exact SDK `provider/model`.
const OPENAI_PROVIDER = "openai-codex";
const CODEX_PROVIDER = "codex";

export interface CatalogModel {
  id: string;
  displayName: string;
  provider: string;
}

interface PiModel {
  id: string;
  name?: string;
  provider: string;
}

/** Map Pi-authenticated models from every provider into DevFleet selector form. */
export function mapModelsToCatalog(models: readonly PiModel[]): CatalogModel[] {
  const seen = new Set<string>();
  const result: CatalogModel[] = [];
  for (const model of models) {
    const id =
      model.provider === OPENAI_PROVIDER
        ? `${CODEX_PROVIDER}/${model.id}`
        : `${model.provider}/${model.id}`;
    if (seen.has(id)) continue;
    seen.add(id);
    result.push({ id, displayName: model.name ?? model.id, provider: model.provider });
  }
  return result.sort((left, right) => left.id.localeCompare(right.id));
}

async function main(): Promise<void> {
  const signal = AbortSignal.timeout(25_000);
  const runtime = await ModelRuntime.create({ signal });
  const models = await runtime.getAvailable(undefined, { signal });
  process.stdout.write(`${JSON.stringify(mapModelsToCatalog(models))}\n`);
}

if (process.argv[1] !== undefined && import.meta.url === pathToFileURL(process.argv[1]).href) {
  await main();
}
