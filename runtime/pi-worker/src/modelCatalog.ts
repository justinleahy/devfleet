import { ModelRuntime } from "@earendil-works/pi-coding-agent";

const signal = AbortSignal.timeout(25_000);
const runtime = await ModelRuntime.create({ signal });
const models = await runtime.getAvailable(undefined, { signal });
const result = [...models]
  .map((model) => ({
    id: `${model.provider}/${model.id}`,
    displayName: model.name ?? model.id,
    provider: model.provider,
  }))
  .sort((left, right) =>
    left.provider.localeCompare(right.provider) || left.id.localeCompare(right.id),
  );
process.stdout.write(`${JSON.stringify(result)}\n`);
