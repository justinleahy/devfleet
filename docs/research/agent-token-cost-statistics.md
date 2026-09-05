# Research: agent token and cost statistics (Pi, Claude Code, Antigravity, Muse)

**Date researched:** 2026-09-05.

**Scope:** exact usage/cost payload fields; whether each is per-turn, cumulative, or unspecified; which numbers DevFleet may sum vs latest-per-session; where USD is unavailable. No credential access, no price estimation, no provider payload guessing. Missing telemetry stays absent.

**Pinned first-party surfaces:**

| Runtime | Version / artifact |
|---|---|
| Pi SDK | `@earendil-works/pi-coding-agent` **0.85.0** and `@earendil-works/pi-ai` **0.85.0** (`runtime/node_modules`) |
| Claude Code stream-json | https://code.claude.com/docs/en/headless ; https://code.claude.com/docs/en/agent-sdk/cost-tracking ; https://code.claude.com/docs/en/agent-sdk/typescript |
| Antigravity CLI | https://antigravity.google/docs/cli/headless |
| Muse MSP v1 | Muse Code **1.0.3-R2198.1** (`/home/justinleahy/.local/bin/muse-bin-1.0.3-R2198.1`); fingerprint `sha256:03312c213efd14277a0e0a102f70adeae497a469ca4edf7242f479953ed758b7`; schema `msp.schema.json` (tbh-protocol spec 206) |

---

## Privacy and precision (fail closed)

1. Never zero missing, non-finite, or unknown-shaped fields.
2. Never estimate USD from token counts or catalog rates. Persist USD only when the stored event already contains a numeric cost field, and label it **client/catalog estimate**, never provider-billed (none of the four streams document an invoice field).
3. Never fold cache into input. Never add thinking/reasoning into output when docs call it a subset; if overlap is unspecified (Antigravity `thinking_tokens`), keep a separate series.
4. Never open credential stores or transcripts (`~/.claude`, `~/.pi`, `~/.gemini`, `~/.config/muse`, Muse session JSONL).
5. Malformed/unknown append-only payloads: skip the event; no partial fill.
6. Do not substitute `/usage` subscription quota for session token/cost statistics.

---

## Normalization table

Legend: **P** = per completion/step (summable at that grain). **T** = per user turn. **S** = session/query cumulative (latest only). **I** = inferred client cost. **N** = cost not present.

| Runtime | Field | Grain | Fleet rule | Cost |
|---|---|---|---|---|
| Pi | `Usage.input` | P, final assistant / compaction / toolResult `usage` | Sum finals; ignore `message_update` | — |
| Pi | `Usage.output` | P | Sum finals | — |
| Pi | `Usage.cacheRead` | P | Sum as cache-read series | — |
| Pi | `Usage.cacheWrite` | P | Sum as cache-write series | — |
| Pi | `Usage.cacheWrite1h?` | P | Optional subset of cacheWrite | — |
| Pi | `Usage.reasoning?` | subset of output | Do not add to output | — |
| Pi | `Usage.totalTokens` | P | Do not add as a fifth series | — |
| Pi | `Usage.cost.*` | P | Do not treat as billed USD | **I** (`calculateCost` catalog) |
| Pi | JSON `message_update.usage` | cumulative on in-flight message | **Replace until `message_end`**; do not sum | I if present |
| Pi | `SessionStats.tokens` / `cost` | S | Latest-per-session or recompute from entries | I |
| Claude | `result.usage.input_tokens` | T; main loop only; streaming-input: this turn | Sum **distinct `-p` processes**; not successive results in one stdin query | — |
| Claude | `result.usage.output_tokens` | T | Same; authoritative output (per-step output is placeholder) | — |
| Claude | `result.usage.cache_read_input_tokens` / `cache_creation_input_tokens` | T | Same | — |
| Claude | `result.modelUsage[model].inputTokens` etc. | S within `query()`; streaming-input running total | **Latest result per query**, then sum queries | `costUSD` **I** |
| Claude | `modelUsage[].thinkingTokens?` | subset of that model output | Do not add to output | — |
| Claude | `result.total_cost_usd` | S within `query()` | Latest per query; label estimate | **I** not billing |
| Claude | assistant `message.usage` | P with `message.id` dedup | Optional; skip `parent_tool_use_id` for main-loop accounting | — |
| Antigravity | `step_update.usage.*` | P when known | Sum DONE steps **xor** use result, never both | **N** |
| Antigravity | `result.usage.input_tokens` | **S** in stdin multi-turn; **T** for one-shot `-p` | **Latest result per `conversation_id`** | **N** |
| Antigravity | `result.usage.output_tokens` | S/T | Latest | **N** |
| Antigravity | `result.usage.thinking_tokens` | S/T | Latest; separate series (overlap unspecified) | **N** |
| Antigravity | `result.usage.cache_read_tokens` | S/T | Latest | **N** |
| Antigravity | `result.usage.total_tokens` | S/T | Latest; do not add to components | **N** |
| Antigravity | USD | — | — | **unavailable** |
| Muse | `session/tokenUsage.usage.inputTokens` | P (one notify per model completion) | Do **not** sum raw input across providers | **N** |
| Muse | `session/tokenUsage.promptTokens` / `totalTokens` | P counted-once | Sum completions **xor** use cumulative | **N** |
| Muse | `session/tokenUsage.cumulative.*` | **S** accumulate-only | **Latest-per-session** | **N** |
| Muse | `usage.reasoningTokens` | reported reasoning | Do not add to output | **N** |
| Muse | `usage.cacheReadTokens` / `cacheWriteTokens` / `cachedTokens` | P | Separate; do not re-derive cache convention | **N** |
| Muse | `turn/completed.usage` | T aggregate | Sum turns xor session cumulative | **N** |
| Muse | USD | — | — | **unavailable** |

