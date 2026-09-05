# Architecture

Pi Command Center is a single-user, single-node proof of concept: a Blazor command center plus an on-machine node that runs Pi, Claude Code, and Antigravity children against one canonical Git workspace per project. No Git worktrees.

## Component boundaries

| Component | Process | Authority |
|---|---|---|
| `PiCommandCenter.Web` | In-process with Control Plane | Blazor Interactive Server UI (fleet, project, request, `/attention`, `/usage`) |
| `PiCommandCenter.ControlPlane` | ASP.NET Core (`net10.0`) | Authoritative SQLite store; HTTP API; SignalR `/nodeHub`; migrations at startup |
| `PiCommandCenter.Application` / `Domain` / `Infrastructure` | Libraries | Domain model, use cases, EF Core |
| `PiCommandCenter.Contracts` | Library | SignalR DTOs only (`PiCommandCenter.Contracts.NodeTransport`) |
| `PiCommandCenter.Node` | Separate `net10.0` worker | Claims work, launches runtimes, local event spool, reservation/mail gateways |
| `runtime/pi-worker` | Node.js ≥ 26 (`runtime/package.json`) | Pi SDK session; strict NDJSON protocol v1 on stdin/stdout |

Dependency direction: Domain → Application → Infrastructure/ControlPlane/Node. The TypeScript worker never imports C#; it speaks protocol v1 only.

## Data flow

```text
Browser (cookie admin)
    │  HTTPS / Blazor circuit / REST under /api
    ▼
Control Plane  (loopback default, SQLite WAL)
    ▲
    │  authenticated SignalR /nodeHub
    │
Node worker
    │  NDJSON protocolVersion 1 (1 MiB frames) ──► pi-worker (Pi SDK)
    │  official `claude` + host-owned --settings hooks
    │  official `agy` (read-only reviewer)
    ▼
Canonical project repository (supervisor-owned Git)
```

```text
Browser  GET /usage  (Blazor Interactive Server; manual Refresh only, no polling)
    │  INodeSubscriptionUsageGateway.GetAsync(nodeId)
    ▼
Control Plane  NodeSubscriptionUsageGateway
    │  SignalR Clients.Client(connectionId).InvokeCoreAsync
    │  method GetSubscriptionUsage  (no arguments)
    ▼
Node worker  IRuntimeSubscriptionUsageProbe.GetAsync
    ├─ Pi           IProviderSubscriptionQuotaReader.ReadPiAsync
    │                node-local auth.json ──► GET https://chatgpt.com/backend-api/wham/usage  (private)
    ├─ Claude Code  claude --version; claude auth status; ReadClaudeAsync
    │                node-local .credentials.json ──► GET https://api.anthropic.com/api/oauth/usage  (private)
    └─ Antigravity  agy --version; agy -p /usage --print-timeout 8s  (text report, pinned TSV grammar)
Only normalized windows cross SignalR. Credentials never leave the node.
```

1. Operator registers projects whose paths sit under `Projects:ApprovedRoots` (default `~/Developer`; `~` expands to the user home).
2. A work request is enqueued (`POST /api/projects/{projectId}/requests`).
3. The node `ClaimNext`s via `/nodeHub`, starts a root Pi session, and publishes normalized events (`PublishEvents`).
4. The root delegates children; write-capable children acquire leases before mutating files.
5. Completion is an objective gate (`EvaluateCompletion`); the UI reads persisted result, verification runs, and events.

## Persistence

- Control-plane connection string: `ConnectionStrings:ControlPlane` (default `Data Source=controlplane.db;Cache=Shared` in `src/PiCommandCenter.ControlPlane/appsettings.json`).
- Startup applies EF Core migrations (`Program` logs “Applying control-plane database migrations”).
- Node event spool: `Node:EventSpoolPath` default `~/.local/share/pi-command-center/node-spool.db`.
- Application data directory used by setup/demo: `~/.local/share/pi-command-center` (`PI_CC_DATA`), mode `0700`.

## Status model (SPEC §21)

