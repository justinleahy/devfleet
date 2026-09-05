# Fleet-owned projects and request-time execution assignment

**Status:** Proposed design; no implementation authorized
**Scope:** Decouple project identity from execution placement while retaining one designated canonical workspace per project
**Review state:** Requirements and security reviews completed with no remaining blockers; awaiting operator acceptance. The corresponding `TODO.md` item remains open.

## Decision

A **Project** belongs to the fleet. A **WorkspaceBinding** records one node-local checkout that the operator has explicitly designated and that the node has validated. An **ExecutionAssignment** is the durable, request-specific authorization for one node to run one request in one workspace.

The initial phase permits zero or one WorkspaceBinding per Project. It does not make clones interchangeable and does not move work. Request-time assignment is still valuable because it removes ownership from Project, records where every request ran, gives recovery one authoritative owner, and establishes the atomic seam needed for future scheduling without pretending repository mobility exists.

## Sources reviewed

- `README.md`
- `SPEC.md`, especially §§3.1–3.7, 7–12, 17–19, 21–23, 29–31, 41–46
- `docs/architecture.md`
- `docs/protocols.md`
- `docs/security.md`
- `docs/design/fleet-owned-projects-prompt.md`
- Current project, request, persistence, node transport, worker, reservation, authentication, and Blazor code and tests under `src/` and `tests/`

Prior session history was consulted with `cass`. It confirms the earlier product decision: project identity, workspace readiness, and execution placement are separate; repository distribution remains deferred. No prior implementation was found.

## Current discrepancies

| Requirement | Current implementation | Consequence |
|---|---|---|
| Project may exist without a node or checkout | `Project.Register` requires `NodeId` and `RepositoryPath`; `ProjectDto` exposes both as required (`src/PiCommandCenter.Domain/Projects/Project.cs`, `src/PiCommandCenter.Application/Projects/ProjectDto.cs`) | Missing placement is unrepresentable. |
| Registration is fleet metadata validation | `ProjectCatalog.RegisterAsync` validates the control-plane filesystem and Git, then `ResolveNodeId()` uses configuration or a SHA-256 machine-name fallback (`src/PiCommandCenter.Infrastructure/Projects/ProjectCatalog.cs`) | Registration requires a checkout and can fabricate a node identity that has never authenticated. |
| Workspace validation is performed by the node that owns the path | `IProjectCatalog.ValidateAsync` and `POST /api/projects/{id}/validate` run path and Git checks in the control-plane process (`src/PiCommandCenter.Application/Projects/IProjectCatalog.cs`, `src/PiCommandCenter.Api/ProjectsEndpoints.cs`) | The validating machine may not be the execution node. |
| No eligible node leaves a request queued with a reason | `RequestClaimService.ClaimNextAsync` returns `null`; `WorkRequestDto` has no scheduling reason | UI cannot distinguish no binding, invalid path, offline node, runtime, capacity, or policy. |
| Eligibility is stronger than connected or idle | Claim checks only registered node, `Project.NodeId`, project enabled, and partial project capacity (`src/PiCommandCenter.Infrastructure/Requests/RequestClaimService.cs`) | It ignores liveness, workspace validation, runtime readiness, server-side node capacity, and most execution policy. |
| Configuration exposes `MaxActiveWriteRequests` above one | `SPEC.md` §5.1 and the shared canonical checkout require one active development request; branch creation performs checkout in that workspace | The initial phase must keep an effective write cap of one regardless of a larger configured value. Higher write concurrency requires an isolation design outside this scope. |
| Lease expiry must not authorize another writer | Capacity counts only unexpired claims; `NodeWorker` drops a rejected renewal locally (`RequestClaimService`, `src/PiCommandCenter.Node/NodeWorker.cs`) | An expired claim can stop occupying a project slot while its request/session may still be running or uncertain. This is an unsafe second-writer path. |
| Assignment is durable history | `RequestClaim` mixes placement and a renewable lease; UI has no assignment projection | Placement cannot be presented as a first-class historical fact. |
| Authenticated node identity is authoritative | One shared bearer token authenticates the node role; each hub message self-asserts `NodeId` (`NodeTokenAuthenticationHandler`, `NodeHub`, node transport DTOs) | Any holder of the shared token can impersonate another node id. |
| Node events and controls are assignment-authorized | `PublishEvents` persists payload `NodeId`; heartbeat session ids determine SignalR groups; reservations and completion validate correlation but not assignment ownership | A compromised or buggy authenticated node can claim foreign authorship or join a foreign session group. |
| UI treats binding as readiness | `Home.razor` registration requires a path and says registration binds the project to a node; `Project.razor` always renders node/path | Fleet-owned and unbound states cannot be shown honestly. |
| Product documentation is fleet-oriented | `SPEC.md` §§3.6, 7, 10, 29, and 31 and `docs/architecture.md` still define a project as a node-owned repository | The new requirement intentionally supersedes those passages; they must be revised before implementation is considered conformant. |

