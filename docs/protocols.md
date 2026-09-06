# Protocols

Three versioned transports: Pi worker stdio (protocol v1), Muse Code host stdio (MSP v1), and Control Plane SignalR `/nodeHub`.

## Pi worker protocol v1

Process: `node` launches `runtime/pi-worker/src/index.ts` (`Pi:WorkerPath`, `Pi:NodeExecutable`).

- Stdin/stdout: strict NDJSON (LF; CRLF accepted on decode). Logs **only** on stderr.
- `protocolVersion`: `1` (`PROTOCOL_VERSION` / `PiProtocol.Version`).
- Maximum frame size: **1 048 576 bytes (1 MiB)** UTF-8, **excluding** the trailing newline (`MAX_FRAME_BYTES` / `PiProtocol.MaxFrameBytes`). Oversized frames fail with `FRAME_OVERSIZED` before JSON parse.
- Split on `\n` only (not Unicode line separators).
- Malformed frames: `FRAME_EMPTY`, `FRAME_INVALID_JSON`, `FRAME_NOT_OBJECT`, `FRAME_UNSUPPORTED_PROTOCOL_VERSION`, `FRAME_UNKNOWN_KIND`, `FRAME_MISSING_FIELD`. The worker logs and continues.

### Envelope

```json
{
  "protocolVersion": 1,
  "messageId": "01K...",
  "kind": "request",
  "sessionId": "session-01K...",
  "type": "session.start",
  "payload": {}
}
```

`messageId`, `kind`, `sessionId`, and `type` are required non-empty strings.

### Frame kinds

`hello`, `event`, `request`, `response`, `heartbeat`, `goodbye`.

Heartbeat while a session is active: 15 s from the worker. Node heartbeats to the hub are independent (`Node:HeartbeatSeconds`, default 10).

### Node → worker requests (correlated `response` by `messageId`)

Typical types: `session.start`, `session.input`, `session.cancel`, `session.snapshot`, `goodbye`. Input is acknowledged immediately; the SDK run continues in the background.

### Worker → node orchestration `request` types (SPEC §24.2)

`plan.submit`, `plan.revise`, `agent.spawn`, `agent.status`, `agent.await`, `agent.message.send`, `agent.inbox.read`, `agent.message.acknowledge`, `agent.cancel`, `reservation.acquire`, `reservation.expand`, `reservation.release`, `reservation.handoff.request`, `project.diff.inspect`, `verification.request`, `verification.intermediate.request`, `request.block`, `request.complete`.

`verification.request` is parameterless final verification. Correlation and assignment context come from the session transport. Legacy `profileId` and `commandId` payload fields are accepted for one compatibility release, ignored, and recorded as a deprecation diagnostic. New workers omit both. `verification.intermediate.request` is the child intermediate check and must never be treated as final verification.

### Events

Each `event` carries a strictly increasing per-session `seq` and a unique `messageId`. Text/thinking deltas may coalesce; lifecycle, blocker, reservation, error, and completion events are never dropped. Process crash synthesizes `session.failed` / `session.closed`.

Provider login missing: emit `session.snapshot` or `session.failed`-equivalent with blocked + input-required dimensions (see [security.md](security.md)).

## Muse Code host protocol (MSP v1)

Process: the node launches the official `muse` executable (`Muse:Executable`) as `muse serve --disable-write --disable-shell --no-session-log`, one host per `muse/<model-id>` session, with the project repository as the working directory. Argv is explicit (`ArgumentList`, no shell) and never carries `--yolo`, `--disable-sandbox`, `--api-key-stdin`, a login/logout/auth subcommand, or a credential.

