# Research: multi-subscription credentials (node-local enrollment)

**Date researched:** 2026-09-05  
**Status:** decision-grade feasibility / design (not implemented)  
**Constraint:** facts from official docs, official source, DevFleet repo symbols, and prior primary-source research notes. Private quota readers already shipped for `/usage` are **usage-only exceptions**; they do not justify a Control Plane token broker.

**Legend:** **FACT** = official docs, official source, or DevFleet code. **INFERENCE** = host design choice or security policy, not a provider requirement. **Private** = first-party surface not published as a product API.

Related notes: [subscription-usage.md](subscription-usage.md), [pi-sdk.md](pi-sdk.md), [claude-code.md](claude-code.md), [antigravity-cli.md](antigravity-cli.md), [architecture.md](../architecture.md), [security.md](../security.md), [protocols.md](../protocols.md).

---

## Direct answer

**Feasible now, if and only if credentials stay on the node that runs the CLI, enrollment is native (operator `/login` / `agy` sign-in on that machine), and the Control Plane stores only opaque `profileId`s plus node-local path/env pointers — never tokens.**

Reject a generic Control Plane **raw-token broker** (extract, store, relay, or refresh OAuth access/refresh tokens in SQLite / SignalR / Blazor). That pattern is not a documented cross-provider product API, collides with DevFleet’s existing “provider OAuth stays in official CLIs” policy ([security.md](../security.md)), and is the wrong inference from today’s private quota readers.

**Recommended trust boundary:** one operator-owned node; profiles are host-owned records `{profileId, provider, nodeId, isolationRoot}`; the node launches the official process with isolation env (`PI_CODING_AGENT_DIR` / `authPath` / `CLAUDE_CONFIG_DIR`) or the single Antigravity OS identity; quota/health is **profile-scoped** and fail-closed; work leases are **sticky and fenced** to one profile for the whole session — **no mid-session switching**.

Public remaining-quota APIs are still absent. The private Pi/Claude HTTPS readers and the pinned `agy -p /usage` TSV parser are accepted **only** to populate `/usage`. They must not become a reason to centralize custody or to pretend a public OAuth/quota API exists.

---

## Verdicts (corrected against ClaimsCrossCheck)

| Claim | Verdict | Kind |
|---|---|---|
| Pi: one `Credential` per `Provider.id` in a `CredentialStore`; simultaneous same-provider accounts need distinct stores/`authPath` (dedicated `agentDir` is sufficient) | Yes | **FACT** |
| Claude: `CLAUDE_CONFIG_DIR` isolates file credentials and macOS Keychain entries; documented for side-by-side accounts | Yes | **FACT** |
| Claude: `setup-token` / `CLAUDE_CODE_OAUTH_TOKEN` is a documented **CI injection** path (operator-generated, person-tied) | Yes | **FACT** |
| Pi SDK: `CreateModelRuntimeOptions.authPath` / injected `CredentialStore` is a documented isolation/injection path | Yes | **FACT** |
| Cross-provider Control Plane that completes native consumer login/refresh and withholds tokens | No public API | **FACT** |
| Antigravity: documented named multi-profile selector / `--profile` / config-root override | **Absent from supported interface** (not a proof of impossibility) | **FACT** |
| Public, stable JSON remaining-subscription API for Pi / Claude / Antigravity | None | **FACT** |
| Antigravity `agy -p /usage` | Documented **headless text report**, not TUI-only, **no published schema** | **FACT** |
| Anthropic / OpenAI: do not share credentials or make an account available to another person | Yes | **FACT** (terms) |
| Google: one-credential-per-node | Not stated; account security / no bypass of protections | **FACT** (terms) |
| Bind consumer credentials to one owner and the smallest node set; never expose to other operators | DevFleet policy | **INFERENCE** |
| Do not parse vendor caches or call private refresh from the Control Plane | DevFleet policy | **INFERENCE** |

---

## Provider matrix (what is implementable now)

### Pi (`@earendil-works/pi-coding-agent`)

