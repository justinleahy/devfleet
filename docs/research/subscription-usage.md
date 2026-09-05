# Research: subscription usage / remaining quota reporting

**Date researched:** 2026-09-05 (original survey); **decision addendum** same day, see [Decision: private first-party endpoints accepted](#decision-private-first-party-endpoints-accepted).  
**Original constraint:** first-party docs, official source, and installed CLI `--help` only. No credential stores. No model invocations. No quota-consuming prompts. The survey below stands as the **public-surface** finding: none of the three providers offers a public, documented, machine-readable remaining-quota API. The decision addendum records what DevFleet ships instead and why.

**Local CLI versions observed (help/version only):**

| Binary | Version command | Observed |
|---|---|---|
| `pi` | `pi --version` | `0.84.3` |
| `claude` | `claude --version` | `2.1.248 (Claude Code)` |
| `agy` | `agy --version` | `1.1.27` |

**Legend:** **Verified** = stated in official docs or official source. **Local observation** = version/help on this machine. **Unavailable** = no first-party non-interactive remaining-quota surface found *in public docs*. **Private** = a first-party endpoint the official client binary calls but that is not published as an API (accepted by decision; see addendum). **Must fail closed** = do not invent remaining quota.

---

## Contract target (normalized UI, not bound to repo code)

A host UI should present, per provider profile:

- `provider` + `profileId` (opaque local identity; never a secret)
- `status`: `available` | `unavailable` | `error`
- optional `planLabel` / `accountLabel` (non-secret)
- one or more named **limit windows** with percent-used **or** percent-remaining and optional reset time
- `observedAt` (ISO-8601)
- `source` (command/API label)
- `diagnostic` (non-secret: exit code, error class, "no snapshot")

Unknown, truncated, TUI-only, or unparseable output → `status: unavailable` or `error`. Never fabricate remaining quota. Never carry over a previous reading.

Proposed JSON shape (host-owned; not an upstream type):

```json
{
  "provider": "claude-code",
  "profileId": "default",
  "status": "available",
  "planLabel": "Max",
  "accountLabel": null,
  "windows": [
    {
      "name": "session",
      "percentUsed": 42,
      "percentRemaining": null,
      "resetsAt": "2026-09-05T18:00:00Z"
    }
  ],
  "observedAt": "2026-09-05T12:00:00Z",
  "source": "claude --version; claude auth status; GET https://api.anthropic.com/api/oauth/usage",
  "diagnostic": null
}
```

If `windows` cannot be parsed with both a name and at least one of `percentUsed`/`percentRemaining` from a payload whose shape is **pinned** (public schema, or private endpoint pinned to an observed client version), omit windows and set `status` to `unavailable` or `error`.

---

## Pi (`@earendil-works/pi-coding-agent`)

### Verified: no first-party remaining-subscription command

Installed `pi --help` (v0.84.3) lists interactive, `--print`/`-p`, `--mode json|rpc`, `pi auth …`, `pi update --models`, session flags. It does **not** list `/usage`, `quota`, remaining subscription, or plan windows.

Official README command table (`/login`, `/logout`, `/session`, `/model`, …) has **no** usage/quota command. Footer shows **session** token/cache/cost/context for the current conversation, not provider plan remaining. Source: https://github.com/earendil-works/pi-mono/blob/main/packages/coding-agent/README.md (Interactive Mode / Commands).

### Verified: session stats ≠ subscription remaining

RPC `get_session_stats` returns **this session’s** tokens, estimated `cost`, and `contextUsage` percent of the **model context window**. It is not remaining Claude/Codex/Gemini subscription quota.

Command: `{"type": "get_session_stats"}`

Documented response fields: `sessionFile`, `sessionId`, message counts, `tokens.{input,output,cacheRead,cacheWrite,total}`, `cost`, optional `contextUsage.{tokens,contextWindow,percent}`.

Source: https://raw.githubusercontent.com/earendil-works/pi-mono/main/packages/coding-agent/docs/rpc.md (`get_session_stats`).

`contextUsage` omitted when no model/window; `tokens`/`percent` null after compaction until a later assistant response. Compaction failure may mention "API quota exceeded" as an **error string**, not a remaining-quota object.

### Verified: auth readiness without printing secrets

`pi auth --help`:

```
pi auth print-api-key [--provider <provider>] [--model <model>]
pi auth print-bearer-token [--provider <provider>] [--model <model>] [--min-expiry <duration>]
pi auth check [--provider <provider>] [--model <model>] [--json] [--credentials] [--no-refresh]
```

**Do not** use `print-api-key`, `print-bearer-token`, or `--credentials` in DevFleet. Those emit secrets.

Safe probe: `pi auth check --provider <name> --json --no-refresh`  
`--no-refresh` avoids OAuth refresh network. JSON without `--credentials` is status-only.

**Local observation (this machine, no secrets):** `pi auth check --provider anthropic --json --no-refresh` → keys `provider`, `reason`, `status` with `status: not_ready`, `reason: credentials_not_configured`. Treat this shape as **local observation** until pinned against the same version’s source; fail closed if keys differ.

### Quota consumption

- `pi auth check --json --no-refresh`: no model call documented; **should not** consume completion quota. [INFERENCE from help: check/refresh credentials only.]
- `pi -p "…"`, RPC `prompt`, SDK `session.prompt()`: **consume** provider quota. Do not use for remaining-quota polling.
- Footer / `get_session_stats`: local aggregates of **already billed** session usage.

### Credentials / privacy

Auth resolution (SDK): runtime overrides → stored `auth.json` → env → custom provider. Source: https://raw.githubusercontent.com/earendil-works/pi-mono/main/packages/coding-agent/docs/sdk.md ("API Keys and OAuth"). On this machine `auth.json` holds an `openai-codex` **OAuth** credential (`type`, `access`, `refresh`, `expires` ms epoch, `accountId`); `pi auth check --provider openai-codex --json --no-refresh` → `status: ready`, `authType: oauth`. Pi writes `auth.json` **in place** under a `proper-lockfile` lock (directory `auth.json.lock` beside the file, `stale: 30_000`; not rename-atomic), merging `{...current, [provider]: next}`, mode `0600`. Any other writer must therefore take the same lock before touching the file, and must mount the **directory**, not the file, so the lock directory can exist. Pi's own refresh: `POST https://auth.openai.com/oauth/token`, `application/x-www-form-urlencoded`, `grant_type=refresh_token`, `client_id=app_EMoamEEZ73f0CkXaXp7hrann`; refresh triggers when `now + 5 min >= expires`. Source: installed `@earendil-works/pi-ai` 0.84.3 `dist/auth/oauth/openai-codex.js`, `dist/auth/resolve.js`; `@earendil-works/pi-coding-agent` `dist/core/auth-storage.js`. Never print tokens; never use `print-bearer-token`/`--credentials`.

### Timeout / errors

RPC: command `success: false` before acceptance; later failures on the event stream. Transient overloaded/rate-limit/5xx: `set_auto_retry`. No documented timeout for `get_session_stats` or `auth check`. Host should impose its own wall clock (e.g. 5–15 s) and map timeout → `error`.

### Private: ChatGPT/Codex usage snapshot (what DevFleet reads)

Pi has no quota client, but the Pi `openai-codex` credential is a ChatGPT OAuth token, and the **official Codex CLI** reads remaining subscription windows from the ChatGPT backend:

- `GET https://chatgpt.com/backend-api/wham/usage` — headers `Authorization: Bearer <access>`, `ChatGPT-Account-Id: <accountId>`. Does not spend inference quota (inference is `POST …/codex/responses`). Do **not** send `x-openai-codex-luna-reserve` and never call `…/wham/rate-limit-reset-credits/consume`.
- Response (generated OpenAPI `0.0.1` models, snake_case): `plan_type`; `rate_limit.{allowed, limit_reached, primary_window, secondary_window}` each `{used_percent, limit_window_seconds, reset_after_seconds, reset_at}` (Unix seconds); optional/null `credits`, `spend_control`, `additional_rate_limits[]`, `account_id`, `user_id`. Each `additional_rate_limits` entry requires a bounded safe `limit_name`, optional/null `rate_limit`, and that object's primary/secondary windows (same window shape as the aggregate). `credits` and `spend_control` are monetary/credit balances, not percentage plan windows — they stay unrepresented.
- Source: `openai/codex` `codex-rs/backend-client/src/client/rate_limit_resets.rs`, `codex-rs/codex-backend-openapi-models/src/models/rate_limit_status_payload.rs`, `codex-rs/app-server/README.md` ("Rate limits (ChatGPT)"). Spark rows are **not inferred**; they come from `additional_rate_limits`.

**Classification: private.** Not an OpenAI product API; path and shape can change with the Codex CLI. Third-party use of the ChatGPT OAuth token is a ToS consideration the operator accepted.

Mapping: aggregate `primary`/`secondary` keep names `five-hour` / `weekly` (from `limit_window_seconds`: 18000 → `five-hour`, 604800 → `weekly`) → `percentUsed = used_percent`, `percentRemaining = 100 - used_percent`, `resetsAt` from `reset_at`. Optional/null `additional_rate_limits[]` appends independently displayable windows named `{limit_name} {duration}` (provider `limit_name` trimmed, printable, bounded, and safe; duration same as aggregate). A present but malformed array, entry, or window fails the provider. `spend_control.remaining_percent` and other non-percentage credits/spend metadata are a **different** concept and are not plan windows. Redact `account_id`/`user_id`.

Live evidence (2026-09-05, Codex `/wham/usage`, HTTP 200, sanitized): `additional_rate_limits[].limit_name` = `GPT-5.3-Codex-Spark`; both the five-hour and weekly Spark windows reported **0% used**. No account IDs, tokens, reset timestamps, or raw bodies retained. Fable is **not** an OpenAI field.

### Recommendation (Pi) — as shipped

| Need | Action |
|---|---|
| Remaining subscription | Node reads `SubscriptionUsage:PiCredentialPath` (default `~/.pi/agent/auth.json`) key `openai-codex`; if the access token is within 60 s of `expires`, refresh at `auth.openai.com` and commit the rotated credential back under Pi's own `auth.json.lock` (bounded wait, 30 s stale), re-reading the latest file and compare-and-swapping on the `refresh` value that was exchanged; only that entry's `access`/`refresh`/`expires`/`accountId` change, unrelated keys are preserved, the write is a `0600` temp file + fsync + rename + directory fsync, never in place. Then `GET chatgpt.com/backend-api/wham/usage`. `available` only when `rate_limit.primary_window` (and any present `secondary_window`) parse coherently, and any present `additional_rate_limits` entries parse the same way. Aggregate names remain `five-hour`/`weekly`; additional names are `{limit_name} {duration}` (e.g. `GPT-5.3-Codex-Spark five-hour`, `GPT-5.3-Codex-Spark weekly`). |
| Fail closed | No file / no `openai-codex` / not `type: oauth` → `unavailable`. Symlink, FIFO, non-regular, or group/world-accessible file → `credential_unreadable` before any network call. Refresh failure, lock or rename failure (`credential_persist_failed`), non-2xx, timeout, oversize, missing or malformed `rate_limit`, or a present malformed `additional_rate_limits` array/entry/window → `error`. A CAS mismatch (Pi rotated first) discards the node's refresh response and reloads the file once. Never a partial window set; never more than 8 windows. Monetary credits/spend without a percentage stay unrepresented. |
| Session burn | Not shown. RPC `get_session_stats` is session cost, not plan remaining. |
| Never | `pi auth print-api-key`, `print-bearer-token`, `--credentials`; sending the token anywhere but `https://auth.openai.com` / `https://chatgpt.com`. |

---

## Claude Code (`claude`)

### Verified: `/usage` is the plan-limit UI; it is interactive

https://code.claude.com/docs/en/commands.md :

> `/usage` — Show session cost, plan usage limits, and activity stats. On a Pro, Max, Team, or Enterprise plan, includes a breakdown of what counts against your plan limits. `/cost` and `/stats` are aliases.

Also: `/usage` can run **immediately** while Claude is responding (with `/status`, `/tasks`). That is in-session TUI, not a subprocess API.

https://code.claude.com/docs/en/costs.md documents the **Session** block (local token/$ estimate), plan usage bars, usage-credits row, and last-known bars if the usage endpoint is rate-limited (60-minute snapshot; press `r` to retry). Session $ is **not** billing for Max/Pro subscribers.

**No CLI subcommand** such as `claude usage` appears in https://code.claude.com/docs/en/cli-reference.md or `claude --help` (v2.1.248). Scriptable auth is `claude auth status` (JSON default; `--text`; exit 0 logged in, 1 not).

### Verified: `/usage` is not a stable JSON contract

Official examples are **human TUI text** (Session totals, percent attribution, `Unlimited`, `Showing last-known usage`). Keyboard `d`/`w`/`r` are TUI. There is **no** documented JSON schema for `/usage` output.

`claude -p` **does** run prompts and can run some slash commands (`/config key=value` works with `-p`; `/usage-credits` in `-p` **does not** send Team/Enterprise admin requests). Official docs **do not** state that `claude -p "/usage"` prints machine-readable plan bars without starting an agent turn. **Do not invoke `-p /usage`:** it may consume quota or open TUI; both violate fail-closed + quota-free polling. [Unavailable as a verified non-interactive API.]

### Verified: plan windows (conceptual, not parseable CLI)

https://code.claude.com/docs/en/costs.md — Teams/Enterprise seat allowance: **rolling five-hour window** and **weekly window**, shared with Claude chat and Cowork; size depends on seat tier.

Limit **messages** (not remaining %): "You've hit your session limit" / weekly / Opus / Sonnet. Source: https://code.claude.com/docs/en/errors.md and costs.md "When a developer asks about a limit".

Usage-credits spend row: Pro/Max monthly spend vs optional limit; Team/Enterprise personal spend vs personal limit only. Not remaining included-quota percent.

Attribution breakdown is **local session history on this machine**; excludes other devices and claude.ai.

### Verified: JSON that **does** exist (session cost, not remaining plan)

`claude -p … --output-format json` includes `total_cost_usd` (client estimate). Source: https://code.claude.com/docs/en/headless.md . That **consumes quota**. Not a remaining-quota probe.

OpenTelemetry / Console analytics / Enterprise Analytics API are org-admin surfaces, not per-developer CLI remaining bars. Admin API requires `read:analytics` keys — out of scope for a local credential-safe widget.

### Quota consumption

| Surface | Consumes model/plan quota? |
|---|---|
| `claude auth status` | No model prompt documented; login check only. |
| `claude doctor` | Read-only diagnostics; no model call documented. |
| Interactive `/usage` | Fetches usage endpoint (can be **rate-limited**). Not documented as a billed completion; **local observation required** whether the fetch counts as usage. Do not script it. |
| `claude -p` | **Yes** — billed/limited turn. |
| `/insights` | **Yes** — "tokens count against your plan or API usage" (costs.md). |

### Credentials / privacy

Do **not** use `--bare` for subscription users: never reads OAuth/keychain; needs `ANTHROPIC_API_KEY`. Source: headless.md.

Do **not** relocate `CLAUDE_CONFIG_DIR` if reusing user login. Do **not** run `claude setup-token` (prints long-lived OAuth). Linux credential store: `$CLAUDE_CONFIG_DIR/.credentials.json` else `~/.claude/.credentials.json`, mode `0600`, object `claudeAiOauth { accessToken, refreshToken, expiresAt, refreshTokenExpiresAt, scopes, subscriptionType, rateLimitTier, clientId }`. The CLI writes it atomically (temp file `0600` → fsync → rename → fsync dir) with a compare-and-swap on `refreshToken` and no lock file. Source: installed native binary 2.1.248. DevFleet's node reads this file **only** for the private usage fetch below, commits rotated tokens with the same rename + `refreshToken` CAS discipline, and never prints, logs, or forwards its contents.

Safe: `claude auth status` (JSON). **Local observation:** `--text` printed login method, org, email — PII; prefer JSON and **redact email/org** in UI diagnostics.

### Timeout / errors

Usage fetch failure: the CLI shows last-known bars ≤60 min or "usage endpoint is rate limited". DevFleet does **not** keep a last-known snapshot: 429 and any non-success → `error`, empty windows.

SIGTERM on `-p`: exit 143. Irrelevant if `-p` is never used for polling.

### Private: OAuth usage endpoint (what DevFleet reads)

The interactive `/usage` bars come from a first-party HTTPS GET in the CLI binary:

- `GET https://api.anthropic.com/api/oauth/usage` (CLI also uses `?at_wall=1&skip_spend=1`), `Authorization: Bearer <claudeAiOauth.accessToken>`, `Content-Type: application/json`, 5 s timeout in the CLI. On 401 the CLI refreshes and retries once.
- Refresh: `POST https://platform.claude.com/v1/oauth/token`, **JSON** body `{grant_type: "refresh_token", refresh_token, client_id: "9d1c250a-e61b-44d9-88ed-5944d1962f5e", scope}`; 200 → `access_token`, optional `refresh_token` (keep prior if omitted), `expires_in` seconds; optional `account`/`organization` objects (PII — discard).
- Response: object with any of `five_hour`, `seven_day`, `seven_day_oauth_apps`, `seven_day_opus`, `seven_day_sonnet`, `cinder_cove`, `extra_usage`, optional/null `limits`. Root window objects carry `utilization` (**0–100 percentage points**, matching the public statusline `used_percentage` scale) and `resets_at`. `cinder_cove` uses that same shape and is the **cowork credit** percentage window. `extra_usage` is monthly spend/credits (enabled flag, limits, used credits) — a balance, not an included percentage plan window, and stays unrepresented. `limits[]` is parsed only for `kind=weekly_scoped` rows that carry `scope.model.display_name`; those become independently displayable windows named `weekly {display_name}` with provider capitalization preserved (no lowercase normalization). Unscoped session/weekly `limits` rows duplicate the root windows and are skipped. Unknown future `kind` values are ignored. A present but malformed `limits` array, malformed entry, or malformed recognized scoped row fails the provider.
- Live evidence (2026-09-05, installed-client endpoint, HTTP 200): `five_hour.utilization = 36.0`, `seven_day.utilization = 9.0` — percentage points, not fractions. The earlier reading of the binary as "0–1 fraction" was wrong; the first deployment multiplied by 100, which pushed every window outside the strict 0–100 range and the provider reported `error`. Same snapshot: `limits[]` `weekly_scoped` `scope.model.display_name` = `Fable` at **14% used** (`percent` is percent used; `resets_at` RFC3339 or null — timestamps not retained). Only the shape, scale, and those percents were recorded — no account, organization, token, or reset values were retained. Spark is **not** a Claude OAuth field; Fable is **not** inferred from root keys.
- Source: installed native binary `~/.local/share/claude/versions/2.1.248` (`fetchUtilization`, `TOKEN_URL`, `CLIENT_ID`), confirmed against the live response above. The schema remains private and unstable: treat any value outside 0–100 as drift, never rescale to "fix" it. Fable rows come from `limits` `weekly_scoped`, not from guessing a model name.

**Classification: private.** Not in Anthropic's public API docs; the endpoint is itself rate-limited (429). `claude auth status` is the public command and does **not** mint or refresh tokens, so it is only a login/plan-label precheck.

### Recommendation (Claude) — as shipped

- `claude --version`, then `claude auth status`: not logged in (exit 1 / `loggedIn=false`) → `unavailable` with diagnostic `signed_out`, `Authenticated=false`, no credential read, no HTTP call. Plan label (`subscriptionType`) is informational.
- Read `SubscriptionUsage:ClaudeCredentialPath` (default `~/.claude/.credentials.json`) `claudeAiOauth`; if expired or the GET returns 401, refresh at `platform.claude.com`, commit the rotated tokens (re-read, compare-and-swap on the prior `refreshToken`, merge only `accessToken`/`refreshToken`/`expiresAt`, unrelated JSON preserved, `0600` temp + rename, never in place), retry **once**. When the refresh response omits `refresh_token`, the prior refresh token is kept. A CAS mismatch (the CLI rotated first) discards the response and reloads the file once; lock/rename failure → `credential_persist_failed`.
- `available` only when at least one known root window key (`five_hour`, `seven_day`, `seven_day_oauth_apps`, `seven_day_opus`, `seven_day_sonnet`, `cinder_cove`) or a recognized `limits[]` `weekly_scoped` row yields a finite percent in 0–100 and a parseable `resets_at` (RFC3339 or null); root `percentUsed = utilization` (mapped directly — already percentage points, never multiplied); scoped `percentUsed` is the row's `percent`; `percentRemaining = 100 − percentUsed`. Names: existing root labels plus `cinder_cove` → `cowork credit` and `weekly {display_name}` (e.g. `weekly Fable`). Provider-derived labels must be trimmed, printable, bounded, and safe. Any value outside 0–100 fails the whole provider. At most 8 windows.
- Anything else (429, other non-2xx, timeout, oversize, unknown shape, API-key/Bedrock/Vertex sessions with no `claudeAiOauth`) → `error`/`unavailable`, empty windows, stable diagnostic, no body echoed.
- Still never: scrape the TUI, run `claude -p /usage`, parse transcripts, `--bare`, `setup-token`.

---

## Google Antigravity (`agy`)

### Verified: `/usage` and `/credits` are interactive TUI panels

https://antigravity.google/docs/cli/commands/usage :

- `/usage` alias `/quota` "refreshes your model configuration and quota status from the backend and **opens an interactive TUI panel**."
- Displays remaining requests/tokens **per model**; navigation Esc/Q to close.

https://antigravity.google/docs/cli/commands/credits :

- `/credits` opens a TUI: remaining AI Premium credits, consumption history, purchase links.

https://antigravity.google/docs/cli/credits : statusline may show `AI Credits: N` and low-credit highlight. Not a subprocess JSON API.

https://antigravity.google/docs/cli/reference : slash table confirms `/usage` `/quota` and `/credits`. `agy --help` (v1.1.27) lists `models`, `agents`, `mcp`, … — **no** `usage`/`credits` subcommand.

### Verified: headless `agy -p /usage` is mentioned, but as **text**, not schema

https://antigravity.google/docs/cli/headless : CLI-handled slash commands (`/model`, `/usage`) must be **standalone** `agy -p /model` (or `/usage`); they produce a **text report**, not the NDJSON event stream. Sending `/usage` into `--input-format stream-json` **errors** (exit 2).

**Quota-free constraint:** `agy -p` is print mode: it "sends a single prompt to the agent". Whether `/usage` skips the model is **not** explicitly guaranteed in docs. **Local observation (2026-09-05, host, agy 1.1.27):** `agy -p /usage` prints the report and exits; the report is tab-separated rows `group ⇥ Weekly Limit Remaining|Five Hour Limit Remaining ⇥ NN% ⇥ RFC3339`. Because quota-freeness is not documented, DevFleet runs it only on a **manual** Refresh, never on a timer.

Default `--print-timeout` **5m**; unauthenticated headless → `authentication required`, no hang. Exit 0 success; non-zero on failure to produce a response.

`usage` in JSON envelopes is **token counts for that run** (`input_tokens`, `output_tokens`, …), not remaining plan quota.

### Verified: plan windows (conceptual)

https://antigravity.google/docs/plans :

- Ultra: highest quota, refresh **every five hours**, highest weekly limits, third-party models.
- Pro: high quota, refresh **every five hours until weekly limit**, higher weekly limit.
- Others: weekly refresh + weekly rate limit.
- Limits correlate with **work done**, not prompt count; subject to change.
- Overages: Pro/Ultra can spend purchased AI credits when baseline exhausted; `useG1Credits` in settings (`false` default per CLI reference). **External builds only** for that key.

No numeric remaining % in docs. Baseline "viewed in the settings page" (web/TUI), not CLI JSON.

### Auth storage (Linux)

`agy` uses the Secret Service keyring over the D-Bus session bus when a bus is present (5 s timeout, then file fallback); with **no** session bus it goes straight to the file store `~/.gemini/antigravity-cli/antigravity-oauth-token` (`0600`). Source: installed binary changelog/strings; https://antigravity.google/docs/cli/install ("native secure keyring"). DevFleet never parses this file; it only makes `~/.gemini` visible to `agy` inside the node container (see [Decision](#decision-private-first-party-endpoints-accepted)). Mounting the host bus is the wrong tool: the keyring is session-locked from a container and `agy` falls into browser login.

### Recommendation (Antigravity) — as shipped

- `agy --version`, then `agy -p /usage --print-timeout 8s` (ArgumentList, no shell, stdin closed). The 8 s print timeout sits under the runner's 10 s wall clock so `agy` reports its own failure before the process tree is killed.
- Parse **only** the pinned TSV grammar: 4 columns per nonempty line; column 2 ∈ {`Weekly Limit Remaining`, `Five Hour Limit Remaining`}; column 3 `NN%` integer 0–100; column 4 RFC3339. Names `<group> weekly` / `<group> five-hour`; `percentRemaining = NN`, `percentUsed = 100 − NN`; max 8 rows; duplicate names rejected.
- Any deviation (empty stdout, `authentication required`, ANSI, extra columns, out-of-range, duplicates, truncation, non-zero exit, timeout) → the whole provider is `unavailable`/`error` with empty windows and a `process_*` diagnostic.
- Credits (`/credits`) are not read. The private `cloudcode-pa.googleapis.com` `/v1internal:retrieveUserQuota*` RPCs embedded in the binary are **not** called: they would require extracting the OAuth token from `agy`'s store.
- Never enable `--dangerously-skip-permissions` for a usage probe.

---

## Generic provider (extensibility)

A new provider adapter is allowed only if **all** of the following hold:

1. **Non-interactive** command, RPC method, or HTTPS request (no TUI panel, no PTY).
2. **Pinned shape**: either a documented schema, **or** a private first-party surface whose request and response shape have been read from the official client at a named version and recorded here, with an explicit operator decision accepting the stability/ToS risk. At least one limit window identity and percent-used or percent-remaining (or absolute remaining + limit).
3. **Quota-free** (documented, or observed and therefore restricted to manual refresh) — never poll something that may spend user quota.
4. **No secret emission** (no tokens in stdout, logs, diagnostics, or hub messages; credentials only to the exact provider HTTPS origin, redirects disabled).
5. **Bounded runtime and size** (host-enforced timeout and response/file caps).
6. **Fail-closed:** missing field, unknown shape, non-zero exit, empty stdout, ANSI/TUI codes, incoherent percentages → `unavailable`/`error`, empty `windows`. No last-known snapshots, no interpolation.

Otherwise ship `status: unavailable` with diagnostic `no_stable_quota_surface`. Do not scrape statuslines, screenshots, or HTML settings pages.

Suggested adapter interface (host-owned):

```
probe(profile): Promise<NormalizedUsage>
```

`NormalizedUsage` matches the schema in **Contract target**. Adapters must not import repo-specific types from DevFleet packages; keep this note’s shape as the contract.

---

## Security

- Never `pi auth print-api-key` / `print-bearer-token` / `--credentials`.
- Never `claude setup-token`; never `--bare` for subscription remaining (it cannot see OAuth).
- Credential files (`auth.json`, `.credentials.json`) are read **only on the node**, only by the quota reader, must be regular files under a size cap, and are parsed strictly. Their contents never appear in logs, diagnostics, `Source`, or any hub message. `~/.gemini` is never parsed by DevFleet; only `agy` reads it.
- Tokens are sent **only** to `https://chatgpt.com`, `https://auth.openai.com`, `https://api.anthropic.com`, `https://platform.claude.com` — exact origins, HTTPS, redirects disabled. Never to the control plane or the browser.
- Rotated tokens are committed back with `0600` via temp file + atomic rename only (no in-place overwrite), under the official client's own concurrency discipline (Pi: `auth.json.lock`; Claude: `refreshToken` CAS), preserving unrelated JSON; refresh responses' `account`/`organization` objects and usage bodies' `account_id`/`user_id` are discarded.
- Redact email, org names, account IDs, and user IDs from `diagnostic` (Claude `auth status --text` includes email — JSON mode is used).
- Never log raw CLI stdout or provider bodies (success or error).
- Do not pass user prompts to `-p` for "how much quota do I have?" — that **is** a billed turn and may hallucinate numbers.

---

## Parsing

| Rule | Reason |
|---|---|
| Require JSON parse success (HTTP) or the exact TSV grammar (agy) | TUI `/usage` is not a schema; drift must surface as `error`, not a wrong number |
| Require pinned field names | Version drift: Codex OpenAPI `0.0.1`, Claude binary 2.1.248, agy 1.1.27 |
| Percents only if finite 0–100 (Claude `utilization` arrives in percentage points and is used as-is — never rescaled; live 2026-09-05: `36.0` / `9.0`) | Fail closed |
| `percentUsed + percentRemaining == 100` per window | No inference between the two |
| Reset time only if RFC3339 or Unix-seconds epoch | Do not guess "in 3h" |
| No last-known / stale snapshots | Claude's 60-minute fallback is a CLI UX choice, not a reading |
| Session token totals ≠ plan remaining | Pi `get_session_stats`, agy result `usage`, Claude Session $ |
| Empty `windows` + `available` is invalid | Must be `unavailable`/`error` |
| Host timeout and size caps | None of the surfaces documents small bounds |
| At most 8 windows per provider, end to end | More than any real plan reports; a longer list is schema drift, not data |

---

## Sources

| Claim | URL |
|---|---|
| Pi README commands / footer tokens | https://github.com/earendil-works/pi-mono/blob/main/packages/coding-agent/README.md |
| Pi RPC `get_session_stats` | https://raw.githubusercontent.com/earendil-works/pi-mono/main/packages/coding-agent/docs/rpc.md |
| Pi SDK auth order | https://raw.githubusercontent.com/earendil-works/pi-mono/main/packages/coding-agent/docs/sdk.md |
| Pi CLI help / `pi auth` | installed `pi --help`, `pi auth --help` (v0.84.3) |
| Claude `/usage` | https://code.claude.com/docs/en/commands.md |
| Claude costs, bars, credits, 5h/weekly | https://code.claude.com/docs/en/costs.md |
| Claude CLI / `auth status` | https://code.claude.com/docs/en/cli-reference.md |
| Claude headless / `-p` / no quota JSON | https://code.claude.com/docs/en/headless.md |
| Claude limit error strings | https://code.claude.com/docs/en/errors.md |
| Claude Team/Enterprise seats | https://support.claude.com/en/articles/11845131-use-claude-code-with-your-team-or-enterprise-plan |
| agy `/usage` TUI | https://antigravity.google/docs/cli/commands/usage |
| agy `/credits` TUI | https://antigravity.google/docs/cli/commands/credits |
| agy credits/statusline | https://antigravity.google/docs/cli/credits |
| agy CLI reference | https://antigravity.google/docs/cli/reference |
| agy headless `/usage` as standalone `-p` text | https://antigravity.google/docs/cli/headless |
| agy plans 5h/weekly | https://antigravity.google/docs/plans |
| Installed `agy --help` / `--version` | local v1.1.27 |
| Codex `/wham/usage` client, OpenAPI models, README "Rate limits (ChatGPT)" | https://github.com/openai/codex — `codex-rs/backend-client/src/client/rate_limit_resets.rs`, `codex-rs/codex-backend-openapi-models/src/models/rate_limit_status_payload.rs`, `codex-rs/app-server/README.md` |
| Pi openai-codex OAuth refresh / `auth.json` write | installed `@earendil-works/pi-ai` 0.84.3 `dist/auth/oauth/openai-codex.js`, `dist/auth/resolve.js`; `@earendil-works/pi-coding-agent` `dist/core/auth-storage.js` |
| Claude `/api/oauth/usage`, token URL, client id, credential file | installed native binary `~/.local/share/claude/versions/2.1.248` |
| agy keyring vs file store | installed `agy` 1.1.27 binary strings/changelog; https://antigravity.google/docs/cli/install |

---

## Decision: private first-party endpoints accepted

**2026-09-05.** The operator accepted using the private, first-party quota surfaces that the official clients themselves call, in order to show real remaining windows. What that means in practice:

| Provider | Surface | Public? | Pinned to |
|---|---|---|---|
| Pi (`openai-codex`) | `GET https://chatgpt.com/backend-api/wham/usage`; refresh `POST https://auth.openai.com/oauth/token` | **No** — ChatGPT backend used by Codex CLI | Codex OpenAPI models `0.0.1`; Pi 0.84.3 credential layout |
| Claude Code | `GET https://api.anthropic.com/api/oauth/usage`; refresh `POST https://platform.claude.com/v1/oauth/token` | **No** — CLI-internal | Claude Code 2.1.248 |
| Antigravity | `agy -p /usage --print-timeout 8s` text report | Command **documented**, output schema **not** | agy 1.1.27 TSV layout |

Consequences and guardrails:

1. **Stability risk is real and accepted.** Any of these can change path, auth, or shape without notice. When they do, the reader must produce `error` (`http_malformed`, `process_malformed`, …) with empty windows — never a stale or interpolated value. Re-pin by re-reading the official client at the new version and updating this note.
2. **Manual refresh only.** The `/usage` page fetches when the operator presses Refresh; there is no background polling. This bounds ToS exposure and any undocumented quota cost of `agy -p /usage`.
3. **Local credentials, node-only.** Pi and Claude tokens are read from `SubscriptionUsage:PiCredentialPath` / `SubscriptionUsage:ClaudeCredentialPath` on the node. Rotated tokens are committed back to the same files atomically (`0600` temp → fsync → rename → dir fsync) under the official client's own discipline — Pi's `proper-lockfile` `auth.json.lock` plus a CAS on `refresh`, Claude's CAS on `refreshToken` — and never by in-place overwrite. In `compose.yaml` the host `~/.pi/agent` **directory** is bind-mounted at `/provider-auth/pi` (rw; `PiCredentialPath=/provider-auth/pi/auth.json`), `~/.claude` at `/home/node/.claude` (rw), and `~/.gemini` at `/home/node/.gemini` (rw, for `agy`'s file store — no D-Bus). Directories rather than files because the lock directory and the rename target must live beside the credential; a single-file bind mount pins the inode and is not supported. Only the `node` service has these mounts, which exposes the whole host Pi agent directory to the trusted node process; model subprocesses are kept out (Pi tools resolve repository-relative through the node, Claude denies `Bash` and out-of-repository inspect paths, `agy` runs under a bwrap sandbox with `tmpfs` masks over `/provider-auth` and `/home/node/.claude`). A read-only mount keeps reading usage until the token expires and then reports `refresh_failed`/`credential_persist_failed`.
4. **Exact origins.** Credentials go only to the four HTTPS origins above; redirects are disabled; requests are bounded (10 s, 64 KiB response; credential files ≤ 256 KiB, regular files only).
5. **Redaction.** Diagnostics are stable identifiers (`credential_missing`, `credential_unreadable`, `credential_malformed`, `credential_expired`, `credential_persist_failed`, `refresh_failed`, `signed_out`, `http_unauthorized`, `http_rate_limited`, `http_failed`, `http_timeout`, `http_oversized`, `http_malformed`, `quota_not_reported`, `process_missing`, `process_timeout`, `process_failed`, `process_truncated`, `process_malformed`). Provider bodies, tokens, account/user IDs, and e-mails never leave the reader.
6. **No inference.** `available` requires ≥ 1 coherent window; percent pairs sum to 100; reset times are parsed, not estimated. Version, login state, and plan label remain informational only.
7. **Still out of scope:** `pi -p`, `claude -p /usage`, TUI scraping, transcripts, `agy` credits, the `cloudcode-pa.googleapis.com` internal RPCs, OS keyrings.

Session-level token/cost (Pi RPC, Claude json `total_cost_usd`, agy result `usage`) may be shown only if labeled **session usage**, never as remaining subscription.
