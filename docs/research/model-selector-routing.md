# Research: canonical `<runtime>/<model>` selector routing

**Date researched:** 2026-09-05

**Primary sources:**

- Pi package README (CLI `--model`, `provider/id`): https://raw.githubusercontent.com/earendil-works/pi-mono/main/packages/coding-agent/README.md
- Pi `model-resolver.ts` (first-slash parse): https://raw.githubusercontent.com/earendil-works/pi-mono/main/packages/coding-agent/src/core/model-resolver.ts
- Pi `openai-codex` provider factory: https://cdn.jsdelivr.net/gh/earendil-works/pi-mono@main/packages/ai/src/providers/openai-codex.ts
- Pi providers: https://raw.githubusercontent.com/earendil-works/pi-mono/main/packages/coding-agent/docs/providers.md
- Pi SDK: https://raw.githubusercontent.com/earendil-works/pi-mono/main/packages/coding-agent/docs/sdk.md
- Anthropic Claude Code CLI: https://code.claude.com/docs/en/cli-reference
- Anthropic Claude Code model config: https://code.claude.com/docs/en/model-config
- Anthropic Claude Code headless / permissions: https://code.claude.com/docs/en/headless ; https://code.claude.com/docs/en/permission-modes
- Antigravity headless: https://antigravity.google/docs/cli/headless
- Antigravity CLI reference: https://antigravity.google/docs/cli/reference

This note separates **verified facts** (owned by those sources) from **DevFleet decisions**.

---

## 1. Verified facts

### 1.1 Pi: `provider` + `id`, first `/` only

- Pi `--model` accepts a **pattern or ID**, documented as supporting **`provider/id`** and optional `:<thinking>`. Example: `pi --model openai/gpt-4o` needs no `--provider`. Source: package README CLI Reference / examples.
- `findExactModelReferenceMatch` in `model-resolver.ts`:
  1. Trim; empty → no match.
  2. Prefer a unique exact match of `` `${model.provider}/${model.id}` `` (case-insensitive).
  3. Else **`indexOf("/")` once**: `provider = substring(0, slash)`, `modelId = substring(slash + 1)` (both trimmed, both required). Remainder after the **first** slash is the model id (ids may contain further `/`, e.g. Cloudflare `@cf/moonshotai/kimi-k2.6`, OpenRouter `moonshotai/kimi-k2.6`). Source: `model-resolver.ts` `findExactModelReferenceMatch`; defaults table in the same file.
- `Models.getModel(provider, id)` is the SDK lookup. Source: `packages/ai/src/models.ts` (`getModel(provider: string, id: string)`).
- ChatGPT Plus/Pro Codex is a **separate provider id** `openai-codex` (`id: "openai-codex"`, API `openai-codex-responses`), not `openai`. Source: `openai-codex.ts`; providers.md “OpenAI Codex”; `defaultModelPerProvider["openai-codex"]`.

### 1.2 Claude Code: suffix → `--model`

- `--model` sets the session model to an **alias** (`sonnet`, `opus`, `haiku`, `fable`, …) or a **full model name**. Overrides settings `model` and `ANTHROPIC_MODEL`. Source: https://code.claude.com/docs/en/cli-reference (`--model`); https://code.claude.com/docs/en/model-config (“At startup: `claude --model <alias|name>`”).
- Alias **`default`** is a special value that **clears an override** and reverts to the account runtime default; it is **not itself a model alias**. Source: model-config “Model aliases”.
- Headless: `claude -p` with `--model` applies to that session only (not saved as user default). Source: model-config.

### 1.3 Antigravity: `agy models` and `--model`

- `agy models` lists available model slugs. Pin with `--model <slug>` (and `--effort`, `--agent`). Source: https://antigravity.google/docs/cli/headless (section on model/agent selection); flag table: `--model` = “Model slug for this run (see `agy models`)”.
- Headless `init.model` is present **only when** `--model` is set. Source: headless streaming JSON `init` table.
- Unknown `--model` in headless: **no silent fallback**; non-zero exit, `ERROR` envelope. Source: same page.

### 1.4 Security surfaces (upstream)

- **Claude Code writes:** permission modes and deny rules apply in every mode including `bypassPermissions`; `dontAsk` + `permissions.allow` / `--allowedTools` and PreToolUse deny are the documented lock-down path. Source: https://code.claude.com/docs/en/permission-modes ; hooks on https://code.claude.com/docs/en/hooks . Host isolation via `--settings` (outranks project/user). Source: https://code.claude.com/docs/en/settings .
- **Antigravity headless:** workspace **file read/write is auto-allowed**; shell defaults to Ask and is **soft-denied** unless granted; `--dangerously-skip-permissions` sets `always-proceed`. Source: https://antigravity.google/docs/cli/headless “Permissions in headless mode”.
- **Pi:** no built-in FS/process permission system; process permissions apply. Source: pi-mono README “Permissions & Containerization” (see `docs/research/pi-sdk.md`).

---

## 2. DevFleet decisions (not upstream)