| Capability | Public-supported | Private / unsupported | Implementable in DevFleet now |
|---|---|---|---|
| Isolate accounts | `ModelRuntime.create({ authPath, credentials })`; `createAgentSession({ agentDir })`; `getAgentDir()` / `PI_CODING_AGENT_DIR`; `getAuthPath()` = `join(getAgentDir(), "auth.json")` | Selecting among many OAuth slots **inside one** `auth.json` | Yes: map `profileId` → dedicated `agentDir` (0600 `auth.json`); worker already sends `agentDir` on `session.start` |
| Runtime overlay | `setRuntimeApiKey` / `removeRuntimeApiKey` — **API key only**, not persisted | Not a second Codex/Claude OAuth profile | API-key profiles only |
| Login | Interactive `/login`; `login(providerId, type, interaction)` overwrites the single slot | Host-owned OAuth broker | Operator `pi` login **in that agentDir** |
| Refresh | Official OAuth refresh inside `CredentialStore.modify`; Codex **rotates refresh tokens** — exclusive owner of each `auth.json` | Sharing one Codex `auth.json` across machines/processes | One live Pi worker (or serialized `modify`) per Codex profile |
| Quota | `pi auth check --json --no-refresh` (login readiness). RPC `get_session_stats` = **session** cost/context, not plan remaining | Private `GET https://chatgpt.com/backend-api/wham/usage` (shipped on node for `/usage`) | Health: `auth check`. Remaining windows: existing node reader only, **per isolated auth.json**, never on CP |
| Never | — | `pi auth print-api-key`, `print-bearer-token`, `--credentials`; shipping `auth.json` over SignalR | Enforce |

