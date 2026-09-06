# Architecture

Pi Command Center is a single-user fleet command center: a Blazor control plane plus nodes that run Pi, Claude Code, Antigravity, and Muse Code children. A Project is fleet-owned metadata and has no node or repository path. In the initial phase it may have zero or one node-local WorkspaceBinding; that binding designates one canonical workspace directory — an existing Git checkout, or an ordinary/unborn directory that the assigned node's supervisor prepares locally at the first request start — with no repository mobility or transparent failover. No Git worktrees.

## Component boundaries

| Component | Process | Authority |
|---|---|---|
| `PiCommandCenter.Web` | In-process with Control Plane | Blazor Interactive Server UI (fleet, project, request, `/attention`, `/usage`, `/statistics`); Recover project panel; never node proof |
| `PiCommandCenter.ControlPlane` | ASP.NET Core (`net10.0`) | Authoritative SQLite for Projects, WorkspaceBindings, requests, ExecutionAssignments, RecoveryHolds, RecoveryOperations; HTTP API including project recovery; SignalR `/nodeHub`; migrations at startup |
| `PiCommandCenter.Application` / `Domain` / `Infrastructure` | Libraries | Domain model, use cases, EF Core; `IProjectRecoveryService` / `IManualProjectRecoveryService` / `IRecoveryAttemptCoordinator` |
| `PiCommandCenter.Contracts` | Library | SignalR DTOs (`PiCommandCenter.Contracts.NodeTransport`), including `RecoverAssignmentCommandMessage`, `AssignmentRecoveryProofMessage` |
| `PiCommandCenter.Node` | Separate `net10.0` worker | Validates bindings, claims work, executes assignments, Linux `setsid` process-group identity, assignment journal/event spool; reports node proof, never operator attestation |
| `runtime/pi-worker` | Node.js ≥ 26 (`runtime/package.json`) | Pi SDK session; strict NDJSON protocol v1 on stdin/stdout |

Dependency direction: Domain → Application → Infrastructure/ControlPlane/Node. The TypeScript worker never imports C#; it speaks protocol v1 only.

## Data flow

```text
Browser (cookie admin)
    │  HTTPS / Blazor circuit / REST under /api
    ▼
Control Plane  (loopback default, SQLite WAL)
    ├─ Project: fleet metadata and policy only
    ├─ WorkspaceBinding: 0..1 per Project; NodeId + validated local path + revision
    ├─ WorkRequest: queued independently; durable ExecutionAssignment at claim; optional OriginalRequestId
    └─ RecoveryHold / RecoveryOperation: project-scoped pause and durable stop workflow
    ▲
    │  authenticated SignalR /nodeHub
    │
Assigned node worker
    │  browses and validates designated paths on its own filesystem
    │  NDJSON protocolVersion 1 (1 MiB frames) ──► pi-worker (Pi SDK)
    │  official `claude` + host-owned --settings hooks
    │  official `agy` (read-only reviewer)
    │  official `muse serve` (read-only MSP v1 JSON-RPC over stdio)
    ▼
WorkspaceBinding checkout (supervisor-owned Git)
```