Existing strengths to preserve: `RequestClaimService` uses a serializable transaction and a unique request key; event insertion is idempotent; reservation expiry enters `RecoveryRequired`; reservation fencing is project-scoped; provider and Git processes remain node-owned; queues and history already persist in SQLite.

## Domain relationships

```text
Fleet
  └── Project 1
        ├── 0..1 WorkspaceBinding (initial phase)
        │       └── exactly 1 authenticated Node identity
        ├── 0..* WorkRequest
        │       └── 0..1 ExecutionAssignment
        │               └── exactly 1 WorkspaceBinding snapshot
        ├── project-scoped reservation fencing counter
        └── request/session/message/verification/result history

Node
  ├── 0..* WorkspaceBinding
  └── 0..* active or recovery-required ExecutionAssignment
```

### Project

Stable fleet-owned aggregate:

- `ProjectId`
- display name
- enabled flag
- request and child concurrency policies
- default branch and Git policy
- timestamps and optimistic version

It has no `NodeId`, repository path, or repository URL. Zero bindings is normal. Disabling a project prevents new assignments; it does not revoke an active writer without cancellation and recovery.

### WorkspaceBinding

Initial aggregate with one row at most per Project:

- `WorkspaceBindingId`
- `ProjectId`
- `NodeId`
- node-local canonical absolute `RepositoryPath`
- `Status`: `PendingValidation`, `Valid`, `Invalid`
- stable `ValidationCode` and bounded operator-safe detail
- `ValidatedAt`, `ValidationRevision`, timestamps, optimistic version

Liveness is not binding status. A valid binding can be temporarily ineligible because its node is offline. A missing path is a validation result, not proof that the Project is invalid.

The node validates existence, directory type, approved-root containment, symlink rules, Git repository status, Git availability, default branch, readability, and the no-application-worktree rule. Editing `NodeId`, path, branch policy, or validation inputs increments the binding revision and returns it to `PendingValidation`. A response for an older revision is ignored.

### ExecutionAssignment

Replace the current `RequestClaim` concept with one durable assignment record per request:

- `RequestId` primary key and `ProjectId`
- `WorkspaceBindingId`
- immutable assignment snapshot: `NodeId`, canonical path, default branch, binding validation revision
- `AssignedAt`
- `State`: `Starting`, `Running`, `Finalizing`, `Cancelling`, `RecoveryRequired`, `Completed`, `Failed`, `Cancelled`
- claim token, lease expiry, last renewal/reconciliation time
- terminal time and optimistic version

The assignment row is never deleted merely because its lease expires. The lease says the control plane has recent proof from the assigned supervisor; it is not a timeout-based transfer of write ownership. Historical projections read the immutable snapshot, not a later-edited binding.

A retry that may execute elsewhere is a new WorkRequest linked as a continuation, not mutation of an old request's assignment history.

## Invariants

1. Project identity and queue operations never require a Node or WorkspaceBinding.
2. The initial phase has at most one designated WorkspaceBinding per Project. Only a designated binding whose current revision is `Valid` is eligible for execution.
3. An unassigned request is `Queued`; a queued waiting reason is scheduling metadata, not `Blocked` or `Failed` work.
4. Assignment creation, request `Queued → Starting`, claim-token creation, and capacity checks commit atomically.
5. A request has at most one ExecutionAssignment. The assigned node and workspace are immutable for that request.
6. Every root and child session, reservation, verification run, event, repository read/write, and Git operation must correlate to that assignment.
7. All children execute on the assignment's node and canonical path. No child-level placement exists.
8. The effective active development-request limit remains one per Project. Every nonterminal development assignment, including `Finalizing`, `Cancelling`, and `RecoveryRequired`, occupies that slot; lease expiry never frees it. Existing read-only limits remain policy inputs.
9. Completion, failure, or cancellation acceptance does not release ownership. A terminal assignment requires assignment-bound proof that admission is closed and all root/child, mutation, verification, Git, and supervised process activity is quiescent.
10. Node capacity is enforced by the control plane from nonterminal assignments and fresh advertised capacity, not only by the worker's local count.
11. Heartbeat expiry changes liveness and may move an assignment to `RecoveryRequired`; it never makes the request claimable by another node.
12. Reservation leases and fencing remain independent of the assignment lease. Recovery-required reservation scopes continue blocking reuse.
13. Only the assigned node's authenticated connection and claim token may renew, publish owned history events, operate reservations, record verification, evaluate completion, or receive cancellation for that request.
14. Project-wide Git state remains supervisor-owned. Assignment does not grant agents Git authority.

## Lifecycle

### 1. Register a Project