---

## 1. Pi SDK 0.85.0

Installed: `runtime/node_modules/@earendil-works/pi-ai/dist/types.d.ts`.

```ts
export interface Usage {
    input: number;
    output: number;
    cacheRead: number;
    cacheWrite: number;
    cacheWrite1h?: number;
    reasoning?: number; // subset of output
    totalTokens: number;
    cost: { input: number; output: number; cacheRead: number; cacheWrite: number; total: number };
}
```

`calculateCost(model, usage)` in `runtime/node_modules/@earendil-works/pi-ai/dist/models.js` writes `usage.cost` from **model.cost catalog rates** (`rate/1e6 * tokens`), with Anthropic 1h cache writes at 2× input. This is **not** a provider invoice.

`addUsageToTotals` (`pi-coding-agent/dist/core/usage-totals.js`) adds `input`, `output`, `cacheRead`, `cacheWrite`, `cost.total` across assistant messages plus optional toolResult/compaction/branch_summary `usage`.

`SessionStats` (`agent-session.d.ts`) exposes session `tokens.{input,output,cacheRead,cacheWrite,total}` and `cost`.

JSON/RPC (`modes/json-event.d.ts`): `message_update.usage` is **cumulative** on the streaming message; `message_end` is authoritative.

**Pi aggregation:** persist finals only; sum those token fields across sessions; omit USD or label catalog estimate; never sum `message_update`.

---

## 2. Claude Code stream-json

URLs: headless, cost-tracking, TypeScript SDK reference (research date 2026-09-05).

`SDKResultMessage` includes `total_cost_usd: number`, `usage: NonNullableUsage`, `modelUsage: { [modelName: string]: ModelUsage }`.

```ts
type Usage = {
  input_tokens: number;
  output_tokens: number;
  cache_creation_input_tokens: number | null;
  cache_read_input_tokens: number | null;
  cache_creation: { ephemeral_5m_input_tokens: number; ephemeral_1h_input_tokens: number } | null;
  server_tool_use: BetaServerToolUsage | null;
  service_tier: "standard" | "priority" | "batch" | null;
  speed: "standard" | "fast" | null;
  inference_geo: string | null;
  iterations: BetaIterationsUsage | null;
  output_tokens_details: BetaOutputTokensDetails | null;
};

type ModelUsage = {
  inputTokens: number;
  outputTokens: number;
  thinkingTokens?: number;
  cacheReadInputTokens: number;
  cacheCreationInputTokens: number;
  webSearchRequests: number;
  costUSD: number;
  contextWindow: number;
  maxOutputTokens: number;
  canonicalModel?: string;
  provider?: string;
  costBasis?: "list" | "managed" | "unknown";
};
```

Official grain:

- `usage`: main loop only (excludes subagents); streaming-input: **per turn**.
- `modelUsage` / `total_cost_usd`: whole query including subagents/compaction; streaming-input: **running total** — read latest; `/clear` resets.
- `total_cost_usd` / `costUSD`: **client-side estimates** (bundled price table or `modelPricing`); may apply 1.1× when `inference_geo` is `"us"`. Do not bill from them.
- Per-step assistant `output_tokens` is a placeholder; parallel tool messages share `id` (count once).

Headless `-p --output-format json` includes `total_cost_usd` and per-model breakdown; same estimate caveat.

**Claude aggregation:** one DevFleet `claude -p` process → one final `result`. Prefer summing `modelUsage` on that line. If only `usage` exists, do not invent subagent tokens. USD as `client_estimate` only.

---

## 3. Antigravity CLI

URL: https://antigravity.google/docs/cli/headless

Documented `usage`:

```json
{
  "input_tokens": 10415,
  "output_tokens": 657,
  "thinking_tokens": 616,
  "cache_read_tokens": 8113,
  "total_tokens": 11072
}
```

No cost field.

- `step_update.usage`: per-step when known.
- `result` once per turn; json envelope identical.
- stdin stream-json: `response` per-turn; **`usage` / `num_turns` / `duration_seconds` cumulative over the session**. Docs example: turn 2 `input_tokens` 30662 after turn 1 30384.