```text
Browser  GET /usage  (Blazor Interactive Server; load or manual Refresh)
    │  INodeSubscriptionUsageGateway.GetAsync(nodeId)
    ▼
Control Plane  NodeSubscriptionUsageGateway
    │  SignalR Clients.Client(connectionId).InvokeCoreAsync
    │  method GetSubscriptionUsage  (no arguments)
    ▼
Node worker  in-memory subscription-usage cache (latest snapshot)
    │  reads only; does not start sidecars, HTTP, or CLIs

Node worker  background collector (immediate, then every five minutes)
    │  IRuntimeSubscriptionUsageProbe.GetAsync  (sole collector)
    ├─ Pi sidecar  installed usage.ts under ~/.local/lib/devfleet
    │              (openai-codex, anthropic, kimi-code, zai, xai-oauth, opencode-go,
    │               qwen-token-plan, qwen-token-plan-individual, qwen-token-plan-cn)
    ├─ Anthropic   owner-only .credentials.json ──► https://api.anthropic.com
    └─ Antigravity official agy -p /usage --print-timeout 8s
All three start concurrently on each cache refresh; supplements replace the
same provider id or append in registration order. Only normalized windows
cross SignalR. Failed refresh keeps the last successful snapshot.
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

1. Operator registers fleet Project metadata and policy. Registration neither accepts nor validates a node or repository path.
2. The operator may designate the Project's sole WorkspaceBinding: a node plus a path chosen from that node's approved-root tree. The UI browses via `INodeWorkspaceDirectoryGateway` → SignalR `BrowseWorkspaceDirectories` on the selected authenticated node only (null path lists `Projects:ApprovedRoots`; each response is one directory level; no typed path). The control plane never inspects node filesystems. Browse cannot inspect Git, so the designation form always states the consent warning before submission. The selected authenticated node then classifies the binding under its node-local `Projects:ApprovedRoots` and returns a preparation classification: `valid` (repository with commits on the default branch), `repository_initialization_required` (ordinary directory), or `baseline_commit_required` (unborn repository) — all three status `valid` with the canonical path — otherwise an invalid code such as `default_branch_missing` for an existing repository lacking the configured branch. Classification changes nothing on disk; every edit advances the validation revision, and stale responses are ignored.
3. A work request is enqueued (`POST /api/projects/{projectId}/requests`). Registration and enqueue both work with no binding; the request remains `Queued` with a scheduling reason until the binding and node are eligible.
4. At claim time, the control plane atomically creates a durable ExecutionAssignment, changes the request to `Starting`, and returns the immutable binding snapshot only to the designated node. The initial phase never selects another checkout or fails over to another node.
   After the node durably journals that assignment and before baseline capture, request-branch creation, and root start, its supervisor prepares the workspace through `ITrustedGitService.PrepareWorkspaceAsync`: initializing the repository and/or committing existing non-ignored contents as `Initialize workspace for DevFleet` under a fixed command-local identity, and doing nothing when the workspace already has commits. Preparation and request-branch creation are idempotent. A failure in any startup step journals `StartBlocked`, publishes one assignment-scoped `request.blocked` event without fabricating a session, keeps the assignment retained and retryable, and reconciles as retained rather than recovery-required.
5. Only the connection authenticated as the assigned node, with the assignment token, may renew, publish owned events, operate reservations, verify, complete, or receive cancellation for that request.
6. The root delegates children on the same assigned node and workspace; write-capable children acquire leases before mutating files. A Project has an effective limit of one nonterminal development assignment, including finalizing, cancelling, and recovery-required work.
7. Completion, failure, and cancellation release ownership only after assignment-bound quiescence closes new work, drains supervised activity, flushes events, and accounts for reservations and repository state. Uncertainty enters recovery instead of permitting a second writer.
8. On each existing node heartbeat (`Node:HeartbeatSeconds`, default 10s) `NodeSystemResourceMonitor` samples CPU, memory, disk, 1-minute load, and uptime once, attaches `NodeResourceSnapshotMessage` to `NodeHeartbeatMessage`, and the control plane stores **only that latest** snapshot. The Fleet page (`/`) refreshes through `ProjectionChange.Fleet()`. Sampling, sources, and fail-closed rules: [research/node-system-resource-monitoring.md](research/node-system-resource-monitoring.md).

## Persistence

- Control-plane connection string: `ConnectionStrings:ControlPlane` (default `Data Source=controlplane.db;Cache=Shared` in `src/PiCommandCenter.ControlPlane/appsettings.json`).
- Startup applies EF Core migrations (`Program` logs “Applying control-plane database migrations”).
- `Projects` stores fleet identity and policy, never `NodeId` or a repository path.
- `WorkspaceBindings` stores the Project's zero-or-one designation, node-local canonical path, classification state, code and revision, and the validating NodeId. Binding liveness is not validation state, and a preparable classification (`repository_initialization_required`, `baseline_commit_required`) is valid for scheduling.
- `ExecutionAssignments` stores one immutable node/workspace/default-branch/validation-revision snapshot per assigned request plus its token, lease, state, reconciliation, terminal history, and captured verification policy snapshot (baseline version, selected trusted profile id/revision, mandatory command ids). The row survives terminalization, disconnect, and lease expiry; lease expiry is loss of recent proof, not release or reassignment.
- Recovery tables (`RecoveryHolds`, `RecoveryOperations`, `RecoveryTargets`, `RecoveryReservationTargets`, `RecoveryIdempotencyKeys`, `RecoveryAuditFacts`): one unresolved operation per Project (`Status` not `Recovered`); hold is separate from operation success and blocks `ClaimNext` with `project_recovery_paused`.
- `WorkRequests.OriginalRequestId`: optional immutable FK to the same-project original; Restrict on delete; never copies claim tokens or assignment snapshots.
- Node assignment journal records `AssignmentProcessIdentity` (`ProcessId`, `StartTimeTicks` from `/proc/<pid>/stat` field 22, `ProcessGroupId`, `SessionId`). Recovery never deletes the journal or spool.
- Node event spool and assignment journal: `Node:EventSpoolPath` default `~/.local/share/devfleet/node-spool.db`. The node records assignment identity and token before workspace preparation and launch, so a node that dies mid-preparation still knows which assignment authorized those local Git changes, and restart reconciliation precedes new claims.
- Application data directory used by setup and the systemd daemon: `~/.local/share/devfleet` (`0700`). Protected install root: `~/.local/lib/devfleet`.
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
| Control Plane restart | SQLite queue, history, bindings, and ExecutionAssignments survive. The authenticated node reconnects, submits its durable assignment inventory, reconciles before `ClaimNext`, and replays unacknowledged spool events; `PublishEvents` remains idempotent on `EventId`. |
| Brief disconnect or missed heartbeats | The node is marked offline after **three** missed `Node:HeartbeatSeconds` intervals (default 10s). Its nonterminal assignment remains owned and occupies node/project capacity; another node cannot claim it. |
| Assignment lease expiry | Normal renewal is rejected. The same node must reconcile its persisted token, binding revision, process inventory, and repository status. Expiry never frees the writer slot and never causes failover. |
| Node process restart | The owner-only assignment journal survives. Linux stop proof enumerates `/proc` by session/process group; a PID whose start ticks no longer match is reuse (exited), not live. Non-Linux or missing `setsid` is `process_stop_unproven`. Uncertainty remains `RecoveryRequired`. Restart is not quiescence. |
| Runtime process crash | Capture exit code and stderr tail; emit `session.failed`; mark the ExecutionAssignment and owned **Active** leases `RecoveryRequired` (`reservation.recovery_required`). Neither is released by silence. |
| Startup failure before any session (workspace preparation, policy, branch, or root start) | The node journals `StartBlocked` and spools one assignment-scoped `request.blocked` event with the failing phase; no session identity is fabricated. The assignment is retained and retryable on the same assignment, reconciles as retained rather than `RecoveryRequired`, and cancellation still proves quiescence before terminalizing without a session. |
| Completion, failure, or cancellation | Ownership remains through `Finalizing` or `Cancelling` until the assigned node proves admission is closed and all assignment-bound processes, mutations, verification, Git work, events, and reservations are quiescent. Integer counts of zero are not unknown; unknown inventories cannot authorize release. |
| Operator Recover project | `ProjectRecoveryService` atomically captures inventory, sets `RecoveryHoldRow`, persists `RecoveryOperationRow` (`Pending`/`Running`/`NeedsIntervention`/`Recovered`), and cancels targets. Assigned node runs `RecoverAssignment`. Recheck starts a new attempt; `confirm-manual` is operator attestation (`operator-attestation`); resume clears only a recovered hold. |
| Node never returns | Assignment remains owned until node proof or administrator `confirm-manual`. The same request is not requeued elsewhere. Returning node receives `RecoverAssignment` / cancellation before claims; historical replay cannot reopen terminal or recovered execution. |
| Stale reservation lease | Not reusable until recovery inspection or human force-release (`POST /api/reservations/{leaseId}/force-release` with `confirm=true`, reason, and repository status snapshot). Force-release rotates the fencing token; it is not process-stop proof and is not a substitute for recovery. |
| Reservation service unavailable | Mutations fail closed. |
| Invalid resource snapshot on heartbeat | `NodeRegistry` rejects the heartbeat (`ArgumentException`); the node retries on the next tick. Deserialization of a corrupt stored row yields `Resources = null` on `NodeDto`. |
| Malformed NDJSON | Logged and skipped; the protocol stream stays up. |

Lifecycle ownership: control plane owns hold, operation, idempotency, and terminal truth; node owns process-group identity and `AssignmentRecoveryProofMessage`; administrator owns attestation text and confirmations. Recovery never deletes workspaces, journals, or spools.

Operator procedure: [docs/operations/project-recovery.md](operations/project-recovery.md).


## Role model routing

Sessions carry `Runtime` (session runtime kind) and `Model` (canonical `<provider>/<model>` selector). The provider prefix is lowercase ASCII alphanumeric with interior hyphens; `pi` is rejected because Pi is a runtime, not a provider. The reserved prefixes `claude-code`, `antigravity`, and `muse` select their dedicated official-harness adapters; every other valid prefix resolves to Pi as the runtime adapter (`AgentRuntimeRegistry.Resolve`), so every Pi-backed session's `RuntimeKind` stays `pi`. The model id after the first `/` is provider-native and opaque (it may itself contain slashes). The model id `default` is invalid (`AgentModelSelector.TryParse` rejects it). Adapters always pass that native model id; they never omit it. There are no runtime profiles: the selector alone picks the provider and model, and everything else about a session is derived from that adapter's fixed policy.

`Pi:Model` is the canonical root selector (`codex/gpt-5.6-sol`).

Pi is the runtime adapter behind every non-reserved provider. Selector decoding: `codex` aliases the Pi SDK provider `openai-codex`, so `codex/gpt-5.6-sol` resolves to Pi's `openai-codex/gpt-5.6-sol`; every other Pi provider prefix passes through identically (`zai/glm-4.7` resolves to Pi's `zai/glm-4.7`; `qwen-token-plan/qwen3.8-max` resolves to Pi's `qwen-token-plan/qwen3.8-max`). Qwen Token Plan is generic Pi routing, not a dedicated adapter. Any other syntactically valid provider still goes only to Pi and fails closed unless the Pi worker has that provider authenticated and available. Pi discovery returns one catalog per authenticated Pi provider (the Pi SDK provider `openai-codex` is reported under its `codex` alias), alongside the external Claude Code, Antigravity, and Muse catalogs — every reported selector is runnable. Readiness and catalog matching use the exact explicit selector.

`RuntimeModelDiscovery` is a node-hosted background cache for the Pi, Antigravity, and Muse discovery results. It collects once immediately at node startup and then refreshes on a non-overlapping five-minute cadence; reads never launch discovery processes, and before the first completed refresh a read waits for initial data while honoring caller cancellation. A failed refresh keeps the last completed snapshot, including provider-level error catalogs. Claude Code is excluded from that snapshot because its catalog includes configured route selectors, so `ClaudeCatalog()` is recomputed from live routing on every request. The cache is process-local and starts empty after a node restart.

Each role in `Pi:AllowedChildRoles` (`root`, `architect`, `implementer`, `reviewer`, `verifier`) maps to an ordered candidate list in `Pi:RoleRoutes`, e.g. `architect: claude-code/fable-5-1 → antigravity/gemini-3-pro → muse/muse-spark-1.3 → codex/gpt-5.6-sol`. Built-ins offer `muse/muse-spark-1.3` only on the read-oriented `architect` and `reviewer` routes, after the Claude and Antigravity candidates and before the Codex fallback; `implementer` and `verifier` never list it. The supervisor tries candidates in order and skips one that fails to start, cannot obtain a required reservation, or is read-only while write scopes were requested (`runtime_route_exhausted` when all fail). Spawn requests name only a role; agent content can never pick an executable, credential path, or provider outside the node's routing configuration. Persisted `role-routes.json` overrides that still contain a deprecated `<provider>/default` selector are discarded and replaced by the configured explicit routes.

Write permission is not a route property: it is derived from the reservation leases the supervisor acquires for the child plus the adapter's own policy (Pi and Claude Code edit only under a lease; Antigravity and Muse Code are always read-only). The `verifier` role is a model route for independent agent review and is not the deterministic executor. Deterministic verification is a separate node-owned policy (SPEC §20).

Routes are node-owned and live: the control plane invokes client callbacks `GetRuntimeConfiguration` (→ `NodeRuntimeConfigurationMessage`: `NodeId`, `AllowedRoles`, `RoleRoutes[]{Role, Candidates[]{Model}}`), `DiscoverRuntimeModels` (→ `RuntimeModelCatalogMessage[]`: `Provider`, `Models[]{Id, DisplayName, Provider}`, `Error`), and `UpdateRuntimeConfiguration(UpdateNodeRuntimeConfigurationMessage{RoleRoutes})`. The node validates (allowed roles only, 1–16 canonical, deduplicated candidates per role) and persists owner-only to `Pi:AgentDataDirectory/role-routes.json`. The routing page renders whatever provider catalogs the node reports and offers discovered canonical ids directly with free-text fallback. Discovery returns one catalog per provider: one per authenticated Pi provider from the worker's `modelCatalog.ts` (a discovery process failure is reported as the `codex` catalog's error), `claude-code` from already-configured explicit candidates (Claude has no authenticated list command), `antigravity` from `agy models`, and `muse` from `IMuseModelCatalogReader` (fresh `muse serve` host, `initialize`, `model/list`, terminate; ids are prefixed to `muse/<model-id>`). The Muse catalog fails closed: a reader error, or a read that yields no canonical `muse/` selector, is reported as the catalog `Error`, never as an empty success. Discovery never starts a Muse session and never reads credentials.

Claude hooks and `--settings` live under `$XDG_DATA_HOME/devfleet/claude-runtime/<session>/` (owner-only). The hook validator is loopback HTTP only (`http://127.0.0.1:<ephemeral>/pcc-claude-hook/`). Antigravity is read-only in this PoC.

