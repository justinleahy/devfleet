# Architecture

Pi Command Center is a single-user, single-node proof of concept: a Blazor command center plus an on-machine node that runs Pi, Claude Code, Antigravity, and Muse Code children against one canonical Git workspace per project. No Git worktrees.

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
    │  official `muse serve` (read-only MSP v1 JSON-RPC over stdio)
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
    ├─ Pi sidecar  node /app/runtime/pi-worker/src/usage.ts
    │              (openai-codex, anthropic, kimi-code, zai, xai-oauth, opencode-go)
    ├─ Anthropic   owner-only .credentials.json ──► https://api.anthropic.com
    └─ Antigravity official agy -p /usage --print-timeout 8s
All three start concurrently; supplements replace the same provider id or append
in registration order. Only normalized windows cross SignalR.
```
```text
Node worker  INodeSystemResourceMonitor.Capture()  (on existing HeartbeatSeconds tick only)
    │  NodeResourceSnapshotMessage (nullable fields; first CPU sample null)
    ▼
Control Plane  NodeHub.Heartbeat(NodeHeartbeatMessage)
    │  NodeRegistry serializes latest snapshot onto FleetNode.ResourceSnapshotJson
    │  IProjectionNotifier.Publish(ProjectionChange.Fleet())
    ▼
