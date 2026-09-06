# Project recovery

**Status:** Implemented  
**Date:** 2026-09-06  
**Scope:** A safe, project-scoped escape hatch for stuck execution, preserving workspace contents and history  
**Review state:** Design review completed 2026-09-06. Delivery shipped: Linux `setsid` process-group proof, durable hold/operation, recovery HTTP, recheck, operator `confirm-manual`, resume, linked `OriginalRequestId`. Operator runbook: [docs/operations/project-recovery.md](../operations/project-recovery.md). Architecture: [docs/architecture.md](../architecture.md#recovery).

This document is the product specification as built. Remaining text below is the accepted design; where implementation differs or names concrete types, **Implementation** notes apply.

## Decision

Expose **Recover project**, not **Reset project**.

Recovery stops the current execution safely, resolves its retained ownership, and leaves the Project paused for operator inspection. It never resets Git, discards files, deletes history, or assumes an unreachable agent has stopped. Starting work again is a separate explicit action.

The normal path is one confirmation followed by visible progress. The exceptional path explains exactly what evidence or local intervention is missing rather than presenting an indefinite spinner or an unconditional unlock.

## Sources and current behavior

This implementation builds on:

- [README.md](../../README.md): canonical node-local workspace, one retained writer, no transparent failover.
- [SPEC.md](../../SPEC.md) §§12.4, 17.9–17.10: assignment authorization, quiescence, reservation recovery, audited force-release.
- [Architecture](../architecture.md#recovery): durable assignment journal and event spool, restart reconciliation, startup blockers, cancellation.
- [Protocols](../protocols.md#signalr-node-hub-nodehub): authenticated node ownership, cancellation before notification, replay and reconciliation before claims.
- [Security](../security.md): administrator authentication, node-local filesystem authority, mutation fencing, bounded diagnostics, secret handling.
- `src/PiCommandCenter.Infrastructure/Requests/ExecutionAssignmentService.cs`: reconciliation retains uncertain ownership and prioritizes cancellation.
- `src/PiCommandCenter.Infrastructure/Completion/AssignmentTerminalizationService.cs`: terminalization validates assignment-bound quiescence before releasing capacity.
- `src/PiCommandCenter.Api/RequestsEndpoints.cs` and `IRequestCancellationService`: the existing `POST /api/requests/{requestId}/cancel` route moves a retained assignment to `Cancelling` and delivers a best-effort `CancelAssignmentCommand` to the assigned node.
- `src/PiCommandCenter.Node/NodeWorker.cs`: the node tracks one pending cancellation per assignment and retries the stop-and-terminalize attempt on its tick.
- `src/PiCommandCenter.Contracts/NodeTransport/CompletionMessages.cs`: `AssignmentQuiescenceProofMessage` carries only integer counts, two booleans, and a timestamp.
- `src/PiCommandCenter.Infrastructure/Projects/WorkspaceBindingCatalog.cs`: binding replacement and removal are rejected while any assignment on that binding is not `Completed`, `Failed`, or `Cancelled`.
- `src/PiCommandCenter.Node/Runtime/AssignmentProcessIsolation.cs` and `NodeWorkerProcess.cs`: on Linux, workers launch under util-linux `setsid` (`/usr/bin/setsid` or `/bin/setsid`). Stop proof enumerates `/proc` by session/process group with PID + start ticks. Non-Linux or missing `setsid` returns `process_stop_unproven`; `Process.Kill(entireProcessTree: true)` is not isolation proof.
- `src/PiCommandCenter.Web/Components/Pages/Project.razor`: existing finalizing, cancelling, and recovery-required explanations.

Cancellation, reconciliation, reservation force-release, and terminalization remain separate building blocks composed by project recovery. Releasing a reservation alone still does not terminalize its assignment, and a restart is still not evidence that every old writer stopped.

The implemented workflow adds durable operation tracking, a project scheduling hold, guided manual recovery, and linked retry UX. Reconciliation does not automatically resume an assignment already marked `RecoveryRequired`; recovery uses an explicit stop-and-cancel flow, not an automatic-resume policy.

## Goals

1. Recover a stuck Project without editing SQLite, deleting its node spool, or recreating the Project.
2. Explain why execution is blocked and what the operator can do next.
3. Stop only this Project's assignment-bound activity; leave unrelated projects running.
4. Preserve all remaining workspace contents, uncommitted changes, branches, settings, credentials, and history.
5. Never admit a new writer while an old writer may still access the workspace.
6. Survive browser refreshes, duplicate submissions, disconnects, and process restarts.
7. Distinguish **ownership recovered** from **workspace ready to execute**.

## Non-goals

- Resetting, cleaning, stashing, checking out, deleting, or automatically committing workspace contents.
- Repairing merge conflicts, repository corruption, failing checks, missing provider login, or bad routing automatically.
- Resuming an interrupted agent conversation or continuing the original execution after cancellation.
- Moving a request to another node, creating a worktree, or treating another checkout as the same workspace.
- Restarting services or killing every process owned by the node user from a project button.
- An agent-callable recovery or administrator force-release tool.
- Automatic stuck detection based solely on silence or elapsed time.

## Terminology

| Term | Meaning |
|---|---|
| Recovery operation | Durable operator-initiated workflow targeting a captured set of this Project's nonterminal assignments and unresolved reservations. It is not a replacement assignment state. |
| Recovery hold | Durable project-wide scheduling/admission barrier established when recovery is accepted. It remains after recovery succeeds until explicitly cleared. |
| Quiescence | Admission closed; root, children, process descendants, mutations, verification, Git activity, events, and reservations accounted for with no activity able to continue writing. |
| Automatic recovery | Assigned node proves quiescence using trusted supervision and the existing terminalization authority. |
| Manual recovery | Administrator supplies audited, assignment-specific evidence after local intervention when automatic proof cannot be obtained. It is not a waiver of writer safety. |
| Linked retry | New Work Request with an explicit reference to the original request; no reassignment or reopening of the original. |

## Relationship to existing cancellation

Recover project is not a second cancellation mechanism. It composes the existing pieces and adds what they lack:

| Existing behavior | What recovery adds |
|---|---|
| `POST /api/requests/{requestId}/cancel` transitions one assignment to `Cancelling` and sends one best-effort command to the node. | A durable, project-wide hold, a captured multi-target inventory, bounded attempts with per-target evidence, and progress the operator can watch. |
| The node's pending-cancellation loop retries stop-and-terminalize until the Control Plane accepts a proof. | Operation and attempt correlation on each retry, explicit missing-evidence codes when it cannot prove quiescence, and a `NeedsIntervention` state instead of silent retrying. |
| `POST /api/reservations/{leaseId}/force-release` rotates a fencing token for one lease. | Reservation resolution ordered after stop evidence, never as the first step, and never as a substitute for terminalizing the assignment. |

Recovery acceptance invokes the same cancellation service and domain transitions per target so that there is one cancellation state machine. Plain request cancel keeps its current semantics and gains no hold. An operator who cancels a request first and then opens Recover project sees that assignment already listed as `Cancelling`; recovery adopts it as a target rather than issuing a second transition.

## Operator experience

### Entry points and diagnosis

Show a prominent **Recover project** action on Project and request pages when a retained assignment is `RecoveryRequired`, `Cancelling`, `Finalizing`, or blocked, or its node is disconnected. Link the same recovery panel from the Attention inbox.

Keep the action available in the Project actions menu for a running assignment the operator believes is stuck; no arbitrary inactivity threshold is required. A blocked request waiting for input should offer **Provide input** or its specific fix first. An unassigned queued request with a configuration/readiness blocker should offer that fix and ordinary queued cancellation, not imply that recovery will repair configuration.

Opening the panel is read-only. Display:

- Blocking request and assignment identities, states, assigned node, immutable workspace and binding revision.
- Last node contact, reconciliation, and execution evidence with timestamps; stale or unknown evidence must be labeled.
- Root/child and supervised-operation inventory, distinguishing known zero from unknown.
- Active/recovery-required reservations and the evidence preventing release.
- Known startup, authentication, routing, repository, and verification blockers, with actionable links.
- What can be attempted automatically and why manual intervention may be necessary.

Do not label an agent dead or safe to unlock merely because it is silent.

### Confirmation

Primary action: **Stop work and recover**.

Required confirmation text:

> Stop this project's current work and pause its queue. Files, uncommitted changes, settings, and history will be kept. Work will not restart automatically. If DevFleet cannot prove the old processes stopped, the project will stay blocked.

Show the exact affected assignments, including read-only assignments if enabled. Require a reason, prefilled with the observed blocker and editable. Do not require typing a Project name for this non-destructive normal path.

The server validates the panel's revision before acceptance. If its target inventory changed, refresh the preview and require confirmation again rather than cancelling a newly started request the operator never saw.

### Progress

Show named stages, timestamps, latest progress, and missing evidence, not a fabricated percentage:

1. Pausing new work.
2. Stopping agents and supervised operations.
3. Reconciling events and reservations.
4. Inspecting the workspace.
5. Resolving execution ownership.

Browser navigation does not cancel the operation. A reopened panel displays the same operation. **Retry recovery** re-attempts that operation after a fix; it does not create a second concurrent coordinator.

### Outcomes

| Outcome | UI and available actions |
|---|---|
| Recovered, no additional readiness blockers | **Recovered — queue paused. Ready for new work once resumed.** Offer Resume queue, Retry as new request, and Inspect changes. Per-target outcomes show which targets were cancelled and which `Finalizing` targets completed with their original intent. |
| Recovered, workspace/configuration still blocked | **Execution recovered — setup or workspace action required.** Show exact blockers. Ownership recovery is successful, but no claim bypass is allowed. |
| Manual intervention needed | **Still blocked: [specific missing evidence].** Show local steps, Recheck, and the administrator recovery form where applicable. Keep ownership and hold. |
| No retained execution or unresolved reservation exists | **No execution recovery needed.** A read-only check does not change the queue or create a recovery hold. |

Inspect changes must preserve existing access boundaries; no new unowned workspace-read authority is granted to agents. An offline workspace is shown as unavailable, not empty or clean.

## Recovery hold and concurrency

Acceptance must atomically establish the recovery hold, capture the confirmed assignment/reservation inventory, persist the recovery operation, and record cancellation intent for each targeted nonterminal assignment before notifying any node.

- `ClaimNext` checks the hold in the same transaction that claims work. All new claims for the Project, including configured read-only requests, are blocked with scheduling reason `project_recovery_paused`.
- Queue entries and ordering remain intact. New requests may be enqueued while paused but cannot execute.
- Existing targeted assignments close admission to new root/child work, mutations, verification, and Git operations through the existing assignment authority. Cleanup, evidence reporting, and bounded historical replay remain allowed.
- Binding edits/removal cannot invalidate an unresolved operation. `WorkspaceBindingCatalog` already rejects replacement or removal while any assignment on the binding is nonterminal; the hold extends that protection through the recovery inspection interval after targets terminalize. Project deletion is not currently exposed by `ProjectCatalog`; if it is added, it must refuse while a hold exists.
- The hold is distinct from the existing `Project.Enabled` flag (`project_disabled`). Enabled is an operator policy toggle that survives recovery unchanged; the hold is recovery state that only recovery may set and only Resume queue may clear. `ClaimNext` evaluates both, reporting `project_recovery_paused` before `project_disabled` so the operator sees the reason they can act on. Resume queue on a disabled Project clears the hold and leaves the Project disabled; it does not enable it.
- Exactly one unresolved recovery operation exists per Project. Concurrent browser/API attempts either return the existing equivalent operation or a conflict; they do not run overlapping stop procedures.
- Clearing the hold is a separate revision-checked transaction and is rejected while any target ownership or reservation remains unresolved. It never overrides ordinary scheduling/readiness rules.
- Recovery remains project-scoped. Shared-node or shared-resource uncertainty is surfaced, not resolved by killing unrelated work or releasing another assignment's reservation.

A recovery hold outlives a successful operation. If work completes normally before recovery wins the acceptance race, preserve that terminal result and refresh the target set; never rewrite completed work as cancelled. If recovery commits first, subsequent completion cannot outrun its committed cancellation intent.

## Automatic recovery

The assigned node executes an idempotent, assignment-bound stop procedure:

1. Reconcile its durable inventory and journal the authoritative recovery/cancellation intent before acting. An expired lease cannot be renewed through the ordinary path to bypass reconciliation.
2. Close admission and request cooperative cancellation of every targeted root, child, and supervised operation.
3. After a bounded grace period, terminate only positively identified assignment-owned process trees. Do not kill by executable name, workspace substring, or a bare reused PID. Track process identity and descendants sufficiently to prove exit; inability to do so is missing evidence.
4. Drain or stop in-flight mutations, verification, trusted Git operations, and reservation operations. Cancellation acknowledgement is not process-exit proof. An interrupted Git command may leave an actionable repository blocker; do not delete Git locks automatically.
5. Flush and acknowledge existing spooled events, then record the quiescence evidence. Never clear the spool to make the pending count zero. Control-plane recovery audit events are not an endlessly moving node-spool barrier.
6. Inspect the canonical assignment workspace using read-only trusted operations: repository availability, HEAD/branch where available, index/worktree status, untracked-file metadata, and interrupted-operation indicators. Account explicitly for an ordinary directory or unborn repository after startup failure; absence of HEAD alone is not unknown filesystem state.
7. Resolve assignment-owned active/recovery-required reservations through the existing authority after stop evidence is accepted. Preserve release history and invalidate stale fencing tokens; no direct row deletion or blind bulk force-release.
8. Confirm quiescence and terminalize each still-nonterminal target, with recovery reason and operation id. A target that was `Starting`, `Running`, `Cancelling`, or `RecoveryRequired` terminalizes as `Cancelled`. A target that was `Finalizing` with an accepted `Complete` or `Fail` intent is different: its work is already done and its completion evidence was already accepted at `BeginTerminalization`, so recovery confirms that same intent using the recovery-collected quiescence proof. It terminalizes such a target as `Cancelled` only if the completion gate rejects the confirmation, and it records the gate decision on the operation. Preserve existing failure facts and terminal histories; do not claim successful completion or produce fabricated verification/result evidence.
9. Mark the operation recovered only when every target is accounted for and no conflicting retained ownership remains. Keep the recovery hold.

Partial progress across several targets is durable: safely terminalized targets stay terminal, while unresolved targets retain ownership and keep the Project blocked.

### Process identity and isolation

Implemented:

- Linux: `AssignmentProcessIsolation.IsLinuxIsolationAvailable`; journal `AssignmentProcessIdentity` (`ProcessId`, `StartTimeTicks`, `ProcessGroupId`, `SessionId`). Stop uses `kill(-pgid, SIGTERM/SIGKILL)` when the group is still ours. Escaped descendants are listed; reused PID (start ticks mismatch) is treated as exited.
- Non-Linux: automatic recovery cannot prove stop (`process_stop_unproven`). No tree-walk fallback as proof.
- Transient systemd cgroup scopes were accepted as a preferred upgrade; the shipped node uses session/process group via `setsid` plus `/proc`.

### Evidence and repository preservation

Evidence binds to operation id, attempt number, assignment id, authenticated node, immutable binding revision, and collection time. It includes admission state, child/process inventory, in-flight operation counts, event acknowledgement position, reservation disposition, and bounded repository status. Old evidence from before a new operation/attempt cannot authorize release.

Dirty files are not a failure to prove quiescence. Recovery preserves the workspace as left by stopped work and reports it honestly; it cannot promise that a killed write produced a complete or correct file. It does not require passing tests, a clean tree, a final checkpoint, a branch switch, or completion review to cancel safely. Starting the next request still applies current clean-start, branch, binding, authentication, routing, and verification policies unchanged.

If repository inspection is unavailable or untrustworthy, ownership remains unresolved. Do not manufacture an empty/clean snapshot.

### Time bounds

Implemented `NodeOptions`: `RecoveryCooperativeStopSeconds` default 10, `RecoveryTerminationSeconds` default 20, `RecoveryAttemptSeconds` default 60 (`NodeOptionsValidator` requires attempt ≥ cooperative + termination). `ProjectRecoveryService.AttemptDeadline` is 60 seconds for operation deadlines.

These are attempt deadlines, never ownership-expiry deadlines. An unreachable node immediately presents waiting/manual guidance. When a deadline expires, persist missing evidence and stop active retries; do not release capacity. Late evidence is accepted only if still correlated to the current attempt and independently revalidated. Explicit Recheck or a returning owner can advance the retained cancellation safely without ever resuming original execution.

## Manual recovery

Manual recovery is available only to the authenticated administrator, never to a runtime agent or a node acting as administrator.

The panel first offers non-destructive local steps:

1. Restore connectivity where possible, allowing the owner to reconcile its retained cancellation.
2. On the owning machine, identify and stop the assignment's supervised process trees and verify descendants cannot continue writing.
3. Inspect the exact canonical workspace and record remaining changes, repository status, and interrupted operations.
4. Recheck automatic recovery if the node can now produce trustworthy evidence.

A service restart is an optional local troubleshooting step with a warning that it affects other Projects on that node. It is not proof of quiescence. Never instruct the operator to delete databases, assignment journals, event spools, or workspace contents.

If automatic proof remains impossible, an explicit **Confirm manual recovery** form requires:

- Project, operation, attempt, assignment and binding identities tied to the current server revision.
- Reason and operator identity recorded by authentication, plus confirmation that original execution must not resume.
- Dated process-stop or durable isolation evidence: what was checked/stopped, how descendants were excluded, and how restart access to this workspace is prevented.
- A current repository status snapshot from the actual owning workspace, with source and collection time. The node is by definition unable to supply this, so the administrator collects it on the owning machine and enters it by hand; the form labels it as such. An inaccessible workspace cannot be attested as clean or replaced by another checkout.
- An accounting of affected reservations and any journal/event evidence gaps. Missing events remain an explicit audit gap, not a false claim that the spool was flushed.
- A separate explicit acknowledgement of those gaps and consequences; typed Project name confirmation.

Server validation rejects incomplete, stale, or mismatched evidence. Administrator-supplied evidence is labeled as an **operator attestation**, never as node-observed fact. Missing event history can be explicitly accounted for under this audited exception; unresolved writer access or unavailable repository status cannot be waived.

The authorized recovery transition revokes old execution authority, resolves only the attested targets/reservations, increments applicable fencing epochs, terminalizes affected nonterminal assignments as cancelled, and records the evidence and decision atomically. Keep the scheduling hold until explicitly cleared.

Fencing prevents future authorized tool writes; it cannot stop an already running OS process or revoke an open file descriptor. Therefore token revocation or a disconnected heartbeat alone never satisfies manual recovery.

If the old node returns, reconciliation must order stop for any leftover execution and treat the retained assignment as terminal. It cannot resume, spawn, mutate, verify, or regain a reservation. Bounded late historical events may be ingested without changing terminal truth; newly discovered contradictory writer evidence raises an incident and retains/reinstates a protective hold rather than being silently ignored.

If the machine/workspace is permanently inaccessible and the required evidence cannot be obtained, keep the Project blocked and say so. Lost-workspace abandonment or repository relocation requires a separate product policy; it is not hidden behind recovery.

## Retry and resume

- **Resume queue** clears only the resolved recovery hold. Existing queue priority and ordinary eligibility rules apply; it does not promise the next request can start.
- **Retry as new request** opens an editable draft populated from the selected original request's objective, acceptance criteria, and applicable user settings, with an immutable original-request link.
- Show preserved workspace changes and readiness blockers before submission. Do not copy claim tokens, session ids, reservation leases, old completion/verification evidence, or immutable assignment placement into a new assignment.
- Drafting or enqueueing the retry does not clear the hold. Linked retry is `QueueWorkRequestCommand.OriginalRequestId` on `POST /api/projects/{projectId}/requests`; the column is immutable and confers no execution authority.
- A retry is never an automatic provider call, automatic branch reset, or continuation of the original assignment. It uses the Project's then-current eligible binding and normal startup policy.

## Durable model and transport requirements

Persist a recovery record with id, Project id, target identities/revisions, actor/reason, creation/update/completion timestamps, attempt number, current stage, last progress time, deadline, stable blocker codes, evidence provenance, and resolution. Keep per-target outcomes and an append-only audit trail. Store the recovery hold separately from the operation's success so a refresh or restart cannot accidentally resume the queue.

Operation statuses: `Pending`, `Running`, `NeedsIntervention`, `Recovered`. `NeedsIntervention` retains ownership and may return to `Running` for a new bounded attempt. Database/transport failures are visible diagnostic reasons, never aliases for recovery success. Readiness blockers after `Recovered` are separate from unresolved ownership blockers.

Implemented HTTP (`ProjectRecoveryEndpoints`, also under `/api/v1`):

| Method and route | Contract |
|---|---|
| `GET /api/projects/{projectId}/recovery` | `ProjectRecoveryDiagnosisDto` |
| `POST /api/projects/{projectId}/recoveries` | `StartProjectRecoveryRequest` (`InventoryRevision`, `Reason`, `IdempotencyKey`); `202` |
| `GET /api/projects/{projectId}/recoveries/{recoveryId}` | `ProjectRecoveryOperationDto` |
| `POST /api/projects/{projectId}/recoveries/{recoveryId}/recheck` | `RecheckProjectRecoveryRequest`; does not clear the hold |
| `POST /api/projects/{projectId}/recoveries/{recoveryId}/confirm-manual` | `ConfirmManualProjectRecoveryRequest`; provenance `operator-attestation` |
| `POST /api/projects/{projectId}/recovery/resume` | `ResumeProjectRecoveryRequest` (`OperationId`, `ExpectedHoldVersion`) |

Node hub: `RecoverAssignment` (`RecoverAssignmentCommandMessage`); node reports `ReportRecoveryProgress` / `ReportRecoveryProof`. Proof type is `AssignmentRecoveryProofMessage` with `RecoveryKnownCountMessage` inventories.

Apply existing admin cookie/antiforgery and versioned API bearer policies. Node credentials cannot invoke administrator recovery. Idempotency is `RecoveryIdempotencyKeys` keyed by Project, action (`start`, `recheck`, `confirm-manual`), and key; reuse with a different input hash conflicts. Stale revisions return `409`. No diagnostic response exposes claim tokens, credentials, environment dumps, file contents, or unbounded logs.

Ordinary completion still uses `AssignmentQuiescenceProofMessage`. Recovery uses `AssignmentRecoveryProofMessage` (operation/attempt/assignment/binding/claim-token correlation; `RecoveryKnownCountMessage` inventories; `RecoveryProcessIdentityMessage`; event acknowledgement position or unknown code; `RecoveryReservationDispositionMessage`; `RecoveryRepositoryStatusMessage`).

The Control Plane accepts a recovery confirmation only when every inventory is known and zero, the proof correlates to the current attempt, and the repository snapshot is present. No Pi worker recovery tool or NDJSON version change is required for this feature. Publish Project, request, and Attention projection invalidations on durable progress and outcome changes.

Implemented `RecoveryReasonCodes`: `project_recovery_paused`, `node_unreachable`, `process_stop_unproven`, `operation_drain_timeout`, `events_unacknowledged`, `reservation_unresolved`, `repository_status_unknown`, `recovery_evidence_stale`, `recovery_target_changed`.

## Acceptance criteria and verification plan

All scenarios must be verified with fake/local runtimes; real-provider quota is not needed.

| Scenario | Required result |
|---|---|
| Responsive stuck root with children | One confirmation pauses claims, stops all targeted activity, resolves ownership, preserves files/history, and leaves queue paused. |
| Unresponsive root or verification process | Bounded targeted escalation proves stop or reports missing evidence; no unrelated process is killed. |
| Startup blocked before root creation | Recovery can cancel without inventing a session or requiring a Git HEAD that never existed. |
| Finalizing target with accepted completion | Recovery confirms the original `Complete` or `Fail` intent with its proof; the persisted result is preserved; the target is cancelled only if the completion gate rejects. |
| Request cancelled first, then recovery opened | The `Cancelling` assignment is adopted as a target without a second transition or duplicate node command. |
| Dirty workspace or failed verification | Cancellation can recover ownership without cleanup, checkpoint, successful review, or passing checks; next-run policy still applies. |
| Unknown process, escaped descendant, or reused PID | No optimistic zero-process proof; retain ownership and actionable manual guidance. |
| Offline node or expired assignment lease | No new writer or binding change; returning node receives cancellation before claims, never automatic resume. |
| Duplicate clicks, recheck retries, or two administrators' tabs | One unresolved operation and one active attempt; stale targets conflict; no duplicate terminalization or token rotation. |
| Claim races recovery acceptance | Either the confirmed inventory is safely captured or acceptance conflicts; no unconfirmed new execution is silently cancelled or admitted after hold. |
| Normal completion races recovery | Transaction winner determines outcome; preserve existing terminal result and never reopen it. |
| Browser/control-plane/node restart at every stage | Durable hold, targets, deadlines, cancellation, progress, and audit survive; restart cannot free capacity or resume work. |
| Pending spool events or delayed acknowledgement | No spool deletion or false flushed proof; replay remains idempotent and manual gaps are explicit. |
| Reservation released separately | Retained assignment still blocks until its own quiescence/recovery transition succeeds. |
| Several targeted assignments, one unresolved | Completed cleanup remains recorded; Project stays held until all targets are safe. |
| Incomplete/stale/cross-node manual evidence | Reject release and preserve ownership; administrator attestation never masquerades as automatic proof. |
| Valid manual evidence with history gap | Audited cancellation and fencing changes commit together; gap remains visible and queue remains paused. |
| Recovered node returns with old credentials/tokens | Historical replay cannot reopen execution; stale mutation/spawn/reservation requests fail closed. |
| Unreachable or corrupt repository | No guessed clean snapshot; unresolved inspection keeps recovery blocked with next steps. |
| Retry with existing queued requests | New linked request, no copied execution authority, explicit queue resume, existing priority preserved. |
| Unauthorized request or CSRF | Denied with no hold, cancellation, release, or audit-evidence mutation. |
| Preservation and isolation | Compare pre/post tracked and untracked file contents, branch refs, settings, credentials, histories, and unrelated Project activity; no recovery-induced destructive changes. |

Test layers: domain/state-machine tests for holds and transitions; infrastructure transaction/idempotency tests; API and hub authorization/correlation tests; node supervisor process/deadline tests; end-to-end restart/race tests; browser checks for diagnosis, confirmations, progress, retained pause, manual blockers, and linked retry. Process tests must include descendants and concurrent unrelated work, not only a cooperative fake adapter.

## Delivery record

1. Added assignment-scoped process isolation and PID/start-time tracking to the node, including descendant and unrelated-process coverage.
2. Added durable recovery operation/hold records, idempotency keys, migrations, transaction-safe claim exclusion, and concurrency coverage.
3. Added bounded recovery coordination, per-target evidence, retained completion intent, and restart-safe progress.
4. Added diagnosis, automatic recovery, recheck, and separate resume API/UI paths.
5. Added the audited manual transition and returning-node fences.
6. Added linked retry UX and preservation/readiness checks.
7. Updated the canonical SPEC, architecture, protocols, security documentation, and operator runbook.

### Accepted implementation decisions

Both decisions were accepted on 2026-09-06 with the recommended option.

1. **Process isolation mechanism.** Recommended: Linux session/process group via `setsid` with `/proc` enumeration, upgraded to a transient systemd cgroup scope where available. The alternative, keeping the current tree walk, makes automatic recovery unprovable and should be rejected.
2. **Finalizing targets.** Recommended: complete or fail with the already-accepted intent using the recovery-collected proof, cancelling only on gate rejection. The alternative, cancelling every target uniformly, discards accepted completion evidence and would record finished work as cancelled.

### Confirmed defaults

- Recovery always stops/cancels; preserving and resuming an interrupted session is a separate future feature.
- Queue stays paused after recovery until explicit resume.
- Automatic attempt budgets are 10/20/60 seconds as specified above.
- Manual recovery requires current owning-workspace status and writer-stop/isolation evidence; there is no lost-workspace abandonment override in this scope.

These defaults make the normal recovery quick without converting uncertainty into permission for a second writer.
