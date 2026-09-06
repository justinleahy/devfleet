# Research: project recovery implementation

**Date researched:** 2026-09-06.  
**Design:** [docs/design/project-recovery.md](../design/project-recovery.md) (proposed; not implemented).  
**Local seams inspected:** `src/PiCommandCenter.Node/Runtime/NodeWorkerProcess.cs`, sibling `*ProcessFactory.cs` kill paths, `src/PiCommandCenter.Infrastructure/Requests/ExecutionAssignmentService.cs`.

This note does not restate the design. It records **authoritative OS/.NET/EF/HTTP facts**, maps them onto **existing symbols**, and lists **implementation choices** so automatic recovery can prove stop instead of guessing.

---

## Primary sources

| Topic | URL |
|---|---|
| `setsid(2)` | https://man7.org/linux/man-pages/man2/setsid.2.html |
| `kill(2)` (process groups) | https://man7.org/linux/man-pages/man2/kill.2.html |
| `credentials(7)` (PID, PGID, SID) | https://man7.org/linux/man-pages/man7/credentials.7.html |
| `proc_pid_stat(5)` (`pgrp`, `session`, `starttime`) | https://man7.org/linux/man-pages/man5/proc_pid_stat.5.html |
| `daemon(3)` (fork/`setsid`; double-fork note) | https://man7.org/linux/man-pages/man3/daemon.3.html |
| cgroup v2 (`cgroup.procs`, `cgroup.kill`) | https://docs.kernel.org/admin-guide/cgroup-v2.html |
| `systemd.scope(5)` | https://www.freedesktop.org/software/systemd/man/latest/systemd.scope.html |
| `systemd-run(1)` `--scope` | https://www.freedesktop.org/software/systemd/man/latest/systemd-run.html |
| systemd control-group D-Bus | https://systemd.io/CONTROL_GROUP_INTERFACE |
| .NET `Process.Kill` / `Kill(bool)` | https://learn.microsoft.com/en-us/dotnet/api/system.diagnostics.process.kill |
| .NET `Process.StartTime` | https://learn.microsoft.com/en-us/dotnet/api/system.diagnostics.process.starttime |
| EF Core transactions | https://learn.microsoft.com/en-us/ef/core/saving/transactions |
| EF Core optimistic concurrency | https://learn.microsoft.com/en-us/ef/core/saving/concurrency |
| EF Core SQLite limitations (no native rowversion) | https://learn.microsoft.com/en-us/ef/core/providers/sqlite/limitations |
| SQLite transaction control | https://www.sqlite.org/lang_transaction.html |
| HTTP `202 Accepted`, `409 Conflict` | https://www.rfc-editor.org/rfc/rfc9110.html |
| ASP.NET Core CSRF / antiforgery | https://learn.microsoft.com/en-us/aspnet/core/security/anti-request-forgery |

Kernel/man-pages cited are Linux man-pages 6.18 / kernel cgroup-v2 docs. .NET API is **.NET 10**. systemd docs are **systemd 261.2**.

---

## Existing code (do not invent a second kill path)

### Node process launch and stop

`NodeWorkerProcessFactory` (`src/PiCommandCenter.Node/Runtime/NodeWorkerProcess.cs`):