Browser  GET /  (fleet; NodeDto.Resources)
Null fields render as Unavailable, never as zero.
```

1. Operator registers projects whose paths sit under `Projects:ApprovedRoots` (default `~/Developer`; `~` expands to the user home).
2. A work request is enqueued (`POST /api/projects/{projectId}/requests`).
3. The node `ClaimNext`s via `/nodeHub`, starts a root Pi session, and publishes normalized events (`PublishEvents`).
4. The root delegates children; write-capable children acquire leases before mutating files.
5. Completion is an objective gate (`EvaluateCompletion`); the UI reads persisted result, verification runs, and events.

6. On each existing node heartbeat (`Node:HeartbeatSeconds`, default 10s) `NodeSystemResourceMonitor` samples CPU, memory, disk, 1-minute load, and uptime once, attaches `NodeResourceSnapshotMessage` to `NodeHeartbeatMessage`, and the control plane stores **only that latest** snapshot. The Fleet page (`/`) refreshes through `ProjectionChange.Fleet()`. Sampling, sources, and fail-closed rules: [research/node-system-resource-monitoring.md](research/node-system-resource-monitoring.md).

## Persistence

- Control-plane connection string: `ConnectionStrings:ControlPlane` (default `Data Source=controlplane.db;Cache=Shared` in `src/PiCommandCenter.ControlPlane/appsettings.json`).
- Startup applies EF Core migrations (`Program` logs “Applying control-plane database migrations”).
- Node event spool: `Node:EventSpoolPath` default `~/.local/share/pi-command-center/node-spool.db`.
- Application data directory used by setup/demo: `~/.local/share/pi-command-center` (`PI_CC_DATA`), mode `0700`.
- Fleet node latest resources: `FleetNodes.ResourceSnapshotJson` (nullable JSON of `NodeResourceSnapshotDto`). Heartbeat **replaces** the column; there is no time series. A null `Resources` payload stores null (does not keep the previous snapshot).


## Status model (SPEC §21)

Four independent dimensions on `AgentSession` (never inferred from silence; `Idle` only from `turn.completed` or `session.snapshot`):

| Dimension | Values |
|---|---|
| Liveness | `Starting`, `Online`, `Disconnected`, `Exited` |
| Activity | `Idle`, `Planning`, `Reasoning`, `Responding`, `RunningTool`, `WaitingForReservation`, `WaitingForChild`, `WaitingForMessage`, `Reviewing`, `Verifying`, `Finalizing` |
| Attention | `None`, `InputRequired`, `ApprovalRequired`, `ReservationConflict`, `Warning`, `Error` |
| Work state | `Queued`, `Starting`, `Planning`, `Executing`, `Reviewing`, `Verifying`, `Blocked`, `Completed`, `Failed`, `Cancelled` |

User-facing projection precedence for a session: failed/cancelled → disconnected → blocked (attention or work state) → active work → idle.

Provider authentication missing is **not** a generic crash. Adapters emit `session.snapshot` or `session.failed`-equivalent facts with `Attention=InputRequired` and `WorkState=Blocked` and a reason that names the **provider-native local login** (`claude` / `agy` / `muse login`). The UI Attention inbox (`/attention`) surfaces `InputRequired` as human input.

## Recovery

| Failure | Behavior |
|---|---|
| Control Plane restart | SQLite history survives. Node reconnects with exponential backoff (cap 30s) and re-registers. Unacked spool events replay; `PublishEvents` is idempotent on `EventId`. |
| Missed heartbeats | `NodeLivenessService` marks a node offline after **three** missed `Node:HeartbeatSeconds` intervals (default 10s). |
| Runtime process crash | Capture exit code and stderr tail; emit `session.failed`; `RuntimeCrashRecovery` marks owned **Active** leases `RecoveryRequired` (`reservation.recovery_required`) and does **not** release them. |
| Stale lease | Not reusable until recovery inspection or human force-release (`POST /api/reservations/{leaseId}/force-release` with `confirm=true`, reason, and repository status snapshot). Force-release rotates the fencing token. |
| Reservation service unavailable | Mutations fail closed. |
| Invalid resource snapshot on heartbeat | `NodeRegistry` rejects the heartbeat (`ArgumentException`); the node retries on the next tick. Deserialization of a corrupt stored row yields `Resources = null` on `NodeDto`. |
| Malformed NDJSON | Logged and skipped; the protocol stream stays up. |


## Role model routing

Sessions carry `Runtime` (trusted adapter key) and `Model` (canonical `<runtime>/<model>` selector; the prefix before the first `/` always equals `Runtime`). Trusted prefixes are `codex`, `claude-code`, `antigravity`, and `muse`; the model id after the prefix is provider-native and opaque, and `default` asks the provider for its default model. There are no runtime profiles: the selector alone picks the adapter (`AgentRuntimeRegistry.Resolve`), and everything else about a session is derived from that adapter's fixed policy.

`Pi:Model` is the canonical root selector (`codex/default`).

Each role in `Pi:AllowedChildRoles` (`root`, `architect`, `implementer`, `reviewer`, `verifier`) maps to an ordered candidate list in `Pi:RoleRoutes`, e.g. `architect: claude-code/default → antigravity/default → muse/default → codex/default`. Defaults offer `muse/default` only on the read-oriented `architect` and `reviewer` routes, after the Claude and Antigravity candidates and before the Codex fallback; `implementer` and `verifier` never list it. The supervisor tries candidates in order and skips one that fails to start, cannot obtain a required reservation, or is read-only while write scopes were requested (`runtime_route_exhausted` when all fail). Spawn requests name only a role; agent content can never pick an executable, credential path, or runtime outside the prefix allowlist.

Write permission is not a route property: it is derived from the reservation leases the supervisor acquires for the child plus the adapter's own policy (Pi and Claude Code edit only under a lease; Antigravity and Muse Code are always read-only). Verification profiles are a separate concept (SPEC §20).

Routes are node-owned and live: the control plane invokes client callbacks `GetRuntimeConfiguration` (→ `NodeRuntimeConfigurationMessage`: `NodeId`, `AllowedRoles`, `RoleRoutes[]{Role, Candidates[]{Model}}`), `DiscoverRuntimeModels` (→ `RuntimeModelCatalogMessage[]`: `Runtime`, `Models[]{Id, DisplayName, Provider}`, `Error`), and `UpdateRuntimeConfiguration(UpdateNodeRuntimeConfigurationMessage{RoleRoutes})`. The node validates (allowed roles only, 1–16 canonical, deduplicated candidates per role) and persists owner-only to `Pi:AgentDataDirectory/role-routes.json`. The routing page offers discovered canonical ids directly with free-text fallback. Discovery returns one catalog per trusted prefix: `codex` from the worker's `modelCatalog.ts`, `claude-code` from already-configured non-default candidates (Claude has no authenticated list command), `antigravity` from `agy models`, and `muse` from `IMuseModelCatalogReader` (fresh `muse serve` host, `initialize`, `model/list`, terminate; ids are prefixed to `muse/<model-id>`). The Muse catalog fails closed: a reader error, or a read that yields no canonical `muse/` selector, is reported as the catalog `Error`, never as an empty success. Discovery never starts a Muse session and never reads credentials.

Claude hooks and `--settings` live under `$XDG_DATA_HOME/pi-command-center/claude-runtime/<session>/` (owner-only). The hook validator is loopback HTTP only (`http://127.0.0.1:<ephemeral>/pcc-claude-hook/`). Antigravity is read-only in this PoC.

### Muse Code (`muse/<model-id>`)