## Verification

`IRequestVerificationCoordinator` on the node owns final verification: policy resolution, `project-build` leasing, baseline execution, optional trusted-profile execution, persistence, lifecycle events, bounded `verification.command.started` progress facts, fingerprint reuse, and bounded summaries. The control plane owns the completion gate and evaluates the assignment policy snapshot, not “any passed mandatory row.”

Empty `Verification:Profiles` is baseline-only (`devfleet-baseline` version `1`: mandatory `repository-integrity`, optional `whitespace`). Fingerprints include Git executable mode from opened regular files. Baseline capture and each baseline command use a total deadline. The sanitized Git overlay may copy allowlisted `core.whitespace` only. Projects store only a nullable selected profile id and revision. Catalog callbacks expose ids, labels, command ids, working-directory labels, mandatory/optional flags, and timeouts — never executables, environment, credentials, or raw argv. Child `run_project_checks()` is intermediate and does not change request phase.


## Muse Code (`muse/<model-id>`)

`MuseCodeRuntimeAdapter` (`RuntimeKind` `muse`) launches one official `muse serve` host per DevFleet session and speaks the stable MSP v1 surface over the host's stdio as newline-delimited JSON-RPC 2.0. The host argv is fixed and host-owned: `serve --disable-write --disable-shell --no-session-log`. Nothing in the `Muse` options section (`Executable`, timeouts, stderr and frame caps) can widen that posture; `--yolo`, `--disable-sandbox`, `--api-key-stdin`, and every login/logout/auth subcommand are never passed.