**Antigravity aggregation:** latest `result.usage` per `conversation_id`. Cost unavailable. Do not estimate Gemini prices.

---

## 4. Muse MSP v1 `session/tokenUsage`

First-party: `muse-bin-1.0.3-R2198.1` strings include notification `session/tokenUsage`; schema bundle fingerprint matches `MuseProtocol.KnownFingerprint`. `$defs/SessionTokenUsageParams` description: one per model completion; raw counters plus counted-once `promptTokens`/`totalTokens`; `cumulative` never goes backward; subagent usage not folded in.

Required params: `cumulative`, `promptTokens`, `sessionId`, `sourceRange`, `totalTokens`, `turnId`, `usage`, `viewCursor`. Optional: `durationMs`, `finishReason`, `modelId`.

`TokenUsage`: required `inputTokens`, `outputTokens`, `reasoningTokens`, `cachedTokens`; optional `cacheReadTokens`, `cacheWriteTokens`. Raw counters **not directly summable across providers**.

`CumulativeTokenUsage`: `promptTokens`, `outputTokens`, `totalTokens` (session counted-once totals).

`turn/completed.usage`: turn aggregate, same `TokenUsage` shape.

No cost property. SPEC §27a.4: no usage **RPC method** for quota; the **notification** still exists. Statistics may use persisted `session.usage` events (`MuseCodeRuntimeAdapter` maps `tokenUsage` → `session.usage`).

Observed (research only, not a production data source) projection object:

```json
{
  "cumulative": { "outputTokens": 4014, "promptTokens": 579968, "totalTokens": 583982 },
  "durationMs": 10569,
  "modelId": "muse-spark-1.3",
  "promptTokens": 53177,
  "totalTokens": 53787,
  "usage": {
    "cacheReadTokens": 52209,
    "cacheWriteTokens": 0,
    "cachedTokens": 52209,
    "inputTokens": 53177,
    "outputTokens": 610,
    "reasoningTokens": 203
  }
}
```

**Muse aggregation:** latest `cumulative` per session. Cost unavailable.

---

## Recommended fail-closed aggregation contract

Per DevFleet session, keep one snapshot updated only from well-typed finite numbers ≥ 0:

```ts
type CostKind = "absent" | "client_estimate";

type SessionTokenSnapshot = {
  input: number | null;
  output: number | null;
  cacheRead: number | null;
  cacheWrite: number | null;
  thinking: number | null;
  costUsd: number | null;
  costKind: CostKind;
  source:
    | "pi.assistant_sum"
    | "claude.result.modelUsage"
    | "claude.result.usage"
    | "agy.result.latest"
    | "muse.cumulative";
};
```

1. Skip malformed events.
2. **Pi:** on each **final** assistant (and compaction/toolResult with `usage`), **add** token fields. Initialize series on first valid add, not with zeros beforehand. Ignore `message_update`.
3. **Claude:** on `type === "result"`, **replace** from `modelUsage` (sum models) if present, else `usage`. `costUsd = total_cost_usd`, `costKind: client_estimate`.
4. **Antigravity:** on `event === "result"`, **replace** from `usage`. `costKind: absent`.
5. **Muse:** on `session.usage`, **replace** from `cumulative.promptTokens` / `cumulative.outputTokens`. Map cache only if `cacheReadTokens` is a finite number. `costKind: absent`.
6. **Fleet `/statistics`:** sum non-null series independently. Null is not zero. Fleet USD = sum of `client_estimate` only, labeled estimate, with a coverage count if any session is `absent`.
7. **Runtime/model breakdown:** only if stored payload has `model` / `modelId` / `modelUsage`. Otherwise omit.
8. Tracked vs active agent counts come from DevFleet session liveness, not token events.

---

## Sources (verbatim anchors)

- `runtime/node_modules/@earendil-works/pi-ai/dist/types.d.ts` — `export interface Usage`
- `runtime/node_modules/@earendil-works/pi-ai/dist/models.js` — `export function calculateCost`
- `runtime/node_modules/@earendil-works/pi-coding-agent/dist/core/usage-totals.js` — `addUsageToTotals`
- `runtime/node_modules/@earendil-works/pi-coding-agent/dist/modes/json-event.d.ts` — cumulative usage on `message_update`
- https://code.claude.com/docs/en/agent-sdk/typescript — `SDKResultMessage`, `Usage`, `ModelUsage`
- https://code.claude.com/docs/en/agent-sdk/cost-tracking — estimate warning; `usage` vs `modelUsage` vs `total_cost_usd` scope
- https://code.claude.com/docs/en/headless — json/`stream-json` result cost metadata
- https://antigravity.google/docs/cli/headless — `usage` fields; per-step vs cumulative session `usage`
- Muse `msp.schema.json` `$defs/SessionTokenUsageParams`, `TokenUsage`, `CumulativeTokenUsage`, `notifications["session/tokenUsage"]`
- `src/PiCommandCenter.Node/Runtime/Muse/MuseProtocol.cs` — `KnownFingerprint`, `SchemaVersion = 1`