Four independent dimensions on `AgentSession` (never inferred from silence; `Idle` only from `turn.completed` or `session.snapshot`):

| Dimension | Values |
|---|---|
| Liveness | `Starting`, `Online`, `Disconnected`, `Exited` |
| Activity | `Idle`, `Planning`, `Reasoning`, `Responding`, `RunningTool`, `WaitingForReservation`, `WaitingForChild`, `WaitingForMessage`, `Reviewing`, `Verifying`, `Finalizing` |
| Attention | `None`, `InputRequired`, `ApprovalRequired`, `ReservationConflict`, `Warning`, `Error` |
| Work state | `Queued`, `Starting`, `Planning`, `Executing`, `Reviewing`, `Verifying`, `Blocked`, `Completed`, `Failed`, `Cancelled` |

User-facing projection precedence for a session: failed/cancelled → disconnected → blocked (attention or work state) → active work → idle.

Provider authentication missing is **not** a generic crash. Adapters emit `session.snapshot` or `session.failed`-equivalent facts with `Attention=InputRequired` and `WorkState=Blocked` and a reason that names the **provider-native local login** (`claude` / `agy`). The UI Attention inbox (`/attention`) surfaces `InputRequired` as human input.

## Recovery

| Failure | Behavior |
|---|---|
| Control Plane restart | SQLite history survives. Node reconnects with exponential backoff (cap 30s) and re-registers. Unacked spool events replay; `PublishEvents` is idempotent on `EventId`. |
| Missed heartbeats | `NodeLivenessService` marks a node offline after **three** missed `Node:HeartbeatSeconds` intervals (default 10s). |
| Runtime process crash | Capture exit code and stderr tail; emit `session.failed`; `RuntimeCrashRecovery` marks owned **Active** leases `RecoveryRequired` (`reservation.recovery_required`) and does **not** release them. |
| Stale lease | Not reusable until recovery inspection or human force-release (`POST /api/reservations/{leaseId}/force-release` with `confirm=true`, reason, and repository status snapshot). Force-release rotates the fencing token. |
| Reservation service unavailable | Mutations fail closed. |
| Malformed NDJSON | Logged and skipped; the protocol stream stays up. |

## Runtime profiles

Allowlist (`Pi:AllowedRuntimeProfiles` / `AgentRuntimeProfiles`):

- `local-pi`
- `claude-readonly`
- `claude-reserved-write`
- `antigravity-readonly`


Claude hooks and `--settings` live under `$XDG_DATA_HOME/pi-command-center/claude-runtime/<session>/` (owner-only). The hook validator is loopback HTTP only (`http://127.0.0.1:<ephemeral>/pcc-claude-hook/`). Antigravity is read-only in this PoC.

## Subscription usage (`/usage`)

