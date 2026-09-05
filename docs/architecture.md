# Architecture

Pi Command Center is a single-user, single-node proof of concept: a Blazor command center plus an on-machine node that runs Pi, Claude Code, and Antigravity children against one canonical Git workspace per project. No Git worktrees.

## Component boundaries

| Component | Process | Authority |
|---|---|---|
| `PiCommandCenter.Web` | In-process with Control Plane | Blazor Interactive Server UI (fleet, project, request, `/attention`) |
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
| Hub | `/nodeHub` | Node token policy only; never browsed |

Blazor pages: `/` (fleet), project and request routes, `/attention`, `/login`.

## Demonstration vs Definition of Done

SPEC §43 / §46 require the first demonstration **through the web UI** (login, project, enqueue the health-details request, watch the agent tree, reservations, verification, completion). Operator commands live in the README demo section (`scripts/demo.sh`).

- `scripts/demo.sh --smoke` (default quota-free path): starts Control Plane + Node on loopback `127.0.0.1:${PI_CC_PORT:-5057}`, may copy `demo/health-details-fixture` under the approved root, and may `POST /api/projects` for bootstrap. It does **not** launch Pi, `claude`, or `agy`, and HTTP register/enqueue is **not** request completion.
- Live providers are opt-in only: `RUN_REAL_PI_TESTS=1`, `RUN_REAL_CLAUDE_TESTS=1`, `RUN_REAL_ANTIGRAVITY_TESTS=1` on `dotnet test` (subscription quota). Completing SPEC §43 still means the browser, not curl.
- Blazor surfaces: `/` fleet, `/projects/{id}` queue and composer, request page (plan, diff, verification, result, reservations, mail), `/attention`.