`POST /api/projects` accepts fleet metadata and policies only. Validation checks names, enum values, branch syntax, and numeric policy bounds. It does not touch a filesystem, invoke Git, require a node, or infer a node id. The Project can immediately be listed, opened, and accept requests.

### 2. Provision and validate a WorkspaceBinding

1. Operator chooses a known node and enters that node's local repository path.
2. Control plane creates or updates the sole binding as `PendingValidation` with a new revision.
3. If the node is offline, the binding remains pending. Project registration and enqueue still work.
4. Over the node transport, the control plane asks the selected authenticated node to validate `{bindingId, projectId, revision, path, defaultBranch}`.
5. The node runs the existing path/Git checks under node-local `Projects:ApprovedRoots` and returns a structured, bounded result.
6. The control plane accepts the result only from the connection authenticated as the binding's node and only for the current revision.
7. Success stores the node-returned canonical path and marks the binding `Valid`; failure stores a stable code and marks it `Invalid`.

A path string is meaningful only with its `NodeId`. Duplicate prevention is unique on `(NodeId, CanonicalRepositoryPath)`, not fleet-wide path text.

### 3. Enqueue

Enqueue requires only an enabled, existing Project and valid request content. No binding or connected node is required. The request remains `Queued` until an eligible claim commits.

### 4. Evaluate eligibility

Introduce one deep module, `IRequestEligibilityEvaluator`, used by both claim selection and scheduling projections. Its interface returns eligible candidates plus one deterministic waiting reason; callers do not duplicate policy.

All of these must hold at claim time:

- Project execution is enabled by policy.
- The sole designated WorkspaceBinding exists and is `Valid` at its current revision.
- The caller's NodeId equals the binding's NodeId.
- The hub connection is authenticated as that NodeId.
- Node status is online and its heartbeat/execution-status observation is fresh.
- For each required root and mandatory role route, the node reports fresh adapter-observed readiness: runtime availability, authentication state (`Ready`, `Unavailable`, or `Unknown`), stable evidence source, observation time, and routing revision. Catalog membership, executable presence, credential-file presence, and static aliases are not authentication evidence. Only `Ready` is eligible; unsupported, stale, or unknown observations fail closed with an actionable reason. No credential contents cross the hub. A later provider failure blocks the assigned request on that node; it does not trigger reassignment.
- Control-plane counts show a free node request slot.
- The Project has no other nonterminal development assignment; its effective write cap remains one. Existing read-only request limits allow a read-only request.
- No conflicting nonterminal or recovery-required assignment exists.
- Request is still `Queued` and has no assignment.

Connected, online, or idle alone never means eligible.

### 5. Claim and assign atomically

Inside one serializable transaction:

1. Resolve NodeId from the authenticated hub connection; do not trust a body field.
2. Select queued candidates in priority/time order.
3. Re-evaluate binding revision, liveness, runtime status, node capacity, project policy, project concurrency, request status, and absence of assignment.
4. Insert ExecutionAssignment with an immutable binding snapshot and opaque claim token.
5. Transition WorkRequest to `Starting`.
6. Commit; the unique `ExecutionAssignments.RequestId` key is the final duplicate-claim backstop.
7. Return the assigned workspace and policy snapshot only to that node.

Concurrent claims of one request yield one winner. In the initial phase an idle node with an independent clone has no designated binding and is rejected before assignment, even if its path text and project id match.

### 6. Execute

The node persists the assignment id, token, workspace binding id/revision, and root session identity in its owner-only local spool before launching. `PiRootSessionSupervisor` and child supervisors receive an assignment context rather than a free repository path. Every downstream operation carries the assignment id; the control plane verifies it before crossing reservation, verification, completion, or event seams.

### 7. Complete or cancel

- Completion acceptance first moves the assignment to `Finalizing`; it does not release capacity. The node durably closes admission of new root/child work, mutations, verification, and Git operations; drains or stops existing operations and supervised processes; flushes assignment events; and reports assignment-bound quiescence plus reservation and repository evidence.
- The control plane commits terminal assignment/request/result state only after validating that barrier. Uncertainty enters `RecoveryRequired`. The same release predicate applies to failure and cancellation.
- Cancelling an unassigned queued request atomically marks it `Cancelled` with no assignment.
- Cancelling an assigned request first marks assignment `Cancelling` and sends the command only to its assigned node. Ownership and reservations remain until the quiescence/recovery barrier passes.
- Cancelling while the node is offline stays `Cancelling`/`RecoveryRequired`; reconnect processing delivers cancellation before new claims.
- Force recovery requires repository status, process-death evidence, reservation audit, event-spool disposition, and fencing-token rotation. It does not silently requeue the same request elsewhere.

## Waiting reasons

Waiting reasons are nullable scheduling projections on queued requests. They are recomputed on project, binding, node heartbeat/execution status, assignment, and policy changes, with this precedence:

| Code | Meaning | Operator action |
|---|---|---|
| `project_policy_disabled` | Project policy forbids assignment | Enable or change policy |
| `workspace_binding_missing` | No designated binding | Designate a workspace |
| `workspace_validation_pending` | Node has not validated current revision | Start node or wait for validation |
| `workspace_path_missing` | Designated path does not exist on its node | Restore/edit path, then revalidate |
| `workspace_invalid` | Other node-local validation failed | Inspect result, fix, revalidate |
| `node_offline` | Designated node is not live/fresh | Start or reconnect that node |
| `runtime_unavailable` | Required runtime/route/auth is unavailable | Fix node routing or provider-native login |
| `capacity_unavailable` | Node request slots are occupied | Wait or adjust trusted capacity |
| `project_concurrency_unavailable` | Project execution limit is occupied | Wait, cancel, or recover current work |
| `ready_for_claim` | Eligible; waiting for the worker's next claim | No action |

Do not collapse runtime and capacity in the persisted/interface code even if the first UI groups them visually. Reasons contain stable codes and safe details; never provider credentials, raw CLI output, or fabricated identities.

## Persistence and migration

### Target schema

- Remove `NodeId` and `RepositoryPath` from `Projects`. Retain `Id`, display/policy/default-branch fields, timestamps, and version.
- Add `WorkspaceBindings` with an FK to `Projects`, one-row-per-project unique key for the initial phase, `(NodeId, RepositoryPath)` unique key, validation state/revision, timestamps, and version.
- Rename/rebuild `RequestClaims` as `ExecutionAssignments`, preserving the existing request primary key, `ProjectId`, `NodeId`, token, claim/lease times, and version; add binding id, immutable workspace/default-branch/revision snapshot, state, reconciliation, and terminal fields.
- Add `ExecutionAssignmentId` or equivalent request-FK authorization path to session/event/reservation/verification/completion projections where the assigned node must be checked. Avoid copying mutable binding state into every row.
- Add indexes for `(NodeId, State)`, `(ProjectId, State)`, binding node/path uniqueness, and queued-request ordering.

### Migration sequence

Use one preconditioned, transactional cutover migration because SQLite table rebuilds are involved:

1. **Expand within the migration:** create `WorkspaceBindings` and `ExecutionAssignments` while old Project and claim columns remain readable to the migration.
2. **Backfill bindings:** for every existing Project, create exactly one designated binding using its current `NodeId` and canonical `RepositoryPath`. Mark it `PendingValidation` because the legacy control-plane filesystem check is not an authenticated node attestation. Preserve every `Project.Id`.
3. **Backfill assignments:** convert each current `RequestClaim` into an ExecutionAssignment using the backfilled binding and immutable snapshot. A legacy terminal request may become historical terminal only under a coordinated migration barrier where its node execution is stopped and its sessions/reservations are quiescent; otherwise mark it `RecoveryRequired`. Every nonterminal legacy claim is `RecoveryRequired` until the same authenticated node reconciles it; never return it to the queue.
4. **Verify preconditions:** assert each Project maps to exactly one binding and each claim maps to exactly one assignment. Abort the migration rather than guess if any mapping is missing or ambiguous.
5. **Contract in the same deployment:** rebuild `Projects` without `NodeId`/`RepositoryPath`, drop `IX_Projects_RepositoryPath`, and remove the old claim table/type and compatibility code.

Existing queues, work requests, project ids, sessions, events, messages, reservations, verification, and results remain untouched. The temporary validation pause is deliberate: nodes revalidate migrated bindings on reconnect before new claims. A migration integration test must start from the prior migration, insert a node-bound project plus queue/history/claim, apply the new migration, and assert stable ids, one pending binding, and a retained terminal or recovery-required assignment.

## Application and HTTP interfaces

### Project module

- `RegisterProjectCommand`: remove repository path and node placement.
- `ProjectDto`: expose fleet metadata plus nullable/nested `WorkspaceBindingDto`; never use `Guid.Empty` as missing.
- Split `IProjectCatalog` fleet metadata from an `IWorkspaceBindingCatalog` interface. Move filesystem/Git validation out of `ProjectCatalog` into a node adapter behind the binding seam.
- Replace `DuplicateProjectException(RepositoryPath)` with binding-scoped conflict reporting.

### HTTP surface

- `POST /api/projects` — fleet metadata only.
- `GET /api/projects/{id}` — includes nullable designated binding summary.
- `PUT /api/projects/{id}/workspace-binding` — create/replace the sole designation.
- `POST /api/projects/{id}/workspace-binding/validate` — request validation; returns pending if node offline.
- `DELETE /api/projects/{id}/workspace-binding` — only when no nonterminal/recovery assignment references it.
- `GET /api/requests/{id}` and project request lists — include nullable `SchedulingStatusDto` and immutable `ExecutionAssignmentDto`.
- Add a request-cancel application operation for queued and assigned requests; current session-only cancellation is insufficient.