Lifecycle:

1. `initialize` (client name `devfleet`) then the `initialized` notification. An envelope schema version other than `1` fails closed; a stable-surface fingerprint other than the verified Muse Code 1.0.3 one is only a warning.
2. `session/start` with `workspaceRoot` = the ExecutionAssignment's immutable canonical path snapshot, `approvalMode` = `denyUnmatched` (the host is never asked to prompt), and `modelId` = the selector's native model id (always forwarded; never omitted). The returned provider `sessionId` becomes `ProviderSessionId`.
3. `turn/start` submits the prompt; later `SendAsync` input is another `turn/start`, which the host queues behind a running turn. Turn notifications are normalized into the shared event contract.
4. `turn/cancel` cancels the foreground turn; if the host does not settle within `CancelGraceSeconds` the host is terminated so cancel always lands.
5. MSP has **no session close method**. `CloseSessionAsync` sends a best-effort `view/unsubscribe`, then terminates the host (SIGTERM, then process-tree kill) within the grace period; the process boundary is the close.

Read-only is a model-driven boundary enforced by the host flags, not by DevFleet hooks: `--disable-write` and `--disable-shell` remove write and shell tools from the model, and `denyUnmatched` denies any approval the host would otherwise raise. The adapter refuses to start when the spawn carries a write authorization (a lease would imply write intent the host cannot honour), so a `muse/` candidate is skipped whenever write scopes are requested. Capabilities: streaming events, send input, cancel, snapshot; no child spawn and no plan tools. Every stdout frame is capped at `MaxLineBytes` (1 MiB default); a malformed or oversize frame, or a response with neither result nor error, faults pending requests and terminates the host.