- `ProcessStartInfo` with `UseShellExecute = false`, redirected stdio, `CreateNoWindow = true`. **No** `setsid`, **no** process-group, **no** cgroup.
- Nested `NodeWorkerProcess.KillTreeAsync` / `DisposeAsync` call `_process.Kill(entireProcessTree: true)`.
- Kill is **idempotent** via `_killRequested`; `InvalidOperationException` treated as already exited; `Win32Exception` rethrown only if `!HasExited`.
- `WaitForExitAsync` uses `EnableRaisingEvents` + `Exited`. Official docs: `Kill` is **asynchronous**; `WaitForExit` / `HasExited` **do not include descendants** even after `Kill(entireProcessTree: true)` ([Process.Kill](https://learn.microsoft.com/en-us/dotnet/api/system.diagnostics.process.kill)).

Same `Kill(entireProcessTree: true)` pattern: `AntigravityProcessFactory`, `OfficialAgentProcessFactory`, `MuseProcessFactory`, `BoundedProcessRunner`, Git/inspect/probe helpers. Some cooperative paths use `Kill(entireProcessTree: false)` first.

**Decision:** isolation must wrap **all assignment-owned supervised factories**, not only the Pi worker. Verification/Git/probe trees started **under** the assignment session/scope inherit SID/cgroup; processes started **outside** that root (node host Git inspect for a different project) must not join it.

### Control-plane claim / reconcile

`ExecutionAssignmentService`:

- `ClaimNextAsync`: `BeginTransactionAsync` → eligibility loop → `ExecutionAssignment.Create` + `request.Start` → `SaveChangesAsync` → `CommitAsync` → `PublishAssignmentChange` **after** commit.
- Claim races: `DbUpdateConcurrencyException` or nested `SqliteException` with `SqliteErrorCode` **5 or 6** (`SQLITE_BUSY` / `SQLITE_LOCKED`) → `ChangeTracker.Clear()`, return `null` (`IsClaimRace`).
- `ReconcileAsync`: one transaction; `Cancelling` yields `AssignmentReconciliationDisposition.Cancel` when inventory matches, else `RecoveryRequired`; lease expiry **renews after** `MarkRecoveryRequired` and never drops ownership (class summary).
- `RenewAsync` is **not** in that transaction; recovery must not treat ordinary renew as a bypass of reconciliation (design).
- Notifications after commit: keep this for recovery so a rolled-back accept never invalidates UI.

**Decision:** recovery accept and `ClaimNext` must share the **same hold predicate inside the claim transaction**. Eligibility evaluation today does **not** know a recovery hold; add it on `Project` (or a sibling row loaded in that transaction) **before** `ExecutionAssignment.Create`.

---

## Node isolation

### Linux session / process group

- `setsid()` creates a new session **and** process group; SID = PGID = PID of the caller; caller must **not** already be a process-group leader (`EPERM`) ([setsid(2)](https://man7.org/linux/man-pages/man2/setsid.2.html)). Reliable pattern: `fork` + parent `_exit` + child `setsid`.
- Children inherit SID/PGID across `fork`/`execve`; `setpgid` can move a process to another group **in the same session**. Sessions and groups are a **strict two-level hierarchy** ([credentials(7)](https://man7.org/linux/man-pages/man7/credentials.7.html)).
- `kill(-pgid, sig)` signals every process in that group. `kill(pid, 0)` existence/permission probe. Success for a group means **at least one** process was signaled; `ESRCH` means the group is gone (zombies still “exist” until waited) ([kill(2)](https://man7.org/linux/man-pages/man2/kill.2.html)).
- `/proc/<pid>/stat` fields: `(5) pgrp`, `(6) session`, `(22) starttime` in **clock ticks since boot** ([proc_pid_stat(5)](https://man7.org/linux/man-pages/man5/proc_pid_stat.5.html)). Comm is parenthesized and may contain spaces; parse from the last `)` backward, or use `/proc/<pid>/stat` carefully.
- `daemon(3)` forks + `setsid`; glibc does **not** double-fork, so the daemon **is** a session leader and can acquire a controlling TTY. Double-fork (`fork`, `setsid`, `fork`) is the documented way to leave the session-leader role ([daemon(3)](https://man7.org/linux/man-pages/man3/daemon.3.html)). **A descendant that calls `setsid` leaves the supervisor’s session.** Session enumeration then **misses** it. That is why the design prefers a cgroup when systemd is available.

### PID reuse

- PIDs recycle. Pair PID with `starttime` (procfs ticks) or `Process.StartTime` (cached on Unix after first read; uncached after exit can throw) ([Process.StartTime](https://learn.microsoft.com/en-us/dotnet/api/system.diagnostics.process.starttime)).
- A PID whose `starttime` no longer matches the journal is **exited**, never “still running”.
- Prefer kernel `starttime` ticks over wall-clock `DateTime` for identity (boot-relative, not NTP-shifted).

### .NET `Kill(entireProcessTree: true)` is not proof

Official remarks:

- Forces **abnormal** termination; data/resources can be lost.
- Asynchronous; wait separately.
- `WaitForExit`/`HasExited` ignore descendants.
- Descendants the caller **cannot inspect** are **silently skipped**.
- Can throw `AggregateException` if not all descendants died.
- Throws `InvalidOperationException` if the **caller is in the descendant tree**.

Unix tree walk is parent-pointer based. Double-fork + `setsid` **reparents to PID 1** and/or **new session** → **not** in the tree. **Do not** treat current `KillTreeAsync` as quiescence evidence (`process_stop_unproven`).

.NET `ProcessStartInfo` has **no** POSIX session flag. Implementation: P/Invoke `posix_spawn`/`fork`+`setsid`+`exec`, or a tiny native helper, then `Process.GetProcessById` for stdio. Do not `setsid` the **node host**.

### systemd transient scopes (preferred when the node is under systemd)

- Scope units group **externally created** processes; no main PID; unit lives while **any** process remains ([systemd.scope(5)](https://www.freedesktop.org/software/systemd/man/latest/systemd.scope.html)).
- `systemd-run --scope` runs the command as child of `systemd-run` (inherits environment) but **cgroup-managed** by the manager; **synchronous** until the command exits ([systemd-run(1)](https://www.freedesktop.org/software/systemd/man/latest/systemd-run.html)). For a long-lived worker, use the **D-Bus transient unit API** (`StartTransientUnit` with `PIDs=` / `Delegate=`) described in [CONTROL_GROUP_INTERFACE](https://systemd.io/CONTROL_GROUP_INTERFACE), not a blocking `systemd-run --scope` around the node itself.
- cgroup v2: `cgroup.procs` lists TIDs/PIDs in the group **including descendants of member processes that called `setsid`**, unless they moved cgroups. `cgroup.kill` (when present) writes `1` to SIGKILL the whole subtree ([cgroup v2](https://docs.kernel.org/admin-guide/cgroup-v2.html)).
- **Limitation:** moving into a user/system manager scope requires a working systemd bus (`XDG_RUNTIME_DIR` / user instance). Docker-without-systemd, or a node as PID 1 without cgroup write, → **session/group fallback**, and if `setsid` escape is possible → `process_stop_unproven`.
- **Do not** put unrelated projects in one scope. Name scopes per assignment id (e.g. `devfleet-assignment-<guid>.scope`). Stopping a scope must not stop the node service.

### Concrete isolation recipe (Linux)

1. Detect systemd user/system manager + writable cgroup; if yes, create a **transient scope**, place the **root worker PID** in it, journal `scope_id`.
2. Else: spawn root via `fork`+`setsid` (or equivalent), journal `sid`/`pgid` (= root PID).
3. Journal each tracked PID + `starttime` ticks + sid/pgid/scope.
4. Cooperative stop: SIGTERM to **group** (`kill(-pgid, SIGTERM)`) or scope stop (`SIGTERM` then timeout per `systemd.kill`).
5. Escalation: `kill(-pgid, SIGKILL)` or `cgroup.kill` / scope kill.
6. Proof: enumerate `/proc/*/stat` by `session` **or** read `cgroup.procs` + match `starttime`. Empty known set + no unknown members → known-zero processes. Unknown `/proc` permission → **unknown**, not zero.
7. Any live PID in the group/scope **not** in the journal → **escaped descendant**: stop with the group, list in evidence, do not claim empty inventory until gone.
8. Non-Linux: report `process_stop_unproven`; **no** tree-walk “proof”.

### Tests (process)

Must use **real** descendants, not only a fake adapter:

| Case | Expect |
|---|---|
| Child stays in session | `kill(-pgid)` / scope empty after deadline |
| `setsid` grandchild (no cgroup) | missing evidence, not success |
| `setsid` grandchild **in** transient scope | listed then killed with scope |
| Double-fork reparent to init | session or cgroup still finds it; tree walk would not |
| PID reuse | old pid+new starttime treated exited |
| Concurrent unrelated assignment | other project’s processes untouched |
| Caller-in-tree | never `Kill(entireProcessTree)` from inside the assignment tree |

---

## Durable operations, transactions, concurrency

### EF / SQLite facts that bind the accept transaction

- One `SaveChanges` is already transactional; **manual** `BeginTransactionAsync` is required when **ClaimNext-style** “read many + write one” must be atomic with a hold ([EF transactions](https://learn.microsoft.com/en-us/ef/core/saving/transactions)).
- SQLite: one writer; `BEGIN` default **DEFERRED** upgrades on first write and can hit `SQLITE_BUSY`; `BEGIN IMMEDIATE` takes the write lock at begin ([SQLite transactions](https://www.sqlite.org/lang_transaction.html)). Claim already treats busy/locked as a lost race. Recovery accept should use **the same** busy mapping, not a hung retry that outruns the operator.
- SQLite has **no** native `rowversion` ([EF SQLite limitations](https://learn.microsoft.com/en-us/ef/core/providers/sqlite/limitations)). Use **application-managed** concurrency tokens (`IsConcurrencyToken` + bump on write) for Project hold revision and recovery operation revision ([EF concurrency](https://learn.microsoft.com/en-us/ef/core/saving/concurrency)). `DbUpdateConcurrencyException` → HTTP `409` with refresh payload, same as stale diagnosis revision.
- Unique insert of a second in-flight recovery row should use a **partial unique index** (one unresolved operation per project). Unique violations are **not** `DbUpdateConcurrencyException` (EF docs); map them to “return existing equivalent operation” after re-read, or `409` if payload hash differs.

### Accept atomicity (map to design)

Single transaction, then notify:

1. Compare client **inventory revision** (concurrency token / hash of target assignment ids + binding revisions).
2. Insert/select idempotency row (`ProjectId`, `Action`, `Key`) with **hash of accepted input** + resulting `RecoveryId`.
3. Set **hold** (separate from operation status).
4. Persist operation `Pending`/`Running` + captured targets.
5. Per target: existing cancellation domain transition **if not already `Cancelling`**.
6. Commit; **then** node commands / projection publish (mirror `PublishAssignmentChange` after commit).

`ClaimNext` in the same DB: `if (project.RecoveryHold) continue` with scheduling reason `project_recovery_paused`, evaluated **before** `project_disabled`. Hold check must not be a later filter after insert.

### Idempotency keys

ASP.NET Core has **no** built-in idempotency-key middleware. Implement a table as the design states. HTTP:

- Successful new accept: **`202 Accepted`** with `Location` of the operation ([RFC 9110 §15.3.3](https://www.rfc-editor.org/rfc/rfc9110.html#name-202-accepted)): request accepted for processing that is not complete.
- Same key + **same** input hash: return the **same** operation (`202` or `200` on GET).
- Same key + **different** hash: **`409 Conflict`** ([RFC 9110 §15.5.10](https://www.rfc-editor.org/rfc/rfc9110.html#name-409-conflict)).
- Stale revision: `409` with safe refresh; **do not** cancel unseen new work.

Cookie admin routes keep antiforgery; GET diagnosis must **not** mutate ([ASP.NET CSRF](https://learn.microsoft.com/en-us/aspnet/core/security/anti-request-forgery): never change state on GET). Node bearer credentials cannot call `confirm-manual`.

### Claim vs recovery races

| Winner | Behavior |
|---|---|
| Recovery commit first | `ClaimNext` sees hold; no new assignment |
| Claim commit first, accept inventory stale | accept `409`; operator reconfirms including new assignment **or** not |
| Completion commit first | keep terminal; refresh targets; never rewrite `Completed`/`Failed` as `Cancelled` |
| Cancel already in `Cancelling` | adopt; no second transition |

`ReconcileAsync` today can still `Resume` a `RecoveryRequired` assignment when evidence matches again **except** it first marks recovery and does not auto-resume once already `RecoveryRequired` (`MarkRecoveryRequired` no-ops if already that state; later exact evidence still hits the `RecoveryRequired` branch and stays recovery). Recovery should keep sending **Cancel** disposition, not Resume, for targeted assignments.

### Tests (durable)

- Two overlapping `POST .../recoveries` with same key / different keys.
- `ClaimNext` concurrent with accept (SQLite busy + unique).
- Restart control plane mid-transaction (hold absent or fully present).
- Recheck does not create a second operation.
- Resume rejected while any target nonterminal.

---

## Evidence and quiescence contract

Current `AssignmentQuiescenceProofMessage` (design): integer counts + two booleans + timestamp. **Zero cannot mean unknown.** Recovery proof (new or versioned message) must carry:

- `operationId`, `attempt`, `assignmentId`, claim-token fence, binding revision.
- Each inventory as **known value or unknown+code**.
- Process list: pid, `starttime`, sid/pgid/scope, escaped flag.
- Spool ack **position**, not only pending count (never delete spool to force zero).
- Per-reservation disposition **after** stop evidence.
- Bounded repo snapshot; missing HEAD after startup-fail is **known empty**, not unknown FS.

Control plane accepts confirmation only if every inventory is **known and zero** (or terminalizing `Finalizing` with accepted Complete/Fail intent + same proof). Late evidence: correlate attempt + revalidate; never use pre-attempt snapshots.

Fencing/token revoke **cannot** stop an open fd ([design] + OS: signals/cgroups stop processes; leases do not). Manual attestation is labeled operator-supplied.

### Tests (evidence)

- Unknown `/proc` → not known-zero.
- Stale attempt id rejected.
- Dirty worktree allowed for ownership recovery.
- Incomplete manual form rejected; history-gap ack does not waive writer/repo.

---

## Likely new types / files (research only; not created)

| Area | Likely location |
|---|---|
| Isolation supervisor | `src/PiCommandCenter.Node/Runtime/` (e.g. session/cgroup process factory wrapping existing factories) |
| Journal fields | node assignment journal: sid/pgid/scope, pid+starttime list |
| `NodeOptions` | `RecoveryCooperativeStopSeconds`, `RecoveryTerminationSeconds`, `RecoveryAttemptSeconds` |
| Hold + operation + idempotency | Domain + EF entities/migrations; `ClaimNext` eligibility |
| HTTP | new project recovery endpoints beside existing cancel |
| Proof DTO | `PiCommandCenter.Contracts.NodeTransport` (do not overload integer-only message) |

**Order:** (1) isolation + pid/starttime tests, (2) durable hold/idempotency/`ClaimNext`, (3) proof contract + cancellation correlation, (4) API/UI, (5) manual attestation, (6) linked retry. Automatic recovery **must not** ship UI that claims process-stop proof before (1).

---

## Rejected shortcuts

| Shortcut | Why |
|---|---|
| Keep `Kill(entireProcessTree: true)` as proof | Official descendant skip + no session; design forbids |
| Kill by name / cwd / bare PID | PID reuse; collateral |
| `systemd-run --scope` wrapping the whole node | stops unrelated projects; blocks |
| SQLite `rowversion` | unsupported |
| Idempotency via event-id PK only | different action surface |
| GET that sets hold | CSRF / RFC safe methods |
| Empty process count when `/proc` unreadable | unknown ≠ zero |

---

## Decisions (actionable)

1. **Linux automatic path:** transient systemd **scope** when the bus/cgroup is usable; else **session+pgrp** via `setsid` + `/proc` by `session`/`starttime`; else `process_stop_unproven`.
2. **Identity:** journal `pid` + kernel `starttime` ticks; never kill a mismatched starttime.
3. **Stop proof:** group/scope enumeration after SIGTERM/SIGKILL deadlines (10/20/60s as design defaults); `HasExited` on the root handle is insufficient.
4. **Accept:** one EF transaction (prefer immediate write lock on SQLite) for hold + inventory + cancel intent + idempotency row; notify after commit; `ClaimNext` reads hold in its existing transaction.
5. **Concurrency:** application `IsConcurrencyToken` on project recovery revision; unique unresolved operation; busy/unique → lost race or existing op, not a second coordinator.
6. **HTTP:** `202` for accepted recovery; `409` for stale revision or idempotency payload mismatch; cookie antiforgery on mutating admin routes; no state on GET diagnosis.
7. **Evidence:** known-or-unknown inventories; escaped `setsid` children listed; fencing never substitutes for process-stop.