The old `POST /api/projects/{id}/validate` should be removed in the clean cutover rather than retained as an ambiguous alias.

## Node transport

### Identity

The production multi-node prerequisite is a node-authentication principal containing a stable `NodeId`, backed by a manually provisioned per-node credential. Automatic credential distribution is out of scope. The current fleet-shared token does not authenticate a node identity and is insufficient for multi-node claims.

HTTP is permitted only for a positively verified loopback endpoint. Every non-loopback node connection uses HTTPS/WSS with normal certificate-chain and hostname validation. Reject plaintext remote URLs, TLS-to-HTTP downgrade redirects, and certificate-validation bypasses before sending credentials or assignment data. Manual server-certificate trust and per-node credential provisioning are deployment prerequisites; without them, restrict the phase to loopback.

After authentication:

- `Register` metadata must match the principal's NodeId.
- Bind one NodeId to the connection for its lifetime.
- Derive NodeId from the connection in every later hub method.
- If compatibility DTOs still carry NodeId during one release, mismatch fails closed.
- Replacing a connection for the same NodeId does not replace an assignment or create a writer.

### Additive messages/callbacks

- Node registration/heartbeat publishes a typed execution-status snapshot: available request slots, active/recovering assignment ids, and per-route adapter-observed runtime/authentication readiness with evidence source, observation time, and routing revision. Each enabled mandatory adapter must define a bounded supported native observation before scheduling uses it; unsupported or unknown is ineligible. Keep system resource telemetry separate.
- Control plane callback `ValidateWorkspaceBinding` carries binding id/project id/revision/path/default branch and returns canonical path plus structured validation result.
- Claim result carries assignment id, workspace binding id/revision, immutable path/default-branch snapshot, token, and lease.
- Reconciliation sends the node's durable assignment inventory before new claims.

### Authorization

`NodeHub` must reject:

- heartbeat, claim, or renewal whose payload identity differs from the connection principal;
- events whose retained assignment, authenticated NodeId, token, session, request, and project authorship do not match. Execution state does not erase authorship: terminal assignments acknowledge duplicate EventIds and may accept bounded final/history events from recorded sessions. Historical ingestion never reopens an assignment or authorizes execution, mutation, new sessions, reservations, Git, or verification;
- heartbeat session ids outside the caller's assignments;
- reservation, mutation, verification, completion, mail, and identity operations whose session/request/project correlation does not belong to the assignment;
- stale binding revisions and stale assignment tokens.

Route cancellation directly to `ExecutionAssignment.NodeId` or an assignment-gated session group. Do not trust a heartbeat to establish foreign group membership.

The TypeScript Pi worker NDJSON protocol remains version 1; these are SignalR contract changes, not worker-protocol changes.

## Disconnect, reconnect, and restart recovery

| Event | Required behavior |
|---|---|
| Brief transport disconnect | Node keeps supervised processes and spools events. Assignment remains owned; no other node can claim it. |
| Missed heartbeat threshold | Node becomes offline; nonterminal assignment becomes or projects as ownership uncertain. It still occupies node/project capacity and blocks reassignment. |
| Same node reconnects before lease expiry | Re-register authenticated identity, submit assignment inventory, renew with token, replay idempotent events, continue. |
| Same node reconnects after lease expiry | Normal renewal is rejected. Enter explicit reconciliation: prove persisted token/binding revision, report process inventory and repository status. Control plane either restores the same assignment to the same node or keeps `RecoveryRequired`. |
| Control plane restarts | SQLite assignment survives. Node reconnects, inventory/reconciliation happens before `ClaimNext`, then spool replay. |
| Node process restarts | Owner-only local assignment journal survives. Supervisor first proves old process trees are stopped or reattaches where supported; otherwise marks recovery required and captures repository state. It does not forget the assignment and claim replacement work. |
| Node never returns | Assignment remains recovery required until an administrator performs audited recovery/cancellation. No transparent failover. |
| Startup validation/runtime failure after assignment | Request becomes blocked on the assignment. The assignment is retained; it is not returned to the queue for another node. |

The current `NodeWorker` behavior that removes a rejected renewal from its in-memory dictionary without stopping/reconciling the root must change. Local removal is bookkeeping, never release of write ownership.

## UI states

### Fleet and registration

- Registration dialog contains identity and policy only. Copy: “Creates a fleet-owned project. Designate a workspace on a node when execution is needed.”
- Empty state permits registration with zero nodes.
- Project cards show `No workspace`, `Validation pending`, `Workspace invalid`, `Node offline`, `Runtime unavailable`, `At capacity`, or `Ready`; they do not print a fake Guid.
- Node online badges remain liveness facts, not eligibility badges.

### Project page

