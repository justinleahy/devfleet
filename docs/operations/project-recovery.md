# Operator runbook: project recovery

Recover project stops this Project's retained execution, keeps the workspace and history, and leaves the queue paused. It is not a reset. An authenticated administrator starts it with one **Recover project** action; there is no operator-entered reason. Node-observed proof (`AssignmentRecoveryProofMessage`) is distinct from administrator attestation (`operator-attestation` on `confirm-manual`). Silence is never stop proof.

Architecture: [docs/architecture.md](../architecture.md#recovery). Design as built: [docs/design/project-recovery.md](../design/project-recovery.md).

## Diagnosis (read-only)

`GET /api/projects/{projectId}/recovery` returns `ProjectRecoveryDiagnosisDto`: `ProjectVersion`, `InventoryRevision`, `HoldPresent`, `HoldOperationId`, `HoldVersion`, `LatestOperation`, `NonterminalAssignments`, `UnresolvedReservations`. GET does not create a hold.

Also inspect the Project/request UI Recover project panel. Do not treat disconnected, expired lease, or quiet agents as stopped.

## Exact safe automatic flow

1. Click **Recover project**. Consequences stay visible. Server checks `InventoryRevision`. There is no typed reason and no second confirmation.
2. `POST /api/projects/{projectId}/recoveries` body `StartProjectRecoveryRequest`: `InventoryRevision`, `IdempotencyKey`. Cookie admin (or `/api/v1` bearer). Actor is the authenticated principal. Audit description is server-authored `Administrator requested project recovery.` Response `202` with `Location` `…/recoveries/{recoveryId}` unless `NoOp` (empty inventory; no hold).
3. Control plane (`ProjectRecoveryService`) in one transaction: persist `RecoveryOperationRow`, capture assignment/reservation targets, set `RecoveryHoldRow`, record cancellation intent. `ClaimNext` then reports `project_recovery_paused` before `project_disabled`. New requests may still enqueue.
4. Dispatcher sends SignalR `RecoverAssignment` (`RecoverAssignmentCommandMessage`: `RecoveryId`, `Attempt`, `ProjectId`, `RequestId`, `ClaimToken`, `BindingRevision`, `Deadline`) to the assigned node.
5. Node (`AssignmentRecoveryRunner` / `NodeAssignmentRecoveryRuntime`): journal intent, close admission, cooperative cancel, isolated process-group stop, drain, flush acknowledged events **without deleting** the spool, inspect the canonical workspace, resolve only assignment reservations after stop evidence, report `ReportRecoveryProgress` / `ReportRecoveryProof`.
6. Linux proof: workers started under `setsid`; identity is `AssignmentProcessIdentity` (`ProcessId`, `/proc/<pid>/stat` start ticks, `ProcessGroupId`, `SessionId`). Stop enumerates `/proc` by session/group. Reused PID (start ticks mismatch) is exited. Escaped descendants are listed, not ignored. Non-Linux or missing `/usr/bin/setsid`/`/bin/setsid`: `process_stop_unproven`. Tree kill is not proof.
7. Control plane accepts proof only when inventories are known and zero, correlation matches the current attempt, claim token still fences, and `RecoveryRepositoryStatusMessage` is present. Operation statuses: `Pending`, `Running`, `NeedsIntervention`, `Recovered`. Hold remains after `Recovered`.

Budgets (`NodeOptions`, attempt deadlines only): `Node:RecoveryCooperativeStopSeconds` (10), `Node:RecoveryTerminationSeconds` (20), `Node:RecoveryAttemptSeconds` (60, must be ≥ sum of the first two). `ProjectRecoveryService.AttemptDeadline` is 60s.

## Blocker meanings (`RecoveryReasonCodes`)

| Code | Meaning | Operator next step |
|---|---|---|
| `project_recovery_paused` | Hold blocks claims | Finish recovery; resume only after `Recovered` |
| `node_unreachable` | No live connection for `RecoverAssignment` | Restore node; recheck. Not proof of stop |
| `process_stop_unproven` | Isolation/exit not proved | Linux: inspect group; non-Linux: manual attestation |
| `operation_drain_timeout` | Drain exceeded budget | Recheck after local stop; do not delete state |
| `events_unacknowledged` | Spool not flushed | Let the node replay; never wipe `Node:EventSpoolPath` |
| `reservation_unresolved` | Lease still Active/RecoveryRequired | Wait for recovery disposition; force-release is not stop proof |
| `repository_status_unknown` | Canonical workspace not inspectable | Inspect on the owning machine; do not attest another checkout |
| `recovery_evidence_stale` | Proof/attestation does not match current attempt | Recheck; refresh diagnosis |
| `recovery_target_changed` | Inventory revision mismatch | Re-read GET diagnosis; click **Recover project** again |

Existing readiness/startup codes are unchanged.

## Safe local checks

On the **owning** node, for this assignment's journaled group/session only:

- Confirm Linux `setsid` identity still matches the journal (PID + start ticks + pgid/sid).
- Stop that group if it is still live; verify `/proc` no longer lists matching identities.
- Inspect the **canonical** WorkspaceBinding path (HEAD/branch, dirty/untracked, interrupted Git). Do not delete `.git` locks automatically.
- Restore connectivity so the owner can send proof.

Optional: restart `pi-command-center-node.service` affects every Project on that node and is **not** quiescence.

## Manual attestation (`confirm-manual`)

Only when the operation is `NeedsIntervention`. Authenticated administrator only. Provenance is `operator-attestation`, never node proof.

`POST /api/projects/{projectId}/recoveries/{recoveryId}/confirm-manual` body `ConfirmManualProjectRecoveryRequest`:

- `ExpectedOperationVersion`, `ExpectedAttempt`, `ExactProjectName`, `IdempotencyKey`
- `ConfirmOriginalExecutionCannotResume`, `WriterAccessPrevented`, `AcknowledgeEvidenceGaps` — all required true
- `ProcessStopEvidence`, `ReservationAndEventGapAccounting` (max 1024 chars each via `ManualRecoveryService.MaxTextLength`)
- `RepositoryStatusSnapshot`, `RepositoryStatusSource`, `RepositoryCollectedAt` (must be the owning workspace; age ≤ `MaxRepositoryEvidenceAge` = 15 minutes)

No `Reason` field. Audit description is server-authored `Administrator confirmed manual recovery after evidence review.` Missing event history may be accounted as an audit gap. Unresolved writer access or unavailable repository status cannot be waived. Fencing/token revoke cannot stop an open process or fd. Success keeps the hold. Action name `confirm-manual`.

## Recheck (not resume)

`POST /api/projects/{projectId}/recoveries/{recoveryId}/recheck` body `RecheckProjectRecoveryRequest`: `ExpectedOperationVersion`, `IdempotencyKey`. Allowed from `NeedsIntervention` (increments `Attempt`, status `Running`). Already `Pending`/`Running` is idempotent. `Recovered` cannot recheck. Does not clear the hold.

## Resume (separate)

`POST /api/projects/{projectId}/recovery/resume` body `ResumeProjectRecoveryRequest`: `OperationId`, `ExpectedHoldVersion`. Requires operation `Recovered`, every captured assignment terminal, every captured reservation `Released`. Removes `RecoveryHoldRow` only. Does not set `Project.Enabled`. Ordinary eligibility still applies.

## Linked retry

`POST /api/projects/{projectId}/requests` with `QueueWorkRequestCommand.OriginalRequestId`. Must be the same Project; stored immutable on `WorkRequest.OriginalRequestId` (`IX_WorkRequests_OriginalRequestId`, Restrict delete). Does not copy claim tokens, sessions, leases, or assignment snapshots. Enqueue does not resume the hold.

## Returning node

Reconnect reconciles before `ClaimNext`. `RecoveryAttemptDispatcher.DispatchForNodeAsync` re-sends `RecoverAssignment` for open targets. `Cancelling` dispositions stay cancel. Terminal assignments stay terminal; replay cannot reopen them. Stale claim tokens fail closed. Contradictory writer evidence keeps/reinstates the hold.

## Never delete or force

Never delete or truncate:

- Workspace files, uncommitted changes, branches
- Control-plane SQLite (`ConnectionStrings:ControlPlane`)
- Node assignment journal or `Node:EventSpoolPath`
- Recovery operation/hold/idempotency/audit rows as a shortcut

Never:

- Treat silence, lease expiry, disconnect, or service restart as stop proof
- Use reservation `force-release` instead of recovery
- Kill by executable name, path substring, or a bare reused PID
- Kill unrelated Projects' processes
- Pass `force=true`, skip inventory fencing, or skip process-stop evidence, exact-name confirmation, or required acknowledgements
- Resume the original assignment or copy its execution authority onto a retry