`MuseCodeRuntimeAdapter` (`RuntimeKind` `muse`) launches one official `muse serve` host per DevFleet session and speaks the stable MSP v1 surface over the host's stdio as newline-delimited JSON-RPC 2.0. The host argv is fixed and host-owned: `serve --disable-write --disable-shell --no-session-log`. Nothing in the `Muse` options section (`Executable`, timeouts, stderr and frame caps) can widen that posture; `--yolo`, `--disable-sandbox`, `--api-key-stdin`, and every login/logout/auth subcommand are never passed.

Lifecycle:

1. `initialize` (client name `devfleet`) then the `initialized` notification. An envelope schema version other than `1` fails closed; a stable-surface fingerprint other than the verified Muse Code 1.0.3 one is only a warning.
2. `session/start` with `workspaceRoot` = the project repository, `approvalMode` = `denyUnmatched` (the host is never asked to prompt), and `modelId` = the selector's model id, or omitted for `muse/default` so the host picks its own default. The returned provider `sessionId` becomes `ProviderSessionId`.
3. `turn/start` submits the prompt; later `SendAsync` input is another `turn/start`, which the host queues behind a running turn. Turn notifications are normalized into the shared event contract.
4. `turn/cancel` cancels the foreground turn; if the host does not settle within `CancelGraceSeconds` the host is terminated so cancel always lands.
5. MSP has **no session close method**. `CloseSessionAsync` sends a best-effort `view/unsubscribe`, then terminates the host (SIGTERM, then process-tree kill) within the grace period; the process boundary is the close.

Read-only is a model-driven boundary enforced by the host flags, not by DevFleet hooks: `--disable-write` and `--disable-shell` remove write and shell tools from the model, and `denyUnmatched` denies any approval the host would otherwise raise. The adapter refuses to start when the spawn carries a write authorization (a lease would imply write intent the host cannot honour), so a `muse/` candidate is skipped whenever write scopes are requested. Capabilities: streaming events, send input, cancel, snapshot; no child spawn and no plan tools. Every stdout frame is capped at `MaxLineBytes` (1 MiB default); a malformed or oversize frame, or a response with neither result nor error, faults pending requests and terminates the host.

Authentication is host-native: the operator runs `muse login` locally before managed use. The node never collects, reads, copies, or relays Muse or Meta credentials. A start or exit whose stderr matches the Muse/Meta login phrasing (`ProviderAuthClassifier`) is surfaced as `Attention=InputRequired` / `WorkState=Blocked` with the fixed reason "Complete Muse Code login locally (muse login)". Reasons are always fixed sentences; a bounded stderr tail (`MaxStderrLines`, default 200) travels only as the `stderrTail` diagnostic field of a non-auth `session.failed`.

## Subscription usage (`/usage`)

Operator page for **normalized** remaining subscription windows per provider on
the connected node. Pi remains the orchestrator: each manual refresh starts the
bundled Pi-SDK sidecar and the registered provider-native supplemental readers
concurrently. There is no Pi quota command and no production OMP CLI. A source
that is missing or drifts fails closed without suppressing the other sources;
numbers are never guessed or carried over.

Research and the public-vs-private classification:
[research/subscription-usage.md](research/subscription-usage.md).

### Flow

1. Browser loads `/usage` (cookie admin, Interactive Server) and presses Refresh.
2. Control plane `INodeSubscriptionUsageGateway` (`PiCommandCenter.ControlPlane.SubscriptionUsage.NodeSubscriptionUsageGateway`) looks up the node's SignalR connection and invokes the **client callback** `GetSubscriptionUsage` (empty argument list). Hub round-trip timeout is 35 s.
3. The node handler calls `PiCommandCenter.Node.SubscriptionUsage.IRuntimeSubscriptionUsageProbe.GetAsync`. It starts the configured Pi sidecar and ordered `ISupplementalSubscriptionUsageSource` implementations concurrently with one observation time.
4. Sidecar reports keep their JSON order. Each non-null supplement replaces the sidecar row with the same exact provider id or appends in registration order. A supplemental exception is isolated; malformed sidecar output still leaves configured supplements available.

### Sources per provider

| Provider ids | Source | Credential authority |
|---|---|---|
| `openai-codex`, `anthropic`, `kimi-code`, `zai`, `xai-oauth`, `opencode-go` | Bundled Pi `ModelRuntime` sidecar | Pi-managed provider state under `~/.pi/agent` |
| `anthropic` | `GET https://api.anthropic.com/api/oauth/usage`; bounded refresh at `https://platform.claude.com/v1/oauth/token` | Claude Code `claudeAiOauth` in `SubscriptionUsage:ClaudeCredentialPath` |
| `google-antigravity` | Official `agy --version`, then `agy -p /usage --print-timeout 8s` pinned TSV report | `agy`'s native `~/.gemini` state |