A Workspace panel shows node, canonical path, validation status/time/revision, eligibility, and actions to designate/edit/revalidate/remove. Removing/replacing is disabled while referenced by a nonterminal or recovery assignment. Request composer remains enabled without a workspace and states that requests stay queued until the designated workspace is eligible.

### Request page

- Unassigned queued: show the precise scheduling reason and action.
- Assigned: show node, workspace, assigned time, assignment state, and last reconciliation.
- Disconnected/expired: show “Ownership uncertain — recovery required; DevFleet will not start a second writer.”
- Terminal: retain assignment history.
- All children inherit the displayed assignment; there is no per-child node picker.

`ProjectionChange` notifications must fire for binding validation, node execution status, assignment, reconciliation, and scheduling changes so existing `LiveView` re-reads update Home, Project, and Request surfaces.

## Minimal safe design versus future extension

| Concern | Minimal safe phase | Future multiple-workspace extension |
|---|---|---|
| Bindings | Zero or one designated binding per project | Multiple explicitly provisioned and independently validated bindings |
| Eligible node | Only the designated binding's node | Scheduler may choose among eligible bindings |
| Repository equivalence | Never assumed | Requires an explicit repository identity/revision and synchronization contract not designed here |
| Assignment | Atomic immutable node/workspace snapshot | Same interface; candidate selection broadens before the same atomic insert |
| Failover | None | Requires safe handoff/recovery and repository transfer; not implied by lease expiry |
| UI | One workspace readiness panel | Candidate workspace inventory and designation/scheduling policy |
| Schema | One binding row per project | Remove the unique ProjectId constraint and add explicit designation/scheduling policy after mobility exists |

Do not create hidden second bindings now “for later.” The future design must solve repository identity, dirty-state transfer, synchronization, and fencing before another clone can be eligible.

## Incremental implementation plan

Each step is independently reviewable and keeps the system fail-closed. The persistence/interface switch is one explicit atomic cutover; preparatory steps leave the current contract intact. The cutover's assignment-dispatch gate stays closed until assignment fencing, quiescence, and durable reconciliation are complete in steps 7–9, so intermediate builds may register, bind, and enqueue but cannot start new execution. No step enables cross-node mobility.

1. **Correct the product contract first**
   - Paths: `SPEC.md`, `docs/architecture.md`, `docs/protocols.md`, `docs/security.md`, `README.md`.
   - Change: adopt the three terms and invariants; document one designated binding, one active development request, quiescence before ownership release, and no failover.
   - Check: documentation review against this proposal; `TODO.md` stays open until that review is accepted.

2. **Characterize the cutover and migration**
   - Paths: migration integration tests, project/request persistence tests, claim/recovery tests.
   - Change: create fixtures from the prior migration covering project ids, queues, history, terminal claims, and active claims; do not change production behavior.
   - Tests: prove the old-schema baseline and expected target mapping, including a fail-closed migration barrier for uncertain legacy execution.

3. **Secure node identity and transport**
   - Paths: node authentication handler/options/setup, `NodeOptionsValidator.cs`, `NodeTransportClient.cs`, `NodeHub.cs`, `NodeConnectionDirectory.cs`, configuration examples, security/auth integration tests.
   - Change: manually provisioned per-node principal, connection-derived NodeId, HTTPS/WSS for non-loopback endpoints, normal certificate validation, fail closed on mismatch/downgrade.
   - Tests: one credential cannot assert another NodeId; unregistered connection cannot call; remote HTTP and certificate bypass are rejected; loopback development remains explicit.

4. **Move validation and readiness evidence to node adapters**
   - Paths: extract current `ProjectCatalog` repository checks into node workspace-validation code; runtime adapter readiness interfaces/implementations; Contracts; `NodeTransportClient` callback; `NodeHub`/gateway; focused adapter tests.
   - Change: add revisioned workspace validation and typed adapter-observed runtime/auth readiness while the old Project contract still drives execution.
   - Tests: real temp Git repo, missing path, invalid repo, wrong-node/stale-revision rejection; each enabled adapter reports supported evidence or `Unknown`, never infers auth from a catalog/file.

5. **Perform the atomic ownership cutover**
   - Paths: Project/WorkspaceBinding/ExecutionAssignment domain and application types, `ProjectCatalog.cs`, request assignment coordinator/evaluator, `ControlPlaneDbContext.cs`, one EF migration/snapshot, Contracts claim messages, Projects/Requests endpoints, all API/UI callers of required Project node/path fields.
   - Change: in one deployment remove Project ownership fields and `ResolveNodeId`, create/backfill the sole pending binding, replace RequestClaim with durable assignment, update claim consumers, and expose fleet-only registration/binding APIs. Registration validates metadata only. Keep new assignment dispatch explicitly disabled until steps 7–9 pass; queued work remains untouched.
   - Tests: register/list/get/enqueue with zero nodes/path; prior-schema migration preserves ids/queues/history; active claims become recovery-required; dispatch guard prevents every new claim during the safety cutover.