Labeled so they are not confused with vendor contracts.

1. **Canonical selector** is one required string `<runtime>/<model>`: trim, max 256, **split on the first `/` only**, both parts required. Prefixes: `codex`, `claude-code`, `antigravity`, `muse`. Examples: `codex/gpt-5.6-sol`, `claude-code/fable-5-1`, `antigravity/gemini-3-pro`, `muse/muse-spark-1.3`.
2. **Registry** routes by **selector prefix** to a **trusted in-tree adapter** only (no user-chosen executable). Security remains prefix → trusted adapter.
3. **Superseded:** `default` as the model part was previously interpreted as **that runtime's provider default** (Claude Code and Antigravity omitting their model override; Pi resolving `codex/default` to the first authenticated `openai-codex` model). The explicit-model rollout removed `/default` as a valid selector: routes now require an explicit provider-native model id. Shared concrete built-ins: `codex/gpt-5.6-sol`, `claude-code/fable-5-1`, `antigravity/gemini-3-pro`, `muse/muse-spark-1.3`.
4. **`codex/<id>` → Pi `openai-codex/<id>`** when talking to Pi (`provider=openai-codex`, `id` remainder). Do not use Pi `openai/` (API-key provider).
5. **`claude-code/<suffix>`** passes an explicit provider-native `suffix` unchanged as `claude --model <suffix>` (e.g. `fable-5-1`, `opus`, or a full model id). Claude's `default` alias is not a model and is not accepted as a selector suffix.
6. **`antigravity/<slug>`** passes `slug` as `agy --model <slug>` after listing via `agy models` when cataloging.
7. **Claude child writes** are **lease-derived** (node reservation / PreToolUse + deny Bash), not Claude’s unrestricted `acceptEdits`/`bypassPermissions`.
8. **Antigravity** is **OS-level read-only** for DevFleet children (do not enable write/shell grants / `--dangerously-skip-permissions`), even though upstream auto-allows workspace writes.
9. **`AgentStartRequest.Model`** is the required canonical selector; **no `RuntimeProfile`**. Route candidates: `RuntimeRouteCandidateMessage(string Model)`. Catalog key `Runtime`; IDs canonical. Node config: `AllowedRoles` + `RoleRoutes` only. Sessions store `Runtime` + `Model`. Usage messages omit runtime-profile lists.
10. **No compatibility aliases** for old profile + optional-model pairs.

---

## 3. Mapping table

| Selector | Adapter | Upstream model argument | Notes |
|---|---|---|---|
| `codex/<id>` | Pi worker | Pi provider `openai-codex`, model id `<id>` | Decision: map suffix to `openai-codex/<id>`. Fact: provider id is `openai-codex`. |
| `codex/default` | Pi worker | *(superseded)* first authenticated `openai-codex` catalog model | **Superseded:** no longer a valid selector; use an explicit id such as `codex/gpt-5.6-sol`. Historical decision was provider-confined default. |
| `claude-code/<suffix>` | Claude CLI | `--model <suffix>` | Fact: `--model` alias or full name. |
| `claude-code/default` | Claude CLI | *(superseded)* omit `--model` or `--model default` | **Superseded:** no longer a valid selector; use an explicit id such as `claude-code/fable-5-1`. Historical: Claude alias `default` clears the override, it is not a model. |
| `antigravity/<slug>` | `agy` | `--model <slug>` | Fact: `agy models` + `--model`. |
| `antigravity/default` | `agy` | *(superseded)* omit `--model` | **Superseded:** no longer a valid selector; use an explicit slug such as `antigravity/gemini-3-pro`. |
| `codex/gpt-5.6-sol` | Pi worker | Pi provider `openai-codex`, model id `gpt-5.6-sol` | Shared concrete built-in. |
| `claude-code/fable-5-1` | Claude CLI | `--model fable-5-1` | Shared concrete built-in. |
| `antigravity/gemini-3-pro` | `agy` | `--model gemini-3-pro` | Shared concrete built-in. |
| `muse/muse-spark-1.3` | Muse adapter | explicit model `muse-spark-1.3` | Shared concrete built-in. |

---

## 4. Sources (URLs)

- https://raw.githubusercontent.com/earendil-works/pi-mono/main/packages/coding-agent/README.md
- https://raw.githubusercontent.com/earendil-works/pi-mono/main/packages/coding-agent/src/core/model-resolver.ts
- https://cdn.jsdelivr.net/gh/earendil-works/pi-mono@main/packages/ai/src/providers/openai-codex.ts
- https://raw.githubusercontent.com/earendil-works/pi-mono/main/packages/coding-agent/docs/providers.md
- https://raw.githubusercontent.com/earendil-works/pi-mono/main/packages/coding-agent/docs/sdk.md
- https://code.claude.com/docs/en/cli-reference
- https://code.claude.com/docs/en/model-config
- https://code.claude.com/docs/en/headless
- https://code.claude.com/docs/en/permission-modes
- https://antigravity.google/docs/cli/headless
- https://antigravity.google/docs/cli/reference