- Stdin/stdout: newline-delimited JSON-RPC 2.0 (LF). Stderr is retained as a bounded tail (`Muse:MaxStderrLines`, default 200) for diagnostics only.
- Envelope schema version: `1` (`MuseProtocol.SchemaVersion`). A host reporting another version fails the start closed; a stable-surface fingerprint other than the verified Muse Code 1.0.3 one is logged as a warning and the session continues on the v1 envelope.
- Maximum frame size: `Muse:MaxLineBytes` (default **1 048 576 bytes**, excluding the newline). An oversize or malformed frame, or a response carrying neither `result` nor `error`, is a protocol fault: pending requests are faulted and the host is terminated.
- Unsolicited server → client requests are answered with JSON-RPC `-32601` (`methodNotFound`); the host is never given an approval or input channel.
- Every mutating request carries a UUIDv7 `commandId` as an idempotency handle.

### Methods the node speaks (and nothing else)

| Method | Direction | Purpose |
|---|---|---|
| `initialize` → `initialized` | request, then notification | Handshake with `clientInfo.name` `devfleet`; verifies the envelope schema version and reads the fingerprint. |
| `session/start` | request | `workspaceRoot`, `approvalMode: denyUnmatched`, and `modelId` set to the selector's native model id (always forwarded; never omitted). Result `session.sessionId` becomes `ProviderSessionId`; `modelId`/`providerId` are echoed into the `session.registered` event. |
| `turn/start` | request | `input: [{type: "text", text}]`. The first call carries the spawn prompt; every later `SendAsync` is another `turn/start`, queued by the host behind a running turn. Result `turnId`/`disposition` are emitted as `turn.submitted`. |
| `turn/cancel` | request | Cancels the foreground turn for `sessionId`/`turnId`. If the host does not settle within `Muse:CancelGraceSeconds` (default 5) it is terminated. |
| `view/unsubscribe` | request, best effort | Sent on close. MSP has **no session close method**: close is `view/unsubscribe` followed by SIGTERM and a process-tree kill within the grace period. |
| `model/list` | request | Discovery only (`IMuseModelCatalogReader`): a separate host is started, handshaken, asked `model/list {}`, and terminated. The reader unions that live list with the four supported IDs `muse-spark-1.3`, `muse-spark-1.3-contributor`, `muse-spark-1.2`, and `muse-spark-1.2-contributor` (Muse's bundled catalog can be incomplete), prefixes them as `muse/<model-id>`, and deduplicates deterministically while keeping extra valid live IDs. A model id of `default` is never added to this catalog. Never starts a session. |

Timeouts: `Muse:StartTimeoutSeconds` (default 30) bounds handshake + `session/start` + first `turn/start`; `Muse:RequestTimeoutSeconds` (default 30) bounds any later acknowledgement.

Turn notifications from the host are normalized into the shared event contract (`session.registered`, `turn.submitted`, `session.snapshot`, `session.failed`, `session.cancelled`, …). A host exit whose stderr tail matches the Muse/Meta login phrasing becomes a blocked + input-required snapshot with the fixed reason `Complete Muse Code login locally (muse login)`; event reasons are fixed sentences, and the bounded stderr tail appears only as the `stderrTail` field of a non-auth `session.failed`. There is no usage, quota, or auth-plan method on this surface, and the node never asks the host for one.

## SignalR node hub (`/nodeHub`)

Outbound from the node (`Node:ControlPlaneUrl` + `/nodeHub`, default `http://127.0.0.1:5057/nodeHub`). Plain HTTP is permitted only when the endpoint is positively verified as loopback; this is the explicit local-development exception. Every non-loopback connection uses HTTPS/WSS with normal certificate-chain and hostname validation. The node rejects plaintext remote URLs, TLS-to-HTTP downgrade redirects, and certificate-validation bypasses before sending its authentication credential or assignment data.

Each node has a distinct, manually provisioned credential whose authenticated principal contains its stable `NodeId`. Credential distribution is not a hub operation. `Register` does not choose identity: any identity metadata must agree with the principal, the connection is bound to the principal's `NodeId` for its lifetime, and every later hub operation derives node identity from the connection rather than trusting a DTO body field. A credential shared by the fleet plus a caller-supplied `NodeId` is not multi-node authentication. Replacing a connection for one node changes neither assignment ownership nor writer authority.

Hub methods (one-argument DTOs in `PiCommandCenter.Contracts.NodeTransport`):

| Hub method | Request DTO | Result |
|---|---|---|
| `Register` | `NodeRegistrationMessage` (display name, agent version, capabilities, typed execution status; identity is connection-derived) | `NodeDto` |
| `Heartbeat` | `NodeHeartbeatMessage` (active assigned session ids, optional `Resources`, typed execution status; identity is connection-derived) | `NodeDto` (`Resources` echoed from latest stored snapshot) |
| `ReconcileAssignments` | Durable local assignment inventory (assignment id, token, binding revision, process and repository status) | Per-assignment continue, renew, cancel, or `RecoveryRequired` decisions |
| `ClaimNext` | `ClaimRequestMessage` (lease request; identity is connection-derived) | `ExecutionAssignmentMessage?` with assignment id, workspace binding id/revision, immutable canonical path/default-branch snapshot, token, and lease |
| `RenewClaim` | `ClaimRenewalMessage` (assignment/request id, claim token, requested lease) | `DateTimeOffset` |
| `PublishEvents` | `NodeEventBatchMessage` (assignment-authorized events) | `NodeEventAcknowledgementMessage` |
| `SendMail` / `ReplyMail` | `SendMailMessage` / `ReplyMailMessage` | `MailDeliveryMessage` |
| `FetchInbox` / `FetchThread` | `FetchMailInboxMessage` / `FetchMailThreadMessage` | `MailInboxMessage` |
| `MarkMailRead` / `AcknowledgeMail` | `MarkMailReadMessage` / `AcknowledgeMailMessage` | `MailReceiptMessage` |
| `AcquireReservation` | `AcquireReservationMessage` | `ReservationOperationResultMessage` |
| `RenewReservation` / `ReleaseReservation` | `ReservationMutationMessage` / `ReleaseReservationMessage` | `ReservationOperationResultMessage` |
| `ExpandReservation` | `ExpandReservationMessage` | `ReservationOperationResultMessage` |
| `TransferReservation` | `TransferReservationMessage` | `ReservationOperationResultMessage` |
| `AuthorizeMutation` | `MutationAuthorizationMessage` | `MutationAuthorizationResultMessage` |
| `MarkReservationRecovery` | `MarkRecoveryMessage` | `ReservationOperationResultMessage` |
| `ListReservations` | `ListReservationsMessage` | `ReservationLeaseMessage[]` |
| `RecordVerification` | `VerificationRunMessage` | `VerificationRunMessage` |
| `EvaluateCompletion` | `EvaluateCompletionMessage` | `CompletionGateDecisionMessage` |
| `ReportRecoveryProgress` | `AssignmentRecoveryProgressMessage` | none (void) |
| `ReportRecoveryProof` | `AssignmentRecoveryProofMessage` | `RecoveryProofDecisionMessage` |


Server-to-node `CancelAssignmentCommand` targets the retained assignment by request id. It is
best-effort only: the control plane commits the request and assignment as `Cancelling` first.
`ReconcileAssignments` returns the `Cancel` disposition for an owner's matching durable inventory
even when that inventory still reports the pre-disconnect running state. The node journals the
authoritative `Cancelling` snapshot before stopping the root and invokes the same quiescence
terminalizer used by live cancellation; it never resumes or reclaims that request.

Server-to-node `RecoverAssignment` (`RecoverAssignmentCommandMessage`) is likewise fire-and-forget `SendAsync` on the node's current connection. Delivery failure persists `NeedsIntervention` without clearing the hold. `Register` rebinds the connection and redelivers open recovery targets (`DispatchForNodeAsync`) before claims.

An assignment claim is an atomic scheduling decision, not discovery of a path. The control plane rechecks the current binding revision, authenticated node, fresh execution readiness, capacity, project policy, concurrency, request state, and absence of an existing assignment before it commits the assignment and moves the request to `Starting`. The node persists the returned assignment identity and token before launching work.

Before `ClaimNext` after any connection or control-plane restart, the node reconciles its owner-only durable assignment inventory. Disconnect or heartbeat/lease expiry changes liveness and may require reconciliation; it never releases an assignment or authorizes another node to claim the request. A terminal, failed, or cancelled assignment remains the durable placement and authorship record.

The initial phase has at most one designated WorkspaceBinding per Project and no repository mobility. Another node's checkout is not interchangeable and does not become eligible because it has the same project id or path text.

`Heartbeat` is not session-ids-only. `NodeHeartbeatMessage.Resources` is a `NodeResourceSnapshotMessage`: required UTC `ObservedAt`; nullable `CpuUsagePercent`, `MemoryUsedBytes`, `MemoryTotalBytes`, `DiskUsedBytes`, `DiskTotalBytes`, `LoadAverageOneMinute`, `UptimeSeconds`. The node fills it from `INodeSystemResourceMonitor.Capture()` on the existing `Node:HeartbeatSeconds` tick (no extra poll). First CPU sample is `null`. Hub maps the snapshot onto `NodeResourceSnapshotDto`; `NodeRegistry` validates (`ObservedAt` offset zero; CPU finite in `[0, 100]` or null; load/uptime finite ≥ 0 or null; byte used ≥ 0, total > 0, and `used ≤ total` when both set) and stores **latest JSON only** on `FleetNode.ResourceSnapshotJson`. Invalid snapshots fail the heartbeat; omitted `Resources` persists null. Application `NodeDto.Resources` is the same shape. Sampling semantics: [research/node-system-resource-monitoring.md](research/node-system-resource-monitoring.md).

`NodeExecutionStatusMessage` is separate from resource telemetry. It reports available request slots; active and recovering assignment ids; a bounded verification-policy summary (baseline availability/version and trusted profile ids/revisions that passed startup validation); and per-route adapter-observed runtime and authentication readiness (`Ready`, `Unavailable`, or `Unknown`) with a stable evidence source, UTC observation time, and routing revision. Only fresh `Ready` observations are schedulable. Executable presence, catalog membership, static model aliases, and credential-file presence are not authentication evidence; unsupported, stale, or unknown observations fail closed. Provider credentials, credential contents, and raw provider output never appear in registration, heartbeat, callbacks, or other SignalR payloads.

These are SignalR transport additions. The TypeScript Pi worker NDJSON protocol remains version 1.


Hub methods are **node → control plane**. Node-local usage, runtime routing, discovery, workspace directory browse, and workspace validation are the reverse: the hub **invokes client callbacks** on the authenticated connected node (no hub methods of the same names).

| Client callback | Arguments | Result |
|---|---|---|
| `GetSubscriptionUsage` | none | `NodeSubscriptionUsageMessage` (`Providers`) |
| `GetRuntimeConfiguration` | none | `NodeRuntimeConfigurationMessage` (`AllowedRoles`, `RoleRoutes[]{Role, Candidates[]{Model}}`) |
| `DiscoverRuntimeModels` | none | `RuntimeModelCatalogMessage[]` (`Provider`, `Models[]{Id, DisplayName, Provider}`, `Error`) |
| `UpdateRuntimeConfiguration` | `UpdateNodeRuntimeConfigurationMessage` (`RoleRoutes`) | `NodeRuntimeConfigurationMessage` |
| `ValidateWorkspaceBinding` | `WorkspaceBindingValidationRequestMessage` (`BindingId`, `ProjectId`, `Revision`, node-local path, default branch) | Canonical path plus structured bounded preparation classification (status/code/detail) for the same revision |
| `BrowseWorkspaceDirectories` | `WorkspaceDirectoryBrowseRequestMessage` (`Path?`) | `WorkspaceDirectoryBrowseResponseMessage` (`CurrentPath?`, `ParentPath?`, `Directories[]{Name, Path}`, `ErrorCode?`, `ErrorDetail?`) |
| `GetVerificationPolicyCatalog` | none | `VerificationPolicyCatalogMessage`: bounded baseline version plus trusted profile ids/revisions/labels and command ids, working-directory labels, mandatory/optional flags, and timeout budgets. No executable path, environment value, credential, or raw argv. |
| `ValidateVerificationProfileSelection` | `VerificationProfileSelectionRequestMessage` (Project id, WorkspaceBinding id/revision, selected profile id/revision or none) | Bounded acceptance/code/detail for an exact match against the designated node's current catalog; the control plane persists an accepted selection. |

`ValidateWorkspaceBinding` is invoked only on the connection authenticated as the binding's node. The node applies its own `Projects:ApprovedRoots` and filesystem/Git checks. The control plane accepts a result only for the requested binding, authenticated node, and still-current revision; stale or cross-node results fail closed. `ApprovedRoots` is node configuration because the path namespace and the directory exist on that node, not on the control plane.

The result classifies how much local Git preparation the designated directory needs; it is not a checkout test, and it changes nothing on disk. Three classifications report status `valid` with the canonical path: `valid` (repository with commits on the configured default branch, nothing to prepare), `repository_initialization_required` (ordinary directory with no repository), and `baseline_commit_required` (repository with an unborn `HEAD`). Everything else is `invalid` with a stable code — `path_missing`, `path_not_directory`, `path_outside_approved_root`, `path_symlink`, `path_not_writable`, `git_unavailable`, `nested_in_parent_repository`, `not_git_repository` (broken `.git` metadata or a gitfile worktree), `default_branch_missing`, `unreadable`, `invalid_request` — plus bounded detail.

Preparation itself is node-local and assignment-scoped: after the claim result is journaled durably and before baseline capture, request-branch creation, and root start, the supervisor calls `ITrustedGitService.PrepareWorkspaceAsync` with the assignment's canonical path and default branch. It initializes the repository and/or commits the directory's existing non-ignored contents with the message `Initialize workspace for DevFleet` under a fixed command-local identity (`DevFleet Supervisor <devfleet@localhost>`, passed per command), and does nothing when the workspace already has commits. Preparation and `CreateRequestBranchAsync` are idempotent, so a retry on the same assignment is safe; a preexisting request branch that has diverged from the default-branch tip is an error rather than a silent reuse. No preparation state crosses the hub except the resulting request events.

If any startup step fails, including preparation, the node journals `StartBlocked` and spools one assignment-scoped `request.blocked` event with the failing phase and bounded reason. No session is created and the event has no session identity. The assignment is retained and retryable on the same assignment, reconciles as retained rather than `RecoveryRequired`, and cancellation of a `StartBlocked` or `Starting` assignment with no root still proves quiescence before terminalizing.

`BrowseWorkspaceDirectories` is invoked only on the selected authenticated node. Blazor calls `INodeWorkspaceDirectoryGateway.BrowseAsync` in the control-plane process; there is no HTTP browse route. A null `Path` lists configured approved roots (`CurrentPath` and `ParentPath` null). A successful directory listing returns the canonical absolute `CurrentPath`, `ParentPath` null when that path is an approved root, and sorted direct child directories only (no files, no symlink entries, no parent traversal above an approved root). Errors use stable codes `invalid_path`, `path_missing`, `outside_approved_root`, and `unreadable`, with no entries and bounded operator-safe detail (512 characters). Results are capped at 500 entries. An offline node is an error. Browse never inspects Git state, so the designation UI cannot know whether a folder is already a repository and always shows the operator-consent warning before designation.

Every event and control for assigned work is authorized against the retained `ExecutionAssignment`: authenticated connection `NodeId`, assignment id and token, workspace binding revision, session, request, and project must agree. This gate covers renewal, event publication, heartbeat session membership, reservations, mutation authorization, verification, completion, mail, cancellation, repository/Git operations, and creation of root or child sessions. Cancellation routes directly to the assigned node or an assignment-gated session group; a heartbeat cannot subscribe a node to a foreign session.

Terminal assignments may acknowledge duplicate event ids and accept bounded final/history events from their recorded sessions, but historical ingestion never reopens execution or authorizes mutations, new sessions, reservations, Git, or verification. Completion, failure, and cancellation release writer ownership only after assignment-bound quiescence is proven; expiry alone never does.

`Model` is always a canonical `<provider>/<model>` selector whose prefix is lowercase ASCII alphanumeric with interior hyphens (e.g. `codex/gpt-5.6-sol`, `claude-code/fable-5-1`, `zai/glm-4.7`); the prefix `pi` is rejected because Pi is a runtime, not a provider. The model id after the first `/` must be an explicit native id: `AgentModelSelector.TryParse` rejects a model id that is exactly `default`. There are no runtime profiles on the wire; the prefix alone selects the adapter — the reserved prefixes `claude-code`, `antigravity`, and `muse` pick their official-harness adapters and every other valid prefix runs through Pi — and the `muse` and `antigravity` adapters are read-only regardless of route position. Adapters always pass the native model id through to the provider. The node rejects an update naming a role outside `AllowedRoles`, a non-canonical or duplicate candidate, or more than 16 candidates for one role, and answers with the persisted normalized routes on success. Persisted role-route overrides containing a deprecated `<provider>/default` selector are discarded and replaced by the configured explicit routes.

Pi is the runtime adapter for every non-reserved provider: `codex` aliases the Pi SDK provider `openai-codex` (so `codex/gpt-5.6-sol` decodes as Pi's `openai-codex/gpt-5.6-sol`), and every other Pi provider prefix passes through identically (`zai/glm-4.7` resolves to Pi's `zai/glm-4.7`). Any other syntactically valid provider goes only to Pi and fails closed unless that provider is authenticated and available to the Pi worker.

`DiscoverRuntimeModels` answers one `RuntimeModelCatalogMessage` per provider. The Claude Code catalog is a DevFleet-maintained set of stable aliases (`fable`, `sonnet`, `opus`, `haiku`) plus canonical Claude selectors already configured in role routes; Claude Code cannot export its authenticated picker. Catalog matching is exact; a model id of `default` is not a catalog entry. The `muse` catalog comes from `IMuseModelCatalogReader` (`model/list` on a fresh read-only host, unioned with the four concrete IDs `muse-spark-1.3`, `muse-spark-1.3-contributor`, `muse-spark-1.2`, and `muse-spark-1.2-contributor` because Muse's bundled MSP catalog can be incomplete, then canonicalized as `muse/<model-id>` and deduplicated while preserving additional valid live IDs); a reader error, or a read that yields no canonical `muse/` selector, is reported in `Error` with an empty `Models` list rather than as an empty success.

Pi discovery returns one catalog per authenticated Pi provider (not only OpenAI Codex): each catalog's `Provider` is the selector prefix for that provider — `codex` for the Pi SDK provider `openai-codex`, the Pi provider id itself for every other (e.g. `zai`) — and each reported model id is a canonical selector under that prefix that decodes back to a runnable Pi model. A Pi discovery process failure is reported as the `codex` catalog's `Error`.

The `DiscoverRuntimeModels` wire shape is unchanged now that discovery is a background cache. `RuntimeModelDiscovery` collects Pi, Antigravity, and Muse results once immediately at node startup and then refreshes them on a non-overlapping five-minute cadence; the callback returns the latest completed snapshot and never starts discovery processes. Provider-level error catalogs are completed results and follow the same cadence. A failed refresh keeps the last completed snapshot; before the first completed refresh, a callback waits for initial data and honors caller cancellation. Claude aliases and configured selectors are recomputed from live routing on every callback. The cache is process-local and starts empty after a node restart.

`NodeSubscriptionUsageGateway` uses `IHubContext<NodeHub>.Clients.Client(connectionId).InvokeCoreAsync<NodeSubscriptionUsageMessage>("GetSubscriptionUsage", [])` with a 35 s timeout. Browser load and manual Refresh on `/usage` both issue that callback; it returns the node's latest in-memory cache and never starts a sidecar, HTTP call, or CLI. The node collects once immediately via `IRuntimeSubscriptionUsageProbe` (unchanged, sole collector) and then refreshes every five minutes (product policy, not a configuration knob). Successful snapshots atomically replace the cache; a failed refresh keeps the last successful snapshot. Before the first successful refresh, the callback waits for initial data and honors caller cancellation. There is no persistence and no new wire message. Pi remains the production orchestrator. Each background collection starts the Pi sidecar and the ordered `ISupplementalSubscriptionUsageSource` readers concurrently with one `ObservedAt`. Sidecar order is preserved; a non-null supplement replaces the same exact provider id or appends in registration order. One supplemental failure is omitted without suppressing sidecar or sibling results.

The sidecar JSON allowlist remains `openai-codex`, `anthropic`, `kimi-code`, `zai`, `xai-oauth`, and `opencode-go`; it does **not** accept `google-antigravity`. Registered provider-native supplements are Anthropic (`SubscriptionUsage:ClaudeCredentialPath` → exact Anthropic OAuth usage/token origins) followed by Google Antigravity (official `agy --version`; `agy -p /usage --print-timeout 8s`). Their final DTO ids are `anthropic` and `google-antigravity`.

`ProviderSubscriptionUsageMessage.Status`: `available` requires at least one validated remaining-quota window; closed sources have empty `Windows` and stable diagnostics. `Diagnostic` must match `^[a-z0-9_]{1,40}$`. `Windows` cross the hub already normalized (`Name`, `PercentUsed`, `PercentRemaining`, `ResetsAt`) on the 0–100 scale, and at most **8** per provider. `ObservedAt` is the cached snapshot's observation time and exposes data age. The node never forwards credential contents, provider response bodies, tokens, account/user IDs, PII, or raw CLI output. `Source` and `Diagnostic` are stable, secret-free labels. Full source and fail-closed rules are in [architecture.md](architecture.md#subscription-usage-usage) and [research/subscription-usage.md](research/subscription-usage.md). Version/auth/plan fields are not remaining quota. Cursor and Muse Code have no provider row.

### Server-enforced bounds (`NodeTransportLimits`)

| Limit | Value |
|---|---|
| Claim/reservation lease seconds | 10–300 |
| Event batch count | 500 |
| Event payload bytes | 256 KiB |
| Active session ids on heartbeat | 200 |
| Resource snapshot numeric fields | finite; CPU 0–100; bytes non-negative with `used ≤ total`; `ObservedAt` UTC |
| Mail payload | 64 KiB |
| Inbox count | 200 |
| Session / verification id length | 128 |
| Verification output | 16 384 bytes |
| Artifact path | 1024 bytes |
| Completion summary | 64 KiB |
| Changed files / review findings | 500 / 200 |
| Recovery claim token | 128 |
| Recovery stage | 128 |
| Recovery reason codes | 16 × 64 |
| Recovery process identities | 32 |
| Recovery reservation dispositions | 32 |
| Recovery interrupted-operation indicators | 16 |
| Recovery summaries (HEAD/branch/index/worktree/group) | 256 |

Reservation errors are in-band (`ReservationErrorCodes`: `conflict`, `not_found`, `invalid_fencing_token`, `invalid_state`, `validation`, `unknown`), not raw hub exceptions. Stale fencing tokens fail mutation authorization.

### Idempotency

- `PublishEvents`: duplicate `EventId`s are acknowledged and not re-inserted (`NodeEventSink`).
- `MarkMailRead`: idempotent per recipient session.
- Reservation acquire of identical scopes for the same owner follows lease semantics (conflict vs existing lease), never silent double-grant of overlapping scopes to two owners.
- Completion evaluation is keyed by request; accepted results persist once.
- Recovery start/recheck/`confirm-manual` keys are `RecoveryIdempotencyKeys` per Project/action/key; same key and hash is a replay, different hash is `409`. Start input is `InventoryRevision` plus `IdempotencyKey` (no operator `Reason`). `confirm-manual` hashes evidence and fence fields, not a typed reason.
- Concurrent `RecoverAssignment` for the same recovery id and attempt shares one node task; a later attempt waits for the prior attempt to finish.
- `ReportRecoveryProof` for an already-terminalized target with recorded outcome is accepted without reopening execution.

### Event message

`NodeEventMessage`: `EventId`, `ExecutionAssignmentId`, `ProjectId`, `RequestId`, `SessionId?`, `Sequence`, `Type`, `OccurredAt`, `PayloadJson`. The authenticated connection supplies node authorship; the retained assignment supplies placement and validates every correlation.

After reconnect, the node first submits its durable assignment inventory for reconciliation, then replays inventory snapshots and unacknowledged events in order. Events are deleted from the local spool only after acknowledgement. Reconciliation and replay precede new claims.

## Project recovery transport

HTTP start is `POST /api/projects/{projectId}/recoveries` with `StartProjectRecoveryRequest` (`InventoryRevision`, `IdempotencyKey`). HTTP `confirm-manual` drops `Reason` and retains every evidence/fence field. At the HTTP/UI trust seam the control plane passes a fixed server-authored audit reason into application commands: `Administrator requested project recovery.` on start, `Administrator confirmed manual recovery after evidence review.` on manual confirm. Actor is the authenticated principal, never a body field.

Control plane → node: `RecoverAssignment` with `RecoverAssignmentCommandMessage`. Recovery always stops/cancels; it never resumes interrupted execution. The claim token remains a fence: the node may act only while it matches current assignment authority. `Register` rebinds the connection and `RecoveryAttemptDispatcher.DispatchForNodeAsync` redelivers open-target commands after reconnect, before `ClaimNext`.

Node → control plane: `ReportRecoveryProgress` (`AssignmentRecoveryProgressMessage`) and `ReportRecoveryProof` (`AssignmentRecoveryProofMessage`). Identity is the authenticated connection, never a payload `NodeId`. Correlation requires current `RecoveryId`, `Attempt` ≥ 1, `ProjectId`, `RequestId`, bounded `ClaimToken`, `BindingRevision`, and `ObservedAt`.

`RecoveryKnownCountMessage` is either a nonnegative `Value` with a blank unknown code, or a null value with an explicit `UnknownReasonCode`. Known zero is empty, not unknown. Unknown inventories cannot authorize release.

Proof additionally carries `AdmissionClosed`, event acknowledgement position or unknown code, `RecoveryProcessIdentityMessage` rows (`Pid`, `StartedAt`, `GroupOrScopeId`, `EscapedDescendant` — no command lines, environment, or secrets), `RecoveryReservationDispositionMessage` rows (`LeaseId`, bounded `Disposition`, optional `ReasonCode` — never a fencing secret or path dump), and `RecoveryRepositoryStatusMessage` (availability, HEAD/branch, index/worktree summaries, untracked known-count, interrupted-operation indicators). File contents, diffs, credentials, and unbounded logs are prohibited.

The control plane accepts proof only when every inventory is known and zero, admission is closed, correlation matches the current attempt, the claim token still fences the assignment, and the repository snapshot is present. Stale attempt/observation is `recovery_evidence_stale`. Mismatched target is `recovery_target_changed`. Rejection lists missing requirements in `RecoveryProofDecisionMessage`.

Linux workers start under `setsid`. Journaled identity is PID plus `/proc/<pid>/stat` start ticks plus process group and session. Stop enumerates `/proc` by session/group. A PID whose start ticks no longer match is reuse (exited). Escaped descendants are listed, not ignored. Non-Linux or missing `/usr/bin/setsid`/`/bin/setsid` reports `process_stop_unproven`. Tree kill is not proof. Events are flushed without deleting `Node:EventSpoolPath`. Reservation disposition is recorded after stop evidence; force-release is not recovery proof.