6. **Expose scheduling and waiting projections**
   - Paths: application eligibility types, Infrastructure request queries, `WorkRequestDto`, projection notifications, focused query/integration tests.
   - Change: one evaluator supplies both claim decisions and stable UI reasons.
   - Tests: no binding, pending, missing path, invalid, offline, runtime unavailable/unknown, node capacity, single-writer project concurrency, read-only policy, and ready precedence.

7. **Fence all request-scoped node operations by assignment**
   - Paths: `NodeHub.cs`, `NodeEventSink.cs`, reservation/mail/verification/completion gateways and stores, session projection rows, integration tests.
   - Change: validate connection NodeId, assignment, project, request, session, and token before active mutation; separately authorize bounded final/history events and idempotent terminal replay.
   - Tests: foreign event/session heartbeat/reservation/completion rejected; terminal `request.completed`/`session.closed` and lost-ack duplicates drain safely; historical replay cannot authorize a mutation.

8. **Make terminalization a quiescence barrier**
   - Paths: completion gate/store, root/child supervisors, verification and Git supervisors, assignment coordinator, completion/recovery tests.
   - Change: introduce `Finalizing`; close new admission, drain/stop processes and operations, flush events, verify reservations/repository, then atomically terminalize and release capacity.
   - Tests: delayed child/tool/write during completion keeps a competing request queued; failure/cancellation follow the same release predicate; uncertainty enters recovery.

9. **Persist node assignment journal and implement reconciliation**
   - Paths: node spool schema, `NodeWorker.cs`, `NodeTransportClient.cs`, root/child supervisors, runtime recovery, node and end-to-end recovery tests.
   - Change: journal before launch; reconcile before claims; rejected renewal cannot silently drop ownership; cancellation wins before new work. Enable assignment dispatch only after steps 7–9 integration checks pass.
   - Tests: control-plane restart resumes the same assignment; disconnect prevents a second claim; expired reconnect requires reconciliation; node restart with uncertain processes stays recovery required; atomic competing claims yield one assignment; independent clone and ineligible node are rejected.

10. **Complete the Blazor operator surfaces**
    - Paths: `Home.razor`, `Project.razor`, `Request.razor`, shared styles if needed, `FluentSurfaceTests.cs`, live-view integration tests.
    - Change: fleet-only registration, workspace panel, waiting reasons, immutable assignment history, finalizing/recovery warnings.
    - Tests: HTML/UI states for no nodes, no binding, invalid/offline/unavailable/ready, assigned/finalizing/recovery history; no fake NodeId.

11. **Clean up and verify end to end**
    - Paths: demo/setup scripts, examples, obsolete project validation/claim/options/types/comments, end-to-end tests.
    - Change: remove every old caller and ambiguous validate endpoint; update examples for node-local roots, TLS, identity, and binding designation.
    - Checks: targeted .NET tests per step, then `./scripts/verify.sh`; browser exercise of registration → binding → enqueue → assignment → completion barrier and disconnect/recovery. Real-provider tests remain opt-in.

## Acceptance test plan

| Scenario | Setup and action | Required assertion |
|---|---|---|
| Registration and enqueue with no nodes | Empty node registry; register Project; enqueue request | Both succeed; stable ProjectId; request is queued with `workspace_binding_missing`; no NodeId/path fabricated. |
| Waiting without binding | Query project/request projections | Project is valid; request remains `Queued`; action says designate workspace. |
| Node-side workspace validation | Provision path on authenticated node and validate | Only that node may answer; canonical path and current revision stored; invalid/missing results are distinct. |
| Execute when designated workspace is ready | Valid binding, fresh authenticated node, required runtime, free capacity | Claim atomically creates one assignment and starts request; payload path comes from binding snapshot. |
| Ineligible-node rejection | Wrong node calls ClaimNext | No assignment or request transition; reason for project remains tied to designated node readiness. |
| Independent clone rejection | Second idle node has its own clone/path for same project | It cannot claim because it is not the designated binding; no path-string/project-id equivalence is inferred. |
| Competing claims | Two eligible claim transactions race for one queued request in future-capable test setup | Exactly one assignment and one `Starting` transition commit; loser receives no work. |
| Capacity and policy | Exhaust node slot, project write/read limit, disable project, or remove runtime | Request remains queued with the matching stable reason; connected/idle alone does not pass. |
| Single-writer preservation | Configure `MaxActiveWriteRequests` above one; run one development assignment; enqueue another | Second development request remains queued with `project_concurrency_unavailable`; shared checkout branch cannot switch under the active root. |
| Quiescent terminalization | Completion accepted while a child/tool/write or supervised process remains active; another request claims | Assignment stays `Finalizing`, occupies capacity, and denies the competing claim until the barrier passes. |
| Terminal event replay | Complete, emit final events, lose acknowledgement, then replay | Owned final/history events and duplicate EventIds are acknowledged; replay cannot reopen work or authorize mutations. |
| Remote transport protection | Configure a non-loopback HTTP URL, downgrade redirect, or certificate bypass | Startup/connection fails before any node credential or assignment data is sent. |
| Disconnect without reassignment | Assigned node loses heartbeat while root may still run; another node claims | Assignment enters/projects recovery required; other node receives nothing; project slot remains occupied. |
| Reconnect and restart | Same authenticated node presents persisted assignment inventory/token | Same assignment resumes or enters explicit reconciliation; no second root is created. |
| Cancellation | Cancel queued, running-online, and running-offline requests | Queued cancels directly; assigned retains ownership until stop/recovery; assignment history remains. |
| Retained assignment history | Complete or cancel request, edit binding later, restart control plane | Request still shows original node/path/revision snapshot and terminal assignment state. |
| Existing-project migration | Start on old schema with project, queued request, sessions/history, and optional claim; apply the coordinated migration barrier and migrate | ProjectId and all history unchanged; one pending binding created for node revalidation; safely quiesced terminal claim becomes history, otherwise claim becomes recovery-required; uncertain work is not reassigned. |
| Reservation/Git invariants | Execute multiple children on assignment | All share one node/path; overlapping reservation denied; no worktree; only supervisor changes Git state. |