Authentication is host-native: the operator runs `muse login` locally before managed use. The node never collects, reads, copies, or relays Muse or Meta credentials. A start or exit whose stderr matches the Muse/Meta login phrasing (`ProviderAuthClassifier`) is surfaced as `Attention=InputRequired` / `WorkState=Blocked` with the fixed reason "Complete Muse Code login locally (muse login)". Reasons are always fixed sentences; a bounded stderr tail (`MaxStderrLines`, default 200) travels only as the `stderrTail` diagnostic field of a non-auth `session.failed`.

## Subscription usage (`/usage`)

Operator page for **normalized** remaining subscription windows per provider on
the connected node. Pi remains the orchestrator: the node runs the bundled
Pi-SDK sidecar and the registered provider-native supplemental readers
concurrently once immediately and then every five minutes into an in-memory
cache. Browser load, manual Refresh, and the SignalR `GetSubscriptionUsage`
callback read that cache; they do not start sidecars, HTTP calls, or CLIs.
There is no Pi quota command and no production OMP CLI. There is no
persistence, no new wire message, and no configurable interval: five minutes
is the product policy. A source that is missing or drifts fails closed without
suppressing the other sources; numbers are never guessed. A failed cache
refresh keeps the last successful snapshot; `ObservedAt` on each provider row
exposes data age.

Research and the public-vs-private classification:
[research/subscription-usage.md](research/subscription-usage.md).

### Flow