Operator page for **normalized** remaining subscription windows per provider on the connected node. Windows come from the same **private first-party surfaces the official clients use** — not from public product APIs. The operator accepted that risk; the trade-off is that any of these surfaces can change or disappear without notice, in which case the page shows `unavailable`/`error` and never a guessed number. Refresh is **manual only** (the page's Refresh button); nothing polls in the background.

Research and the public-vs-private classification: [research/subscription-usage.md](research/subscription-usage.md).

### Flow

1. Browser loads `/usage` (cookie admin, Interactive Server) and presses Refresh.
2. Control plane `INodeSubscriptionUsageGateway` (`PiCommandCenter.ControlPlane.SubscriptionUsage.NodeSubscriptionUsageGateway`) looks up the node's SignalR connection and invokes the **client callback** `GetSubscriptionUsage` (empty argument list). Hub round-trip timeout is 35 s.
3. The node handler calls `PiCommandCenter.Node.SubscriptionUsage.IRuntimeSubscriptionUsageProbe.GetAsync`, which reads Pi and Claude quota through `IProviderSubscriptionQuotaReader` (`ReadPiAsync` / `ReadClaudeAsync`) and runs the Antigravity CLI, then returns `NodeSubscriptionUsageMessage`.

### Sources per provider

| Provider | Credential (node-local, never transmitted except to the listed origin) | Quota surface | Classification |
|---|---|---|---|
| Pi (`local-pi`, `openai-codex` OAuth) | `SubscriptionUsage:PiCredentialPath` (default `~/.pi/agent/auth.json`; empty string disables), key `openai-codex` | `GET https://chatgpt.com/backend-api/wham/usage` with `Authorization: Bearer` + `ChatGPT-Account-Id`. Refresh only when the access token is within 60 s of expiry: `POST https://auth.openai.com/oauth/token` (form-encoded, client `app_EMoamEEZ73f0CkXaXp7hrann`); the rotated `access`/`refresh`/`expires`/`accountId` are committed back into the same file under Pi's own lock (see [Bounds](#bounds)). `Source`: `chatgpt.com/backend-api/wham/usage`. | **Private** ChatGPT backend endpoint used by the official Codex CLI. Pi itself never calls it; Pi has no quota API. |
| Claude Code | `claude --version`, `claude auth status` (login presence, plan label), then `SubscriptionUsage:ClaudeCredentialPath` (default `~/.claude/.credentials.json`; empty string disables), object `claudeAiOauth` | `GET https://api.anthropic.com/api/oauth/usage` with `Authorization: Bearer` + `anthropic-beta: oauth-2025-04-20`. On expiry or 401: `POST https://platform.claude.com/v1/oauth/token` (JSON, client `9d1c250a-e61b-44d9-88ed-5944d1962f5e`), then exactly one retry; rotated `accessToken`/`refreshToken`/`expiresAt` committed back with a compare-and-swap on the prior `refreshToken`. The refresh response may omit `refresh_token`; the prior one is then kept. `Source`: `claude --version; claude auth status; api.anthropic.com/api/oauth/usage`. | **Private** CLI endpoint; not in Anthropic's public API docs. `claude auth status` is the only public command and does not refresh tokens. |
| Antigravity | `agy`'s own credential store (no file is parsed by DevFleet) | `agy --version`, then `agy -p /usage --print-timeout 8s`; stdout parsed as strict TSV: `group ⇥ Weekly Limit Remaining\|Five Hour Limit Remaining ⇥ NN% ⇥ RFC3339`. | **Documented** headless slash command, but its output is a human text report with **no published schema**; the grammar is pinned to agy 1.1.27. Not documented as quota-free, hence manual refresh only. |

`Source` on each row is a stable, secret-free label naming the commands/origins used (e.g. `agy --version; agy -p /usage --print-timeout 8s`). Version/auth/plan fields are informational and are never treated as remaining quota.

### Window mapping (no inference)

- Pi: upstream `rate_limit.primary_window` / `secondary_window` `{used_percent, limit_window_seconds, reset_at}` → `PercentUsed = used_percent`, `PercentRemaining = 100 - used_percent`, `ResetsAt` from the Unix-seconds `reset_at`. Aggregate names from `limit_window_seconds`: 18000 → `five-hour`, 604800 → `weekly`, other exact hour/day multiples → `N-hour`/`N-day`, otherwise `primary`/`secondary`. Optional/null `additional_rate_limits[]` is parsed (not dropped): each entry needs a bounded safe `limit_name`, optional/null `rate_limit`, and its primary/secondary windows; display names are `{limit_name} {duration}` (e.g. `GPT-5.3-Codex-Spark five-hour`, `GPT-5.3-Codex-Spark weekly`). Spark is that OpenAI array — it is not inferred and is not a Claude field. A present malformed array/entry/window fails the provider. `credits` / `spend_control` without a plan-window percentage stay unrepresented. Live evidence (2026-09-05, sanitized): `limit_name` `GPT-5.3-Codex-Spark`, five-hour and weekly both **0% used**.
- Claude: upstream `five_hour` → `five-hour`, `seven_day` → `weekly`, `seven_day_opus` → `weekly opus`, `seven_day_sonnet` → `weekly sonnet`, `seven_day_oauth_apps` → `weekly oauth-apps`, `cinder_cove` → `cowork credit`; `utilization` is already in percentage points (0–100, e.g. `36.0`) and maps directly (`PercentUsed = utilization`, no rescaling); `PercentRemaining = 100 - PercentUsed`; `ResetsAt` from `resets_at` when reported. Optional/null `limits[]`: only `kind=weekly_scoped` with `scope.model.display_name` becomes `weekly {display_name}` (provider capitalization preserved, e.g. `weekly Fable`); `PercentUsed` is that row's `percent`; unscoped session/weekly rows duplicate the roots and are skipped; unknown future kinds are ignored. A present malformed array/entry or malformed recognized scoped row fails the provider. Fable comes from Claude `limits` `weekly_scoped` — it is not inferred and is not an OpenAI field. `extra_usage` and other credit/spend balances without a percentage window stay unrepresented. Live evidence (2026-09-05, sanitized): `display_name` `Fable` at **14% used**.
- Antigravity: `NN%` remaining → `PercentRemaining = NN`, `PercentUsed = 100 - NN`; names are `<group> weekly` / `<group> five-hour`; duplicates rejected.
- Every provider is capped at **8** windows end to end (`RuntimeSubscriptionUsageProbe.MaxWindows`): a ninth window is schema drift and invalidates the whole provider row, on the node and again on the page.
- A percentage that is not finite, outside 0–100, or whose used/remaining pair does not sum to 100, or a reset time that is not RFC3339/epoch, invalidates the **whole** provider row. Nothing is estimated, extrapolated, or carried over from a previous reading. Percentage quota windows are the only rows on `/usage`; monetary balances without a percentage are out of scope.

### Fail closed

- `available` **requires** at least one coherent window. Empty `Windows` with `available` is invalid.
- `unavailable`: `credential_missing` (file or OAuth entry absent, or the path is configured empty), `quota_not_reported` (200 but no rate-limit windows in the body), `signed_out` (`claude auth status` reports not logged in; `Authenticated=false`, no credential read, no HTTP), CLI missing or unconfigured (`process_missing`).
- `error`: `credential_unreadable` (not a regular file — symlinks and FIFOs included —, group- or world-accessible, > 256 KiB, I/O failure; checked before any network call), `credential_malformed`, `credential_expired` (expired with no refresh token), `refresh_failed`, `credential_persist_failed` (refresh succeeded but the rotated tokens could not be committed — lock not acquired in time, temp file not creatable, or rename refused, e.g. read-only mount; the refresh response is discarded and usage is not read), `http_unauthorized` (401/403 after the single retry), `http_rate_limited` (429), `http_failed` (other non-2xx, including 3xx since redirects are refused), `http_timeout`, `http_oversized`, `http_malformed` (JSON/schema drift); CLI `process_timeout`, `process_failed`, `process_truncated`, `process_malformed`.
- Diagnostics are **stable identifiers only** (the list above). Provider response bodies, provider error bodies, tokens, account IDs, user IDs, and e-mail/org names are never logged, never placed in `Diagnostic`, and never sent to the control plane.

### Bounds

- HTTP: credentials are attached only to the **exact HTTPS origins** above; any other host or scheme is refused and redirects are not followed. **10 s** timeout, **64 KiB** response cap.
- Credential files: regular files ≤ **256 KiB**, owner-only mode; parsed strictly; unrelated JSON keys are preserved when a rotated token is persisted. Persistence is **atomic only**: `0600` temp file in the same directory → fsync → rename → directory fsync. There is no in-place overwrite path; if the rename cannot happen the read closes with `credential_persist_failed`. Each provider's commit is compatible with the official client's own writer so the two never clobber each other:
  - Pi: acquire the `proper-lockfile` directory lock `auth.json.lock` next to the file (bounded wait ≈ 5 s with backoff; a lock older than 30 s is treated as stale, matching Pi's `stale: 30_000`), re-read the latest document, compare-and-swap on the `openai-codex.refresh` value that was exchanged, merge only `access`/`refresh`/`expires`/`accountId`, commit, release.
  - Claude: the CLI uses no lock, so neither does the node; re-read immediately before the rename, compare-and-swap on `claudeAiOauth.refreshToken`, merge only `accessToken`/`refreshToken`/`expiresAt`, commit.
  - A CAS mismatch means the official client rotated first: the node's refresh response is discarded, the file is reloaded once, and the read proceeds from the newer credential (no diagnostic unless that path itself fails).
- Process runner: `ProcessStartInfo.ArgumentList` only (**no shell**), stdin closed, **10 s** wall timeout for every CLI call, **16 KiB** stdout/stderr capture, process-tree kill on timeout. `agy -p /usage` carries its own `--print-timeout 8s` so `agy` gives up before the runner does and the result is `process_failed`/`process_malformed` rather than a tree kill.

### Deployment (`compose.yaml`)

Only the `node` service sees provider credentials; the control plane and the browser never do.

| Host path | Container path | Why |
|---|---|---|
| `${HOME}/.pi/agent` | `/provider-auth/pi` (`rw`) | Pi OAuth read + token rotation; `SubscriptionUsage__PiCredentialPath=/provider-auth/pi/auth.json`. The **containing directory** is mounted, not the file: the `proper-lockfile` lock is the sibling directory `auth.json.lock`, and the atomic commit needs to create a temp file and rename over `auth.json` in that directory. A single-file bind mount pins the inode, so rename fails with `EBUSY` and would force a crash-unsafe in-place write; that mode is not supported. The mount is deliberately **not** the container's Pi data directory (`Pi__AgentDataDirectory=/data/pi-agent`), so the in-container Pi worker keeps its own store. |
| `${HOME}/.claude` | `/home/node/.claude` (`rw`) | Already mounted for the Claude runtime; `SubscriptionUsage__ClaudeCredentialPath=/home/node/.claude/.credentials.json`. Same directory-mount reasoning: the CLI and the node both rename over `.credentials.json`. |
| `${HOME}/.gemini` | `/home/node/.gemini` (`rw`) | `agy` on Linux stores its OAuth token in the Secret Service keyring over D-Bus **or**, when no session bus is present, in `~/.gemini/antigravity-cli/`. The container has no session bus, so `agy` uses the file store. Do **not** mount `/run/user/<uid>/bus` or set `DBUS_SESSION_BUS_ADDRESS`: that keeps keyring mode on against a session-locked keyring and forces a browser login. |

#### Trust boundary inside the node

The node container is one trust domain: the operator's own subscriptions, mounted read-write, on a machine they own. Mounting `~/.pi/agent` exposes the **whole host Pi agent directory** (sessions, settings, `auth.json`) to the node process, not just the credential; the same is already true of `~/.claude`. That exposure is accepted for the trusted supervisor, and is what lets the node rotate tokens without corrupting the host CLIs' stores. It is **not** extended to model-driven subprocesses:

- **Pi worker** (`runtime/pi-worker`): its SDK store is `Pi__AgentDataDirectory` (`/data/pi-agent`), never `/provider-auth`. Root and child sessions run with **no built-in tools**; `read`/`grep`/`find`/`ls` are node-owned custom tools that round-trip to the node and resolve through `RepositoryPathPolicy` (repository-relative, no symlink escape), so a prompt cannot name `/provider-auth/pi/auth.json` or `/home/node/.claude/.credentials.json`.
- **Claude Code**: the CLI must read its own `~/.claude/.credentials.json` to run at all. Model tools are limited by the host-owned `--settings` allowlist (`Read`/`Glob`/`Grep`, plus `Edit`/`Write` only under a lease) and the PreToolUse hook denies any inspect path outside the repository; `Bash` is not allowed, so the model cannot open `/provider-auth` either.
- **Antigravity**: `AntigravityReadOnlySandbox` (bwrap) binds the host root read-only and then mounts private empty `tmpfs` over `/provider-auth` and `/home/node/.claude` — emitted last so no later bind can shadow them — while `/home/node/.gemini` stays visible for `agy`'s own login. A mask location that exists but is not a directory, or a repository path that falls inside a masked location, makes sandbox setup throw and the session fail closed.
- **Quota reader**: `ProviderSubscriptionQuotaReader` in the node process is the only code that opens `/provider-auth/pi/auth.json` or `.credentials.json` for their contents.

### Normalized DTO (`PiCommandCenter.Contracts.NodeTransport`)

- `NodeSubscriptionUsageMessage`: `NodeId`, `Providers`.
- `ProviderSubscriptionUsageMessage`: `Provider`, `RuntimeProfiles`, `Status` (`available` \| `unavailable` \| `error`), optional `Authenticated` / `PlanLabel` / `Version`, `Windows`, `ObservedAt`, `Source`, `Diagnostic`.
- `SubscriptionUsageWindowMessage`: `Name`, `PercentUsed` and/or `PercentRemaining`, optional `ResetsAt`.
- Node-internal: `SubscriptionQuotaReadStatus { Available, Unavailable, Error }`, `ProviderSubscriptionQuotaReadResult(Status, Windows, Source, Diagnostic, Authenticated, PlanLabel)`, `IProviderSubscriptionQuotaReader.ReadPiAsync/ReadClaudeAsync`, and the `SubscriptionUsage` options section (`PiCredentialPath`, `ClaudeCredentialPath`).

## HTTP surface (cookie admin unless noted)

| Method | Path | Notes |
|---|---|---|
| GET | `/health` | Anonymous on loopback |
| GET/POST | `/api/projects` | List / register |
| GET | `/api/projects/{projectId}` | |
| POST | `/api/projects/{projectId}/validate` | |
| GET/POST | `/api/projects/{projectId}/requests` | List / enqueue |
| GET | `/api/requests/{requestId}/messages` | |
| POST | `/api/requests/{requestId}/messages` | |
| POST | `/api/requests/{requestId}/reply` | |
| POST | `/api/requests/{requestId}/guidance` | Human guidance |
| GET | `/api/requests/{requestId}/result` | |
| GET | `/api/requests/{requestId}/events` | |
| POST | `/api/sessions/{sessionId}/message` | |
| POST | `/api/sessions/{sessionId}/cancel` | |
| POST | `/api/messages/{messageId}/acknowledge` | |
| POST | `/api/reservations/{leaseId}/force-release` | Human-only |
| GET/POST | `/account/login` | Antiforgery form; fields `username`, `password`, `returnUrl` |
| POST | `/account/logout` | Antiforgery; `returnUrl` |
| GET | `/usage` | Blazor remaining subscription windows; manual Refresh only |
| Hub | `/nodeHub` | Node token policy only; never browsed |

Blazor pages: `/` (fleet), project and request routes, `/attention`, `/usage`, `/login`.

## Demonstration vs Definition of Done

SPEC §43 / §46 require the first demonstration **through the web UI** (login, project, enqueue the health-details request, watch the agent tree, reservations, verification, completion). Operator commands live in the README demo section (`scripts/demo.sh`).

- `scripts/demo.sh --smoke` (default quota-free path): starts Control Plane + Node on loopback `127.0.0.1:${PI_CC_PORT:-5057}`, may copy `demo/health-details-fixture` under the approved root, and may `POST /api/projects` for bootstrap. It does **not** launch Pi, `claude`, or `agy`, and HTTP register/enqueue is **not** request completion.
- Live providers are opt-in only: `RUN_REAL_PI_TESTS=1`, `RUN_REAL_CLAUDE_TESTS=1`, `RUN_REAL_ANTIGRAVITY_TESTS=1` on `dotnet test` (subscription quota). Completing SPEC §43 still means the browser, not curl.
- Blazor surfaces: `/` fleet, `/projects/{id}` queue and composer, request page (plan, diff, verification, result, reservations, mail), `/attention`, `/usage`.