## Security and recovery implications

- Per-node identity is necessary before multi-node claiming. A shared fleet token plus self-asserted Guid is not an authenticated identity.
- Workspace paths remain node-local secrets/metadata and must be bounded/redacted in logs. Provider credentials never move.
- Approved roots move from control-plane project registration to node-local binding validation and every filesystem operation still rechecks path containment.
- Assignment authorization supplements, not replaces, reservation fencing. Assignment says which supervisor may act; reservation says which session may mutate which path.
- Uncertain ownership is deliberately availability-reducing. This is the safe trade-off while process state and repository state cannot be transferred.
- Recovery and force-release actions require audit facts. Heartbeats and time alone are not evidence that a writer stopped.

## Trade-offs and open questions

1. **Per-node credential provisioning and server trust:** authenticated identity and protected non-loopback transport are required; automatic distribution is out of scope. Choose the manual enrollment, certificate-trust, rotation, and revocation procedure before multi-node claim code ships.
2. **Adapter readiness evidence:** specify a supported, bounded native availability/authentication observation and freshness threshold for each enabled mandatory adapter. Until an adapter supplies positive evidence, it reports `Unknown` and remains ineligible rather than treating catalogs, executables, or credential files as proof.
3. **Default branch ownership:** this proposal keeps it as Project policy and snapshots it into assignments; binding validation proves that branch exists locally. If nodes intentionally use different branches later, move it to an explicit execution policy, not an implicit path property.
4. **Recovery of a dead root process:** safest initial behavior is recovery-required plus operator retry as a new WorkRequest. Reconstructing provider sessions is adapter-specific and not required for fleet ownership.
5. **Read-only concurrency during uncertain write ownership:** keep project policy conservative and count recovery assignments. Relax only with proof that read-only adapters cannot mutate and repository inspection is safe.
6. **Waiting reason storage:** prefer a computed projection backed by one evaluator; persist only if audit/history requirements emerge. Assignment and validation state themselves remain durable.

## Explicit non-goals

No SSH tunnels, Git host integration, cloning, replication, synchronization, shared filesystems, uncommitted-change transfer, transparent failover, workspace relocation, cross-machine child distribution, credential distribution, multi-user tenancy, or extra fleet tenancy model. No GitHub/GitLab dependency. No relaxation of the canonical-workspace, no-worktree, reservation, concurrency, or supervisor-owned Git rules.

## Documentation changes required with implementation

- `SPEC.md`: replace §§3.6, 7, 10, 10.1, 10.2, 12.2, 23, 29, 30.1, and 31.1–31.3 node-owned assumptions; add WorkspaceBinding, ExecutionAssignment, waiting reasons, and recovery invariants.
- `docs/architecture.md`: change registration/claim data flow and persistence/recovery tables; node hosts assignments, not projects.
- `docs/protocols.md`: document connection-derived identity, binding validation, typed execution status, assignment claim/reconciliation, and assignment-gated events/controls.
- `docs/security.md`: move approved-root checks to nodes; document per-node identity, assignment authorization, and no-reassignment-on-expiry.
- `README.md`: split project registration from workspace designation in setup/demo/configuration and remove `Projects:NodeId` ownership language.
- Deployment examples: move approved roots to node configuration and document manual per-node credential provisioning once chosen.
- `TODO.md`: keep the design item unchecked until this proposal is reviewed and accepted; implementation completion is a separate decision.