1. The node starts one immediate collection through `PiCommandCenter.Node.SubscriptionUsage.IRuntimeSubscriptionUsageProbe.GetAsync`, then refreshes the in-memory cache every five minutes on a non-overlapping cadence. Successful snapshots atomically replace the cache. A failed refresh preserves the last successful snapshot and retries on the next cadence.
2. Browser loads `/usage` (cookie admin, Interactive Server) or presses Refresh. That read does not collect.
3. Control plane `INodeSubscriptionUsageGateway` (`PiCommandCenter.ControlPlane.SubscriptionUsage.NodeSubscriptionUsageGateway`) looks up the node's SignalR connection and invokes the **client callback** `GetSubscriptionUsage` (empty argument list). Hub round-trip timeout is 35 s.
4. The node handler returns the latest cached `NodeSubscriptionUsageMessage`. Before the first successful refresh, a read waits for initial data and honors caller cancellation. It never invokes the probe.
5. Each collection starts the configured Pi sidecar and ordered `ISupplementalSubscriptionUsageSource` implementations concurrently with one observation time. Sidecar reports keep their JSON order. Each non-null supplement replaces the sidecar row with the same exact provider id or appends in registration order. A supplemental exception is isolated; malformed sidecar output still leaves configured supplements available.

### Sources per provider

| Provider ids | Source | Credential authority |
|---|---|---|
| `openai-codex`, `anthropic`, `kimi-code`, `zai`, `xai-oauth`, `opencode-go` | Bundled Pi `ModelRuntime` sidecar | Pi-managed provider state under `~/.pi/agent` |
| `qwen-token-plan`, `qwen-token-plan-individual`, `qwen-token-plan-cn` | Bundled Pi sidecar reports `unavailable` with diagnostic `quota_console_only`; `/usage` shows the configured plan and a console-only remaining-quota notice, never percentages | Pi-managed Qwen Token Plan auth under `~/.pi/agent`. Remaining Alibaba Token Plan quota is console-only; session tokens cannot derive Alibaba Credits |
| `anthropic` | `GET https://api.anthropic.com/api/oauth/usage`; bounded refresh at `https://platform.claude.com/v1/oauth/token` | Claude Code `claudeAiOauth` in `SubscriptionUsage:ClaudeCredentialPath` |
| `google-antigravity` | Official `agy --version`, then `agy -p /usage --print-timeout 8s` pinned TSV report | `agy`'s native `~/.gemini` state |

`google-antigravity` is a valid final DTO id but is deliberately absent from the
Pi-sidecar JSON allowlist. The provider-native Anthropic card replaces any
same-id sidecar report. Cursor remains unsupported. `/usage` never renders raw node diagnostics.

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

### Deployment (systemd user daemon)

Production is **only** Fedora systemd user units `pi-command-center-control-plane.service` and `pi-command-center-node.service`. There is no Compose or container runtime. The node process sees host-native provider state; the control plane and browser never do. No OMP binary or `~/.omp` state is required. Bind defaults to loopback; `DEVFLEET_BIND_ADDRESS` selects a specific address.

| Host path | Why |
|---|---|
| `~/.local/share/devfleet` | SQLite, spool, auth files, Data Protection keys, Pi agent data |
| `~/.local/lib/devfleet` | Published Control Plane, Node, and production npm worker |
| `~/.pi/agent` | Pi ModelRuntime provider auth store; **not** labeled as Pi quota |
| `~/.claude` and `~/.claude.json` | Claude runtime state and the owner-only OAuth credential used by the Anthropic supplemental reader |
| `~/.gemini` | Native Antigravity credentials, cache, and logs; the `agy` sandbox grants this provider-owned directory its only writable home-state bind |
| `~/.config/muse` | Muse Code host-native login state for `muse serve`; Muse has no usage card |
| `~/Developer` | Default node-side `Projects:ApprovedRoots` entry for WorkspaceBinding validation; writable as required by the node unit |

#### Trust boundary inside the node

The node is one trust domain: the operator's own subscriptions, on a machine they own. Host-native CLIs and credential stores are used in place; that exposure is **not** extended to model-driven subprocesses:

- **Pi worker** (`runtime/pi-worker`): `ModelRuntime.create()` takes no custom auth/models paths, so provider credentials and models stay in host-native `~/.pi/agent`. `createAgentSession({ agentDir })` uses `Pi:AgentDataDirectory` (`~/.local/share/devfleet/pi-agent`) only for DevFleet-owned session, resource, and role-route persistence (`role-routes.json`). Root and child sessions run with **no built-in tools**; `read`/`grep`/`find`/`ls` are node-owned custom tools that round-trip to the assigned node and resolve through `RepositoryPathPolicy` inside the ExecutionAssignment's canonical workspace (repository-relative, no symlink escape).
- **Claude Code**: model tools are limited by the host-owned `--settings` allowlist (`Read`/`Glob`/`Grep`, plus `Edit`/`Write` only under a lease) and the PreToolUse hook denies inspect paths outside the ExecutionAssignment's canonical workspace; `Bash` is not allowed.
- **Antigravity**: `AntigravityReadOnlySandbox` (bwrap) binds the host root and assigned workspace read-only, then grants a writable bind only to its own `~/.gemini` credential/cache/log directory so the official CLI can maintain native state. Private empty `tmpfs` mounts mask sibling credential stores (`MaskedSecretLocations` includes Pi, Claude, and Muse homes). A mask or state location that exists but is not a directory, an assigned workspace inside a mask, or any overlap between the assigned workspace and writable state makes sandbox setup throw and the session fail closed.
- **Muse Code**: the `muse serve` host is launched with `--disable-write --disable-shell --no-session-log` and `denyUnmatched` approvals, so the model has no write or shell tool and no approval prompt to escalate through. The host reads its own `~/.config/muse` login state; DevFleet never opens it and never passes credentials on argv, stdin, or environment. There is no DevFleet-side hook layer for Muse: the read-only boundary is the host's own tool policy.
- **Quota probe**: the node process is the only collector. On the five-minute cache cadence it runs the installed Pi sidecar, the Anthropic credential/HTTPS reader, and official `agy` usage report concurrently. Browser and SignalR reads never start those processes. Credential and raw provider output remain inside their source boundary and never cross the normalized DTO.

### Normalized DTO (`PiCommandCenter.Contracts.NodeTransport`)

- `NodeSubscriptionUsageMessage`: `NodeId`, `Providers`. Unchanged wire callback.
- `ProviderSubscriptionUsageMessage`: `Provider`, `Status` (`available` \| `unavailable` \| `error`), optional `Authenticated` / `PlanLabel` / `Version`, `Windows`, `ObservedAt`, `Source`, `Diagnostic`. No runtime-profile field. `ObservedAt` is the observation time of the cached snapshot and exposes data age.
- `SubscriptionUsageWindowMessage`: `Name`, `PercentUsed` and/or `PercentRemaining`, optional `ResetsAt`.
- Node-internal: `IRuntimeSubscriptionUsageProbe.GetAsync` (sole collector for the in-memory cache) and the `SubscriptionUsage` options section (`NodeExecutable`, `ScriptPath`).

## Fleet statistics (`/statistics`)

Authenticated operators open `/statistics`. `IFleetStatisticsService` (`FleetStatisticsService`) reads `AgentSessions` plus known append-only `SessionEvents` telemetry. Subscription remaining quota (`/usage`) is a separate surface: `/usage` shows configured Qwen with a console-only remaining-quota notice; `/statistics` attributes actual persisted Pi token telemetry by canonical model provider. Tokens cannot derive Alibaba Credits.

Fleet DTO: `TrackedAgents`, `ActiveAgents` (no end time, liveness not `Exited`, work state not `Completed`/`Failed`/`Cancelled`), `AgentsWithReportedTokens`, `AgentsWithEstimatedCost`, nullable `Tokens` (input, output, cache-read, cache-write, thinking), nullable `EstimatedCostUsd`, `IgnoredTelemetryEvents`, `LatestTelemetryAt`, `Runtimes` ordered by runtime id, and `Providers` as an ordinal list of `ProviderStatisticsDto` (`Provider`, `TrackedAgents`, `ActiveAgents`, `AgentsWithReportedTokens`, `Tokens`, `EstimatedCostUsd`) grouped by canonical model provider (Qwen Token Plan ids remain identifiable). Each runtime and provider row repeats agent counts, tokens, and estimated cost. Null is not zero; a reported zero stays zero. Grouped provider totals are observed session telemetry, not remaining subscription quota.

Research: [research/agent-token-cost-statistics.md](research/agent-token-cost-statistics.md).


## HTTP surface (cookie admin unless noted)

