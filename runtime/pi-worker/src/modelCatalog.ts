import { ModelRuntime } from "@earendil-works/pi-coding-agent";

// DevFleet routes `codex/<id>` selectors to Pi's ChatGPT-subscription provider; only that
// provider's models are reported, already rewritten into the canonical selector form.
const PI_PROVIDER = "openai-codex";
const SELECTOR_RUNTIME = "codex";

const signal = AbortSignal.timeout(25_000);
const runtime = await ModelRuntime.create({ signal });
const models = await runtime.getAvailable(PI_PROVIDER, { signal });
const result = models
  .map((model) => ({
    id: `${SELECTOR_RUNTIME}/${model.id}`,
    displayName: model.name ?? model.id,
    provider: model.provider,
  }))
  .sort((left, right) => left.id.localeCompare(right.id));
process.stdout.write(`${JSON.stringify(result)}\n`);