**Provider ids (FACT):** `openai` (API key) ≠ `openai-codex` (ChatGPT Plus/Pro OAuth). Anthropic subscription in Pi is extra-usage per token, not Claude plan bars ([providers.md](https://github.com/earendil-works/pi-mono/blob/main/packages/coding-agent/docs/providers.md)).

**Current DevFleet seam:** `PiWorkerOptions.AgentDataDirectory` default `~/.local/share/pi-command-center/pi-agent` (`src/PiCommandCenter.Node/PiWorkerOptions.cs`). `PiRuntimeAdapter` launches one worker per session; `PiWorkerSession` `session.start` payload includes `["agentDir"] = agentDataDirectory`. Quota: `SubscriptionUsageOptions.PiCredentialPath` default `~/.pi/agent/auth.json` — **today this is a single default home, not profile-scoped**, and it is a **different path** than `AgentDataDirectory`. `IProviderSubscriptionQuotaReader.ReadPiAsync` / `ProviderSubscriptionQuotaReader.PiSource`.

### Claude Code (`claude`)

| Capability | Public-supported | Private / unsupported | Implementable now |
|---|---|---|---|
| Isolate Pro/Max/Team logins | **`CLAUDE_CONFIG_DIR`** — “useful for running multiple accounts side by side”; `.credentials.json` / Keychain keyed to that dir | Documented **consumption schema** of `.credentials.json` | Yes: `profileId` → config dir; `claude` process env; do not parse the file for routing |
| CI token | `claude setup-token` prints 1-year OAuth; set **`CLAUDE_CODE_OAUTH_TOKEN`**. Org GitHub Actions docs: prefer Console **API key** for shared secrets because OAuth is tied to the generating person | Using that token as a **pooled fleet secret** | Explicit CI path only, operator-placed env on the node, not CP-minted |
| Enterprise / API / cloud | Listed order is **non-gateway** precedence: cloud flags → `ANTHROPIC_AUTH_TOKEN` → `ANTHROPIC_API_KEY` → `apiKeyHelper` → `CLAUDE_CODE_OAUTH_TOKEN` → Console profile/WIF → `/login` OAuth. A **signed-in Claude apps gateway session outranks** cloud flags and those other env/profile/login sources. `--console` login. Bedrock/Vertex/Foundry. Claude apps gateway | CP completing browser OAuth while withholding the token | Fleet/CI should prefer API/cloud/gateway; subscription OAuth stays node-local `/login` |
| Headless | `claude -p` uses `/login` OAuth **only when no higher-precedence credential is present**. `ANTHROPIC_API_KEY` **always wins when set**. `--bare` skips OAuth/keychain **and** `CLAUDE_CODE_OAUTH_TOKEN` | `--bare` for subscription remaining | Do **not** use `--bare` for subscription profiles. Watch `--bare` becoming default for `-p` |
| Auth probe | `claude auth status` JSON; exit 0/1 | `--text` may include email/org | Yes, redacted |
| Quota | Interactive `/usage` TUI; no `claude usage --json` | Private `GET https://api.anthropic.com/api/oauth/usage` (shipped on node) | Health: `auth status`. Remaining: existing reader **with `ClaudeCredentialPath` under that `CLAUDE_CONFIG_DIR`** |

**Current DevFleet seam:** `ClaudeCodeRuntimeAdapter` launches official `claude -p … --settings <trusted> --setting-sources ""` and **never inspects credentials** (`src/PiCommandCenter.Node/Runtime/Claude/ClaudeCodeRuntimeAdapter.cs`). Hooks/settings under `$XDG_DATA_HOME/pi-command-center/claude-runtime/<session>/`. **No `CLAUDE_CONFIG_DIR` today.** Quota: `SubscriptionUsageOptions.ClaudeCredentialPath` default `~/.claude/.credentials.json`; `ReadClaudeAsync`; compose mounts `${HOME}/.claude`. Missing auth → `Attention=InputRequired`, reason names **Claude Code native login** ([security.md](../security.md)).

### Google Antigravity (`agy` 1.1.27)

| Capability | Public-supported | Absent / private | Implementable now |
|---|---|---|---|
| Identity | One OS-keyring token profile; silent keyring else browser; SSH URL+code | **No** `--profile`, `--config`, documented HOME/XDG auth override, named-account list/select | **One Google identity per OS user/node.** Do not invent HOME-profile switching as a supported API |
| Config tree | `~/.gemini/antigravity-cli/` settings, keybindings, `cli.log` | Multi-subscription selector | Single `antigravity-readonly` profile per node |
| Non-subscription | `modelProvider: "gemini"` + `GEMINI_API_KEY` (env only). Enterprise: `gcloud auth application-default login` + `AGY_ADC_AUTH=true` | Treating API key / ADC as Pro/Ultra stacking (plans.md: no BYOK for extra consumer rate limits) | Separate **API/cloud** profiles, not consumer-plan pooling |
| Quota | TUI `/usage` `/quota` `/credits`; headless **`agy -p /usage`** = **text report** (not stream-json) | Published JSON schema; private `cloudcode-pa.googleapis.com` retrieveUserQuota* | Pinned TSV parser already on node (agy 1.1.27); collected by the five-minute node cache; DevFleet never parses the oauth file |

**Current DevFleet seam:** official `agy` read-only reviewer; Bubblewrap host-root read-only. Usage: `RuntimeSubscriptionUsageProbe` runs `agy --version` then `agy -p /usage --print-timeout 30s`. Compose mounts `${HOME}/.gemini`; **do not** mount session D-Bus ([architecture.md](../architecture.md)). Missing auth → native **agy login**.

---

## Current DevFleet credential / usage seam (as shipped)

```text
Browser  GET /usage  (load or manual Refresh)
    → Control Plane  NodeSubscriptionUsageGateway
    → SignalR client callback GetSubscriptionUsage()   // no args, 35s
    → Node in-memory subscription-usage cache
         └─ latest successful NodeSubscriptionUsageMessage

Node cache worker  (immediate startup collection, then every five minutes)
    → IRuntimeSubscriptionUsageProbe.GetAsync
         ├─ Pi ModelRuntime usage sidecar
         ├─ Anthropic OAuth usage supplement
         └─ agy -p /usage  (pinned TSV)
Only normalized windows cross the hub. Credentials never leave the node.
```

Symbols:

- `PiCommandCenter.ControlPlane.SubscriptionUsage.NodeSubscriptionUsageGateway`
- `PiCommandCenter.Contracts.NodeTransport`: `NodeSubscriptionUsageMessage`, `ProviderSubscriptionUsageMessage`, `SubscriptionUsageWindowMessage`
- Node: `IRuntimeSubscriptionUsageProbe`, `IProviderSubscriptionQuotaReader`, `ProviderSubscriptionQuotaReader`, `SubscriptionUsageOptions`
- Hub: `GetSubscriptionUsage` is a **client callback**, not a hub method ([protocols.md](../protocols.md))

**Accepted usage-only exceptions (do not expand):**

1. Node reads Pi `auth.json` / Claude `.credentials.json` **only** to call exact HTTPS origins and persist rotated tokens `0600`.
2. Node parses **agy stdout TSV** pinned to 1.1.27 — not `agy`’s credential file.
3. Fail closed; no last-known snapshot; no bodies/tokens in `Diagnostic` / logs / SQLite.

These exceptions **do not** authorize Control Plane custody, SignalR token relay, browser paste of refresh tokens, or treating private endpoints as public OAuth APIs.

---

## Recommended architecture (node-local native enrollment)

### Trust boundary

| Layer | May hold | Must not hold |
|---|---|---|
| Browser / Blazor | Opaque `profileId`, plan label, normalized windows, enrollment UX that **names the native CLI** | Tokens, `auth.json`, `.credentials.json`, keyring secrets |
| Control Plane / SQLite | `profileId`, `provider`, `nodeId`, isolation root **path string**, kind (`subscription` \| `api_key` \| `cloud`), sticky lease binding | Access/refresh tokens, `CLAUDE_CODE_OAUTH_TOKEN` values, API keys (unless the operator later opts into a documented env-file **on the node**, still not in SQLite) |
| Node | Isolation directories, process env, existing quota-reader files **on that node** | Forwarding secrets on `PublishEvents` / mail / usage DTO |
| Child CLIs | Their native stores | Host-parsed second copies of the same Codex refresh token |

### Schema (host-owned; not an upstream type)

```text
CredentialProfile
  ProfileId          // opaque, not email, not token prefix
  Provider           // pi | claude-code | antigravity
  Kind               // subscription_oauth | api_key | cloud_iam
  NodeId             // enrollment + refresh owner
  Isolation
    Pi:        AgentDir / authPath          // PI_CODING_AGENT_DIR
    Claude:    ClaudeConfigDir              // CLAUDE_CONFIG_DIR
    Antigravity: none (OS user identity) or explicit API/ADC flags
  DisplayLabel       // operator-chosen, non-secret
  CooldownUntil?
  Health             // from last probe: available|unavailable|error + diagnostic class
```

`GetSubscriptionUsage` should grow **profile-scoped** rows (`profileId` on `ProviderSubscriptionUsageMessage` or a new list). Until then, `/usage` is one default identity per provider.

### Routing / leases

1. **Deterministic selection:** walk the existing node-owned `PiWorkerOptions.RoleRoutes[role]` list. For the required provider, choose a compatible profile by **provider-native auth readiness** and stable `profileId` (lowest id on ties). Do **not** treat `/usage` remaining windows as a scheduler SLA.
2. **Sticky fenced lease:** bind `sessionId` → `profileId` at `StartAsync`. Reservation fencing tokens already exist (`invalid_fencing_token`). Extend the same idea: the runtime adapter receives the isolation root at start and **never** changes it on `SendAsync` / child spawn.
3. **No mid-session switching** if a window exhausts: fail/block (`Attention=InputRequired` or `WorkState=Blocked`) or finish the session; enqueue a **new** session on another profile.
4. **Cooldowns:** `/usage` status/windows and private-probe `http_rate_limited` are **display-only**. Only a rate-limit failure from the **launched runtime** may put that profile in cooldown. Do not interpolate remaining %.
5. **Codex exclusivity:** at most one live process refreshing a given Pi `auth.json`.
6. **Antigravity:** at most one consumer subscription profile per node OS user; additional capacity is API/ADC, separately labeled.

### Enrollment UX

- Surface names **provider-native local login** (`pi` in that `agentDir`, `claude` with that `CLAUDE_CONFIG_DIR`, `agy` on the node) — already the missing-auth pattern.
- SSH: provider paste-code / device-code on the **node TTY**, not a CP OAuth callback that captures tokens.
- CI provider credentials are **outside this design**. DevFleet must **not** collect or write OAuth/API secrets into Command Center config (including `$PI_CC_DATA`).

---

## Terms vs DevFleet security policy

**FACT (provider terms):**

- Anthropic Consumer Terms (Claude.ai / Pro / Max, effective 2025-10-08): do not share Account login, API key, or credentials; do not make the Account available to anyone else; automated/non-human access restricted except via Anthropic API key or explicit permission. https://www.anthropic.com/legal/consumer-terms
- OpenAI Terms of Use: do not share credentials or make an account available to others. https://openai.com/policies/terms-of-use/
- Google Terms + Antigravity additional terms: keep the account secure; do not bypass protective measures. They do **not** state “one node per credential.” https://policies.google.com/terms https://antigravity.google/terms
- Anthropic GitHub Actions docs: OAuth from `setup-token` is tied to the person who generated it; prefer Console API key for org-shared secrets. https://code.claude.com/docs/en/github-actions
- OpenAI Codex: refresh-token rotation; sharing `auth.json` invalidates the loser (pi-mono `issue-analysis.yml`).

**INFERENCE (DevFleet policy, not a universal ToS quote):**

- Single local administrator PoC; no multi-user tenancy ([security.md](../security.md)).
- Consumer subscription identities: one human owner; smallest node set (this workstation / this node container); never expose tokens to another operator or to agents.
- Do not pool one Pro/Max/ChatGPT/Google AI login across a fleet as if it were an org API key.
- Prefer Console/API/WIF/Bedrock/Vertex/ADC/gateway for anything that is actually multi-node automation.
- Private quota HTTP and pinned TSV remain node-local, origin-pinned, fail-closed.

---

## Phased implementation

**Phase 0 — policy lock (docs only):** this note. No CP token columns.

**Phase 1 — Claude isolation (highest public support):** node process env `CLAUDE_CONFIG_DIR` per `profileId`; enrollment = `claude auth login` in that dir; `claude auth status`; usage reader `ClaudeCredentialPath = $CLAUDE_CONFIG_DIR/.credentials.json`. Keep `--settings` / `--setting-sources ""` as today.

**Phase 2 — Pi isolation:** `session.start` `agentDir` (already present) per `profileId`; `PI_CODING_AGENT_DIR`; exclusive refresh; `pi auth check --json --no-refresh`. Keep **`SubscriptionUsage:PiCredentialPath` and `Pi:AgentDataDirectory` deliberately separate**. Mapping `/usage` onto a profile-scoped `auth.json` needs an **explicit boundary revision** and serialized refresh ownership — not an implicit merge of those two settings.

**Phase 3 — Antigravity:** model as **one** consumer identity per node; optional separate API/ADC “profiles” that are not subscriptions. Keep pinned `/usage` TSV. No fake `--profile`.

**Phase 4 — routing:** profile-scoped usage DTO; sticky session→profile; cooldowns; deterministic pick. Still no mid-session switch.

**Out of scope / refuse:** CP OAuth broker; copying Keychain / `.credentials.json` / `auth.json` / `antigravity-oauth-token` over the hub; polling private usage on a timer; `print-bearer-token` / `setup-token` in product UX; `--bare` for subscription remaining; scraping TUIs.

---

## Unknowns

- Whether concurrent `CLAUDE_CONFIG_DIR` processes for the **same** login share one rate-limit identity (**INFERENCE:** likely yes).
- Whether `setup-token` is revoked on `/logout`.
- Date `--bare` becomes default for `claude -p`.
- Team/Enterprise concurrent-device policy beyond the consumer sharing clause.
- Whether isolating `HOME` for `agy` isolates keyring (undocumented; keyring name is OS-global “Antigravity CLI”).
- Concurrent `agy` processes vs one quota pool.
- Non-interactive `/logout` for agy.
- Exact `.credentials.json` application schema (private; quota reader already pins `claudeAiOauth` fields for usage only).
- Stability of private wham/oauth/usage endpoints (already accepted for `/usage`).

---

## Primary-source URLs

| Topic | URL |
|---|---|
| Pi SDK auth / `authPath` / `setRuntimeApiKey` | https://github.com/earendil-works/pi-mono/blob/main/packages/coding-agent/docs/sdk.md |
| Pi providers / Codex vs Claude extra usage | https://github.com/earendil-works/pi-mono/blob/main/packages/coding-agent/docs/providers.md |
| Pi RPC session stats | https://github.com/earendil-works/pi-mono/blob/main/packages/coding-agent/docs/rpc.md |
| Pi `CredentialStore` | https://github.com/earendil-works/pi-mono/blob/main/packages/ai/src/auth/types.ts |
| Pi `getAgentDir` / `getAuthPath` | https://github.com/earendil-works/pi-mono/blob/main/packages/coding-agent/src/config.ts |
| Claude `CLAUDE_CONFIG_DIR` | https://code.claude.com/docs/en/env-vars |
| Claude authentication / storage / precedence / setup-token | https://code.claude.com/docs/en/authentication |
| Claude CLI auth commands | https://code.claude.com/docs/en/cli-reference |
| Claude headless / `--bare` | https://code.claude.com/docs/en/headless |
| Claude GitHub Actions secrets | https://code.claude.com/docs/en/github-actions |
| Claude costs / `/usage` TUI | https://code.claude.com/docs/en/costs |
| Anthropic consumer terms | https://www.anthropic.com/legal/consumer-terms |
| OpenAI terms | https://openai.com/policies/terms-of-use/ |
| Antigravity install / keyring / API key | https://antigravity.google/docs/cli/install |
| Antigravity headless `/usage` text | https://antigravity.google/docs/cli/headless |
| Antigravity `/usage` TUI | https://antigravity.google/docs/cli/commands/usage |
| Antigravity plans / no BYOK extra limits | https://antigravity.google/docs/plans |
| Antigravity enterprise ADC | https://antigravity.google/docs/enterprise |
| Antigravity terms | https://antigravity.google/terms |
| Google terms | https://policies.google.com/terms |

**Local CLI versions cited from [subscription-usage.md](subscription-usage.md):** `pi` 0.84.3 (research note; PiAuthProfiles also 0.85.1 source clone), `claude` 2.1.248, `agy` 1.1.27.