| Method | Path | Notes |
|---|---|---|
| GET | `/health` | Anonymous on loopback |
| GET/POST | `/api/projects` | List / register fleet metadata only |
| GET | `/api/projects/{projectId}` | Includes the nullable designated WorkspaceBinding |
| PUT | `/api/projects/{projectId}/workspace-binding` | Create or replace the sole node/path designation (path comes from node browse, not typed entry); advances its validation revision |
| POST | `/api/projects/{projectId}/workspace-binding/validate` | Ask the selected authenticated node to validate the current revision; remains pending while offline |
| DELETE | `/api/projects/{projectId}/workspace-binding` | Allowed only when no nonterminal or recovery-required assignment references it |
| GET/POST | `/api/projects/{projectId}/requests` | List / enqueue; optional `OriginalRequestId` linked retry; ineligible work stays queued |
| GET | `/api/projects/{projectId}/recovery` | Read-only diagnosis, inventory revision, hold, latest operation |
| GET | `/api/requests/{requestId}` | Scheduling status plus immutable ExecutionAssignment history when assigned |
| POST | `/api/projects/{projectId}/recoveries` | Start recovery (`StartProjectRecoveryRequest`: `InventoryRevision`, `Reason`, `IdempotencyKey`); `202` |
| GET | `/api/projects/{projectId}/recoveries/{recoveryId}` | Durable operation progress |
| POST | `/api/projects/{projectId}/recoveries/{recoveryId}/recheck` | New attempt (`ExpectedOperationVersion`, `IdempotencyKey`); does not resume the queue |
| POST | `/api/projects/{projectId}/recoveries/{recoveryId}/confirm-manual` | Administrator attestation; never `force=true` |
| POST | `/api/projects/{projectId}/recovery/resume` | Clear recovered hold (`OperationId`, `ExpectedHoldVersion`) |
| POST | `/api/requests/{requestId}/cancel` | Queued/unassigned work becomes `Cancelled` atomically; assigned request and assignment become `Cancelling` before best-effort owner notification and retain ownership until quiescence is confirmed |
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
| GET | `/usage` | Blazor remaining subscription windows; load and Refresh read the node cache |
| GET | `/statistics` | Blazor all-history session token and client-estimate cost totals |

| Hub | `/nodeHub` | Per-node authentication; request-scoped actions require the caller's ExecutionAssignment and token |

Every resource route is also exposed under `/api/v1` with bearer authentication. Cancellation
retries return the durable request and assignment state. An offline owner is not treated as a
delivery failure: its assignment stays `Cancelling`, and reconnect reconciliation orders
cancellation before the node can claim more work.

Blazor pages: `/` (fleet), project and request routes, `/attention`, `/usage`, `/statistics`, `/login`.


## Node system resources (Fleet `/`)

The node captures a **fail-closed, nullable** snapshot on the existing heartbeat only (`INodeSystemResourceMonitor.Capture` → `NodeWorker` → `NodeHub.Heartbeat`). No extra timer. Previous CPU counters live in process memory so utilization is an interval since the last tick; the **first** `CpuUsagePercent` after process start is `null`.

Sources (unprivileged): cgroup v2 via `/proc/self/cgroup` when the current cgroup has real charge/limits (`cpu.stat`/`cpu.max`, `memory.current`/`memory.max`); otherwise host procfs (`/proc/stat`, `/proc/meminfo`). Disk is `DriveInfo("/")` (`IsReady`, `TotalSize`, `AvailableFreeSpace`). Load and uptime are **host** `/proc/loadavg` and `/proc/uptime` field 1. Unlimited `cpu.max`/`memory.max` fall back to procfs, never a fake 1-CPU or 0-byte budget.

Wire/application types: `NodeResourceSnapshotMessage` / `NodeResourceSnapshotDto` — required UTC `ObservedAt`; nullable `CpuUsagePercent` (0–100), `MemoryUsedBytes`/`MemoryTotalBytes`, `DiskUsedBytes`/`DiskTotalBytes`, `LoadAverageOneMinute`, `UptimeSeconds`. Per-field null on missing file, parse failure, non-finite, negative, `used > total`, or CPU outside `[0, 100]`. Never reuse a stale reading. Control plane persists latest JSON only; `NodeDto.Resources` feeds `/`. Missing meters show **Unavailable**.

Research: [research/node-system-resource-monitoring.md](research/node-system-resource-monitoring.md).

## Demonstration vs Definition of Done

SPEC §43 / §46 require the first demonstration **through the web UI** (login, project, enqueue the health-details request, watch the agent tree, reservations, verification, completion). Operator commands live in the README demo section (`scripts/demo.sh`).

- `scripts/demo.sh` and `scripts/demo.sh --smoke` are quota-free, Control-Plane-only paths on loopback `127.0.0.1:${PI_CC_PORT:-5057}`. They may copy `demo/health-details-fixture` under the configured approved root, but their only API mutation is metadata-only Project registration: no WorkspaceBinding is designated or validated, no node is started, and queued work remains ineligible. Smoke uses temporary data and exits; default mode leaves the Control Plane running.
- A `RUN_REAL_*` opt-in also starts the authenticated node. After node registration, the script designates `Node__Id` plus the prepared fixture path as the Project's WorkspaceBinding and explicitly requests node-local validation. Live providers and request execution remain opt-in; registration, designation, and validation are **not** request completion. Completing SPEC §43 still means using the browser, not curl.
- Blazor surfaces: `/` fleet, `/projects/{id}` queue and composer, request page (plan, diff, verification, result, reservations, mail), `/attention`, `/usage`, `/statistics`.