`google-antigravity` is a valid final DTO id but is deliberately absent from the
Pi-sidecar JSON allowlist. The provider-native Anthropic card replaces any
same-id sidecar report. Cursor remains unsupported.

**Muse Code has no subscription-usage surface.** Muse exposes no safe remaining-quota or auth-plan query: MSP v1 has no usage method, and the only credential-bearing state is the host-native `~/.config/muse` store that DevFleet never opens. Muse therefore has no row on `/usage`, no `ProviderSubscriptionUsageMessage`, and no diagnostic; it is a runtime-only, read-only selector (`muse/<model-id>`) and nothing about its quota is estimated or inferred from session behaviour.

### Window mapping (no inference)

- Utilization may be a fraction in `[0, 1]` or percentage points in `[0, 100]`. Finite `x` with `0 ≤ x ≤ 1` → `PercentUsed = x * 100`; `1 < x ≤ 100` → already percentage points. Anything else fails that window.
- `PercentRemaining = 100 − PercentUsed` when synthesized; if both arrive they must sum to 100.
- At most **8** windows per provider (`RuntimeSubscriptionUsageProbe.MaxWindows`); a ninth window is schema drift and invalidates the whole provider row. Duplicate names rejected.
- A percentage that is not finite, outside 0–100, or whose used/remaining pair does not sum to 100, or a reset time that is not RFC3339/epoch, invalidates the **whole** provider row. Nothing is estimated, extrapolated, or carried over. Percentage quota windows are the only rows on `/usage`; monetary balances without a percentage are out of scope.

### Fail closed

- `available` **requires** at least one coherent window. Empty `Windows` with `available` is invalid.
- `unavailable` / `error`: `process_missing` (node or script missing), non-zero exit, timeout, oversized/truncated stdout, `process_malformed` (JSON parse failure or unknown shape), no coherent window after strict percent normalize.
- Diagnostics are **stable identifiers only** matching `^[a-z0-9_]{1,40}$` (e.g. `process_missing`, `no_credential`, `request_timeout`). Hyphenated tokens are invalid. Provider response bodies, tokens, account IDs, user IDs, e-mail/org names, and raw stdout are never logged, never placed in `Diagnostic`, and never sent to the control plane.

### Bounds

- Process runner: `ProcessStartInfo.ArgumentList` only (**no shell**), stdin closed, host wall timeout and combined-output cap, process-tree kill on timeout.
- Claude credential reads are regular-file-only and bounded; HTTP uses exact HTTPS origins, no redirects, bounded bodies, and one operation deadline. Antigravity output is accepted only through the pinned four-column grammar.

### Deployment (`compose.yaml`)

Only the `node` service sees provider state; the control plane and browser never
do. Compose requires **no OMP binary or `~/.omp` state**. It configures the
bundled Pi sidecar, `SubscriptionUsage__ClaudeCredentialPath`, and the official
`agy` executable. The node container (`HOME=/home/node`) bind-mounts required
directories (`:z`):

| Host path | Container path | Why |
|---|---|---|
| `${HOME}/.pi/agent` | `/home/node/.pi/agent` | Pi ModelRuntime provider auth store; **not** labeled as Pi quota |
| `${HOME}/.claude` and `${HOME}/.claude.json` | under `/home/node` | Claude runtime state and the owner-only OAuth credential used by the Anthropic supplemental reader |
| `${HOME}/.gemini` | `/home/node/.gemini` | Native Antigravity state read only by the mounted `agy` process |
| `${HOME}/.config/muse` | `/home/node/.config/muse` | Muse Code host-native login state for `muse serve` sessions only; Muse has no usage card |

#### Trust boundary inside the node

The node container is one trust domain: the operator's own subscriptions, on a machine they own. Mounting those host directories exposes them to the node process so ModelRuntime can authenticate. That exposure is **not** extended to model-driven subprocesses:

- **Pi worker** (`runtime/pi-worker`): its SDK store is `Pi__AgentDataDirectory` (`/data/pi-agent`). Root and child sessions run with **no built-in tools**; `read`/`grep`/`find`/`ls` are node-owned custom tools that round-trip to the node and resolve through `RepositoryPathPolicy` (repository-relative, no symlink escape).
- **Claude Code**: model tools are limited by the host-owned `--settings` allowlist (`Read`/`Glob`/`Grep`, plus `Edit`/`Write` only under a lease) and the PreToolUse hook denies inspect paths outside the repository; `Bash` is not allowed.
- **Antigravity**: `AntigravityReadOnlySandbox` (bwrap) binds the host root read-only and then mounts private empty `tmpfs` over the sibling credential stores that are not required for `agy` itself (`MaskedSecretLocations`: `/provider-auth`, `/home/node/.claude`, `/home/node/.config/muse`); its own `/home/node/.gemini` stays readable. A mask location that exists but is not a directory, or a repository path that falls inside a masked location, makes sandbox setup throw and the session fail closed.
- **Muse Code**: the `muse serve` host is launched with `--disable-write --disable-shell --no-session-log` and `denyUnmatched` approvals, so the model has no write or shell tool and no approval prompt to escalate through. The host reads its own `~/.config/muse` login state; DevFleet never opens it and never passes credentials on argv, stdin, or environment. There is no DevFleet-side hook layer for Muse: the read-only boundary is the host's own tool policy.
- **Quota probe**: the node process runs the Pi sidecar, the Anthropic credential/HTTPS reader, and official `agy` usage report concurrently. Credential and raw provider output remain inside their source boundary and never cross the normalized DTO.

### Normalized DTO (`PiCommandCenter.Contracts.NodeTransport`)

- `NodeSubscriptionUsageMessage`: `NodeId`, `Providers`.
- `ProviderSubscriptionUsageMessage`: `Provider`, `Status` (`available` \| `unavailable` \| `error`), optional `Authenticated` / `PlanLabel` / `Version`, `Windows`, `ObservedAt`, `Source`, `Diagnostic`. No runtime-profile field.
- `SubscriptionUsageWindowMessage`: `Name`, `PercentUsed` and/or `PercentRemaining`, optional `ResetsAt`.
- Node-internal: `IRuntimeSubscriptionUsageProbe.GetAsync` and the `SubscriptionUsage` options section (`NodeExecutable`, `ScriptPath`).

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


## Node system resources (Fleet `/`)

The node captures a **fail-closed, nullable** snapshot on the existing heartbeat only (`INodeSystemResourceMonitor.Capture` → `NodeWorker` → `NodeHub.Heartbeat`). No extra timer. Previous CPU counters live in process memory so utilization is an interval since the last tick; the **first** `CpuUsagePercent` after process start is `null`.

Sources (unprivileged): cgroup v2 via `/proc/self/cgroup` when the current cgroup has real charge/limits (`cpu.stat`/`cpu.max`, `memory.current`/`memory.max`); otherwise host procfs (`/proc/stat`, `/proc/meminfo`). Disk is `DriveInfo("/")` (`IsReady`, `TotalSize`, `AvailableFreeSpace`). Load and uptime are **host** `/proc/loadavg` and `/proc/uptime` field 1 — not container age. Unlimited `cpu.max`/`memory.max` fall back to procfs, never a fake 1-CPU or 0-byte budget.

Wire/application types: `NodeResourceSnapshotMessage` / `NodeResourceSnapshotDto` — required UTC `ObservedAt`; nullable `CpuUsagePercent` (0–100), `MemoryUsedBytes`/`MemoryTotalBytes`, `DiskUsedBytes`/`DiskTotalBytes`, `LoadAverageOneMinute`, `UptimeSeconds`. Per-field null on missing file, parse failure, non-finite, negative, `used > total`, or CPU outside `[0, 100]`. Never reuse a stale reading. Control plane persists latest JSON only; `NodeDto.Resources` feeds `/`. Missing meters show **Unavailable**.

Research: [research/node-system-resource-monitoring.md](research/node-system-resource-monitoring.md).

## Demonstration vs Definition of Done

SPEC §43 / §46 require the first demonstration **through the web UI** (login, project, enqueue the health-details request, watch the agent tree, reservations, verification, completion). Operator commands live in the README demo section (`scripts/demo.sh`).

- `scripts/demo.sh --smoke` (default quota-free path): starts Control Plane + Node on loopback `127.0.0.1:${PI_CC_PORT:-5057}`, may copy `demo/health-details-fixture` under the approved root, and may `POST /api/projects` for bootstrap. It does **not** launch Pi, `claude`, `agy`, or `muse`, and HTTP register/enqueue is **not** request completion.
- Live providers are opt-in only: `RUN_REAL_PI_TESTS=1`, `RUN_REAL_CLAUDE_TESTS=1`, `RUN_REAL_ANTIGRAVITY_TESTS=1`, `RUN_REAL_MUSE_TESTS=1` on `dotnet test` (subscription quota). Completing SPEC §43 still means the browser, not curl.
- Blazor surfaces: `/` fleet, `/projects/{id}` queue and composer, request page (plan, diff, verification, result, reservations, mail), `/attention`, `/usage`.

