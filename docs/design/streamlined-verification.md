# Streamlined verification

**Status:** Implemented
**Scope:** Make development-request verification complete safely with zero verification-profile setup while preserving independent model review and trusted node-owned command execution
**Review state:** Design review completed 2026-09-06; findings incorporated. Operator accepted the three pre-implementation decisions on 2026-09-06. Canonical docs and deployment examples record the delivered contract.

## Sources reviewed

- `README.md`
- `SPEC.md`, especially §§11–13, 18–21, and 32
- `docs/architecture.md`
- `docs/protocols.md`
- `docs/security.md`
- Current routing, root-tool, child-tool, verification-runner, request-projection, completion-gate, admission-gate, Git argv policy, configuration, UI, and test code under `src/`, `runtime/`, and `tests/`
- The live empty-profile failure mode: rejected profile guesses, no persisted verification run, and a request left in `Verifying`

## Decision

A development request must work when the operator has configured a usable reviewer or verifier model route but has not configured `Verification:Profiles`.

DevFleet will provide a mandatory, built-in **baseline verification** implemented by the node supervisor. The supervisor—not the root model—selects and runs the effective verification policy. The root never guesses profile ids, command ids, executables, or arguments.

Project-specific test commands remain an optional, stronger layer. They continue to come only from trusted node configuration and can be selected for a Project through the web UI. DevFleet never infers and executes repository scripts merely because it discovers a manifest.

This preserves two distinct forms of assurance behind one operator experience:

1. **Independent agent verification** — a reviewer or verifier child examines the implementation, with its model chosen by frontend role routing.
2. **Deterministic node verification** — the node runs the built-in baseline and any explicitly selected trusted project checks.

The request page presents these as one verification stage without implying that model routing configures executable commands.

## Problem

Today, verification has two unrelated configuration surfaces:

- `/routing` configures the model candidates for the `reviewer` and `verifier` child roles.
- `Verification:Profiles` configures trusted node commands and has no web configuration or required default.

The root tool nevertheless requires a free-form `profileId`, and child sessions carry an equivalent `run_verification_command` tool (SPEC §18.1, `runtime/pi-worker/src/childTools.ts`) that reaches the same node handler. With an empty profile dictionary, the root can repeatedly guess names such as `default`, `quickstart`, or `mandatory-verification`. Each rejected call emits `verification.started`, so the request advances to `Verifying` even though no command ran. The completion gate then waits forever because it requires at least one passed mandatory `VerificationRun`.

This violates the expected product contract: an apparently ready project can accept work that cannot complete, and the UI exposes no setup action that would have prevented it.

## Goals

- A newly installed node and newly designated Project can complete a development request without editing JSON or environment variables.
- Configuring a usable reviewer or verifier route in the frontend is sufficient for the agent side of verification.
- The root uses a semantic verification operation and cannot select process execution details.
- `Verifying` means an admitted verification operation is running or has produced an actionable result.
- One repository revision is verified at most once per effective policy revision unless the operator explicitly reruns it.
- Failures block with one clear reason; they do not cause unbounded tool calls, child spawns, or status churn.
- The UI accurately distinguishes baseline checks from project test-suite checks.
- Existing sandbox, reservation, assignment, output-bound, timeout, and completion-gate protections remain intact.

## Non-goals

- Automatically executing `npm test`, `dotnet test`, `pytest`, Make targets, package scripts, or repository-provided binaries based on file discovery.
- Treating an agent's prose approval as deterministic command verification.
- Granting the root, reviewer, or verifier shell access.
- Letting a prompt supply an executable, arguments, working directory, timeout, or profile id.
- Claiming that the built-in baseline is equivalent to a project's full test suite.
- Adding public or multi-user command administration.

## Ubiquitous language

| Term | Meaning |
|---|---|
| **Verification stage** | The user-facing stage containing independent agent verification and deterministic node verification. |
| **Agent verification** | Independent review performed by a child session whose model is selected by the `reviewer` or `verifier` role route. |
| **Baseline verification** | Mandatory zero-configuration checks implemented by trusted node code and available for every valid workspace. |
| **Project checks** | Optional commands from a trusted node profile explicitly selected for a Project. |
| **Effective verification policy** | Baseline verification plus the Project's selected trusted profile, if any. |
| **Verification fingerprint** | Assignment, repository revision/diff identity, and effective-policy revision used for idempotency and staleness. |
| **Final verification** | The effective policy run whose results the completion gate evaluates. Only the coordinator performs it. |
| **Intermediate check** | A child-requested run of the Project's selected project checks during implementation. It never affects request phase or the completion gate. |
| **Policy snapshot** | The effective policy captured on an ExecutionAssignment: baseline version, selected profile id and revision, and the mandatory command ids the completion gate must see pass. |

Do not use **verifier route** and **verification profile** interchangeably in code, documentation, or UI copy.

## User experience

### Installation and first request

A default installation requires no verification section in node configuration. The node always advertises baseline verification as ready.

For a simple request:

1. The operator configures or accepts a ready `reviewer` or `verifier` role route.
2. The operator designates a workspace and queues the request.
3. The root delegates implementation and independent review.
4. On completion submission, the supervisor runs final verification automatically if the current fingerprint has no green result.
5. The request completes when independent review, baseline verification, and the existing completion conditions pass.

No profile name is shown to or requested from the root.

### Routing page

Keep the `verifier` role id unchanged; it is a routing and configuration key (`src/PiCommandCenter.Node/PiWorkerOptions.cs`). Change display copy only: label the role **Verification agent** and show this fixed clarification:

> Chooses the model used for independent agent verification. Deterministic checks run automatically under the Project's verification policy.

Routing updates continue to configure models only. They must not silently create or alter executable command definitions.

### Project page

Add a **Verification policy** card:

- **Automatic baseline** — always enabled and sufficient for zero-configuration completion.
- **Project checks** — `None` by default, or one trusted profile selected from the connected node's bounded profile catalog.
- Readiness — ready, node offline, selected profile unavailable, or policy revision stale.
- Copy when no project checks are selected: `Baseline repository checks will run. No project test suite is configured.`

The profile catalog exposes ids, display labels, command ids, working-directory labels, mandatory/optional flags, and timeout budgets. It does not expose environment values, credentials, raw configuration paths, or arbitrary editable command text.

### Request page

The Verification section shows separate rows for:

- independent agent review/verification;
- built-in baseline checks;
- configured project checks, when present.

While running, show the current check, elapsed time, and timeout budget. On success, use precise copy such as:

- `Baseline checks passed.`
- `Project checks passed: dotnet-test, runtime-test.`

When only the baseline ran, never display `All tests passed`.

Replace the current empty-state copy on the Request page, which tells the operator that runs are recorded "when the root agent calls `request_verification`". The new copy explains that final verification runs automatically on completion and that project checks are selected on the Project page.

Intermediate checks requested by children appear in a separate, collapsed history list labelled as intermediate. They never appear in the final verification rows.

A rejected precondition remains in the prior phase and shows an actionable reason. A failed admitted check blocks the request in its `Verifying` phase and links to bounded output.

## Baseline verification

The baseline is a deep node module, not a shell profile. Its interface is stable while its implementation may grow.

The baseline consists of two commands under one built-in profile.

**`repository-integrity` (mandatory)** must prove:

- the assignment still owns the expected canonical workspace and binding revision;
- no active source-mutation reservation exists;
- the repository can be inspected through the trusted Git module;
- the request diff has no unmerged index entries;
- changed paths remain within the canonical workspace and exclude protected Git metadata.

**`whitespace` (optional)** reports whitespace errors equivalent to `git diff --check`, honouring the repository's `core.whitespace` and `.gitattributes` settings. It is optional because Markdown hard line breaks, patch fixtures, and similar files legitimately contain trailing whitespace, and a baseline must not turn a style nit into a completion blocker with no operator override. Its result is persisted and displayed as a warning. Untracked new files are included by checking them with `git diff --no-index --check` against the empty blob, since a diff from the baseline commit only covers tracked paths.

Changed-file ownership is **not** re-evaluated by the baseline. The completion gate's existing `OwnershipKnown` requirement remains the single authority for attribution; the baseline reports unattributed paths only as informational output.

The implementation should reuse trusted repository and completion evidence rather than invoke repository hooks, filters, scripts, or an unrestricted shell. If a Git subprocess is unavoidable, it must use fixed argv, disable external diff and text-conversion execution (`--no-ext-diff`, `--no-textconv`), disable hooks, and run under the existing verification sandbox. Every new subcommand or flag the baseline needs (`diff --check`, `diff --no-index`, `ls-files -u`, `hash-object`, `write-tree`) must be added to the trusted allowlist in `src/PiCommandCenter.Node/Repository/GitArgvPolicy.cs`; that allowlist change is part of the security review for this work.

Persist each result through the existing verification projection with stable identifiers:

```text
ProfileId: devfleet-baseline
CommandId: repository-integrity   Mandatory: true
CommandId: whitespace             Mandatory: false
```

The baseline receives a bounded timeout owned by DevFleet. The initial target is 30 seconds per command, configurable by the node operator but never by agent content; a timeout on the mandatory command is a failed mandatory run and produces an actionable blocked state.

## Project checks

Project checks strengthen, but never replace, the baseline.

- Command definitions remain node-owned trusted configuration.
- A Project stores only the selected profile id and the node-reported profile revision.
- New Projects select no project profile by default.
- Selecting a profile is an explicit administrator action in the Project page.
- The node validates the selection against the Project's designated WorkspaceBinding and its own current catalog.
- A missing or stale selected profile makes the Project ineligible for new assignment with `verification_policy_unavailable`; it must not fail for the first time after implementation work finishes.
- Existing command execution keeps fixed argv, repository-relative working directories, Bubblewrap isolation, hidden credentials, network isolation, output bounds, per-command timeouts, `project-build` reservation, and incompatibility with active mutation.
- Optional command failures are persisted and displayed but do not independently fail completion.

### Upgrade migration

An installation that gates on a `default` profile today would silently lose its test suite from completion gating if upgrade left every Project at `None`. That is a weakening of verification and is not acceptable as a silent default. The upgrade therefore:

- runs a one-time migration that selects `default` for every Project that has at least one persisted `VerificationRun` with `ProfileId = default`, provided the designated node still advertises a `default` profile at first heartbeat after upgrade;
- records the migration as an audit event and a startup diagnostic naming each migrated Project;
- shows a persistent Project-page warning, `Node advertises trusted profiles but none is selected`, for any Project whose node advertises profiles while the Project remains baseline-only;
- never selects a profile for a Project with no verification history.

Assignments in flight at upgrade have no captured policy snapshot. The coordinator captures one lazily at their first final verification or completion attempt, using the Project's post-migration selection. Assignments created after upgrade always capture the snapshot at assignment time.

## Child verification tool

Children currently expose `run_verification_command(profileId, commandId?)` (SPEC §18.1), which reaches the same node handler as the root tool. Leaving it unchanged would let an implementer's mid-work run emit `verification.started`, advance the request to `Verifying` early, and persist mandatory rows that the completion gate counts.

Replace it with a parameterless **intermediate check**:

```text
run_project_checks()
```

Rules:

- It runs only the Project's selected project checks. It never runs the baseline and never selects a profile or command.
- If the Project has no project checks, it returns `no_project_checks` without launching anything.
- It executes under the same `project-build` lease, sandbox, output bounds, timeouts, and active-mutation incompatibility as final verification. The calling child's own reservation counts as active mutation, so the child must release or the supervisor must pause its lease for the duration; the response says which.
- Runs are persisted with `RunKind: Intermediate`, are ignored by the completion gate and by fingerprint reuse, and never emit `verification.started`, `verification.completed`, or `verification.failed`. They emit `verification.intermediate` for history only, which never changes request phase.
- Results are returned to the child as a bounded summary.

The worker protocol carries the same one-release compatibility for the legacy child fields as for the root tool.

## Orchestration interface

Introduce one deep module at the node seam:

```csharp
public interface IRequestVerificationCoordinator
{
    Task<RequestVerificationDecision> VerifyFinalAsync(
        RequestVerificationContext context,
        CancellationToken cancellationToken);
}
```

The context contains assignment identity, requesting root session, repository/binding snapshot, repository fingerprint, and the assignment's captured policy snapshot. It contains no caller-selected profile or command.

The coordinator hides:

- policy resolution;
- idempotency and stale-result detection;
- precondition checks;
- `project-build` acquisition and release;
- baseline execution;
- optional trusted-profile execution;
- persistence of every admitted run;
- lifecycle events and request-phase projection;
- bounded summaries returned to the root;
- cancellation and timeout mapping.

`VerifyFinalAsync` returns one of:

- `Passed` — all mandatory checks for the fingerprint passed;
- `Failed` — an admitted mandatory check failed or timed out;
- `Rejected` — verification did not start because a precondition was not met;
- `Cancelled` — assignment cancellation won;
- `Reused` — an existing green result matches the same fingerprint and policy revision.

The deletion test applies: removing this module would force policy, leasing, idempotency, persistence, and status rules back into root tools, completion, and UI callers.

## Root tool and completion behavior

Replace the current free-form root interface:

```text
request_verification(profileId, commandId?)
```

with:

```text
request_verification()
```

The tool requests the effective policy for the current repository fingerprint. It is useful for an explicit pre-completion run but is not required for correctness.

`submit_completion` becomes the deterministic backstop:

1. Evaluate non-verification prerequisites that can be checked without terminalizing.
2. If the current fingerprint lacks a green effective-policy result, call `VerifyFinalAsync`.
3. If verification fails, return one bounded failure and keep/re-enter the request as `Blocked` with `BlockedPhase=Verifying`.
4. If verification is rejected, leave the request in its prior phase and return the precondition action.
5. If verification passes or is reused, include the verified fingerprint in the completion evidence and continue through the existing completion and quiescence gate.
6. At `Begin`, after `TryEnterTerminalization` but before `CloseAdmission`, the node re-inspects the repository and recomputes the fingerprint. Operations and children are still admitted during this window, so a child may have mutated the workspace after the green result. If the recomputed fingerprint differs from the verified one, `Begin` is rejected with `verification_stale`, admission stays open, the request keeps its phase, and the root receives one actionable response. The coordinator may then run once more for the new fingerprint.

The supervisor must not ask the model to choose another profile after `unknown_profile`. Under the new interface, `unknown_profile` is a configuration/readiness error outside agent control.

The worker protocol may accept legacy `profileId` and `commandId` fields for one compatibility release, but the node ignores them and records a deprecation diagnostic. New workers omit both fields.

### Completion gate rule

The control plane owns the gate (`AssignmentTerminalizationService`) and cannot infer the effective policy from run rows alone. With a baseline that always passes, the current "at least one passed mandatory row" check would accept a request whose selected project checks never ran. The gate therefore changes to:

1. Load the assignment's policy snapshot: baseline version, selected profile id and revision, and the list of mandatory command ids.
2. Take the fingerprint and policy revision reported in the completion evidence and confirmed by the node's re-inspection at `Begin`.
3. Consider only `VerificationRun` rows with `RunKind` of `Baseline` or `ProjectCheck` whose `Fingerprint` and `PolicyRevision` match. Rows for other fingerprints are stale history and are ignored; `Intermediate` rows are always ignored.
4. Require exactly the snapshot's mandatory command ids to each have a `Passed` row. A missing row is reported as `<commandId> verification has not run`; a failed row as `<commandId> verification failed`.
5. Optional rows never affect the decision.

`OwnershipKnown`, review completion, reservation release, and the quiescence barrier are unchanged.

## State and event rules

        - Resolve policy and acquire admission before emitting `verification.started`.
        - Only `verification.started` may advance a request to `Verifying`.
        - Emit `verification.command.started` before each baseline or profile command with fingerprint, policy revision, command id, run kind, mandatory flag, start/event time, and timeout seconds. It is a bounded progress fact and never changes request phase.
        - Emit `verification.rejected` when no operation was admitted. This event never changes request phase.
        - Emit `verification.intermediate` for child-requested intermediate checks. This event never changes request phase and is never counted by the completion gate.
        - `verification.rejected`, `verification.intermediate`, `verification.cancelled`, and `verification.command.started` must remain in the SPEC §22 event contract. The Request page reduces current command, elapsed, and timeout budget from the latest open final lifecycle plus matching progress, and clears them on a matching terminal event.
        - Emit exactly one terminal event per admitted run: `verification.completed`, `verification.failed`, or `verification.cancelled`.
        - Persist each baseline/project check before emitting the terminal profile event.
        - A failed mandatory run blocks once; it does not automatically retry.
        - A new repository fingerprint makes prior runs stale but retains them as history.
        - A repeated call for the same fingerprint and policy revision returns `Reused` without launching a process or writing duplicate rows.
        - A changed policy revision invalidates reuse even when the repository revision is unchanged.
        - The UI derives elapsed time from the persisted run, admitted-operation event, or command-progress event, not from the request's general `UpdatedAt`.

### Verification fingerprint

        The fingerprint includes at least:

        ```text
        ExecutionAssignment.RequestId
        ExecutionAssignment binding validation revision
        baseline commit
        current request branch/head
        normalized changed-path and content identity
        Git-relevant executable mode of opened regular worktree/untracked files
        verification policy revision
        ```

        It must not include timestamps or other values that change without repository or policy changes.

        Content identity covers unstaged tracked changes and untracked files. Compute it from opened regular-file handles (content hash plus Git executable bit) so that the repository's real index, staging state, and working tree are never touched. SPEC §3.7 makes the supervisor the owner of staging, and this rule keeps fingerprinting from becoming a hidden staging step. Capture and each full baseline command share one total deadline covering metadata prep, traversal, hashing, and Git loops.

## Scheduling and readiness

Node execution readiness gains a bounded verification-policy summary:

- built-in baseline availability and version;
- trusted profile ids and revisions;
- whether each profile passed node startup validation.

The authoritative eligibility evaluator adds `verification_policy_unavailable` after runtime readiness and before capacity. This reason applies only when a Project explicitly selected project checks that the designated node can no longer provide. Baseline-only Projects remain eligible on a normal installation.

The scheduler never requires the root model to discover verification configuration.

## Security invariants

1. Agent content never chooses executable, argv, working directory, environment, timeout, or profile.
2. The built-in baseline does not execute repository-provided code.
3. Project checks execute only definitions already trusted by the assigned node and selected by the administrator.
4. Every verification operation remains assignment-, request-, project-, binding-revision-, and session-authorized.
5. Verification remains incompatible with active source mutation and owns `project-build` while admitted.
6. Provider and node credentials remain hidden from baseline and project checks.
7. Network, process-tree, output, and filesystem bounds remain fail-closed.
8. A pass is reusable only for the exact verification fingerprint and policy revision.
9. The UI never overstates baseline verification as test-suite coverage.

## Failure behavior

| Failure | Required result |
|---|---|
| Baseline cannot inspect repository | One failed mandatory baseline run; block in `Verifying` with a stable reason. |
| Active mutation exists | `Rejected`; remain in the prior phase and identify the active prerequisite. |
| Selected trusted profile disappears before assignment | Keep request queued with `verification_policy_unavailable`. |
| Captured profile disappears during an assignment | Fail closed and block with an operator action; do not ask the model to guess. |
| Project command exits nonzero | Persist output/status and block if mandatory. |
| Project command times out | Kill its process tree, persist `TimedOut`, and block if mandatory. |
| Root repeats verification unchanged | Return `Reused`; no new process, row, or phase churn. |
| Root settles after a failed call | Request remains visibly blocked rather than silently `Verifying`. |
| Node/control-plane disconnects during verification | Preserve assignment ownership and reconcile through existing recovery rules. |

## Data and transport changes

### Project policy

Add a nullable verification-policy selection:

```text
TrustedVerificationProfileId
TrustedVerificationProfileRevision
```

Null means baseline only. These fields are fleet policy references, not executable definitions.

### Verification runs

Add or derive:

```text
Fingerprint
PolicyRevision
RunKind: Baseline | ProjectCheck | Intermediate
AttemptId
```

Enforce uniqueness sufficient to prevent duplicate rows for one command, fingerprint, policy revision, and non-intermediate run kind while retaining later attempts after a repository change. Today `VerificationRuns` has only a `RequestId` index.

### ExecutionAssignment policy snapshot

Add to the assignment:

```text
VerificationPolicyRevision
BaselineVersion
TrustedVerificationProfileId (nullable)
TrustedVerificationProfileRevision (nullable)
MandatoryCommandIdsJson
```

The snapshot is captured from the node catalog at assignment time (or lazily for pre-upgrade assignments) and is the completion gate's only source for the mandatory command list.

### Completion evidence

`CompletionEvidence` gains `VerificationFingerprint` and `VerificationPolicyRevision`. The node populates them from the coordinator's last green result and confirms them by re-inspection at `Begin`.

### Node callbacks

Add authenticated node callbacks for:

- reading the bounded verification catalog/readiness;
- validating and applying a Project profile selection.

No executable path, environment value, credential, or raw command output crosses catalog responses.

### Worker protocol

`verification.request` carries no profile or command selector. Correlation and assignment context continue to come from the session transport. The child intermediate check uses a distinct `verification.intermediate.request` type so the node can never confuse it with final verification.

## Acceptance criteria

### Zero-configuration completion

Given:

- `Verification:Profiles` is empty;
- the built-in baseline is healthy;
- a reviewer or verifier route reports ready;
- a valid workspace is designated;

when a development request creates a simple Python script, completes independent review, and submits completion,

then:

- baseline verification runs exactly once;
- one mandatory passed `VerificationRun` is persisted;
- no `unknown_profile` event occurs;
- no profile id is requested from the root;
- the request reaches `Completed` if all other gates pass.

### Rejected calls do not fake progress

Given an active source-mutation lease, when final verification is requested, then `verification.rejected` is emitted, the request does not enter `Verifying`, and the response identifies the lease prerequisite.

### No retry storm

Given a failed mandatory check and an unchanged fingerprint, repeated completion submissions do not rerun commands, spawn replacement children, or append duplicate run rows. The request remains blocked with one actionable failure.

### Rerun after repair

Given a failed check, when an authorized implementer changes the repository and releases its reservation, the new fingerprint permits exactly one new verification attempt. A green attempt supersedes the stale red attempt for completion evaluation while preserving both in history.

### Project checks

Given a Project explicitly selects a trusted `dotnet` profile, final verification runs baseline first and then the profile. Completion requires every mandatory check in the effective policy to pass.

### Selected checks cannot be skipped

Given a Project with a selected trusted profile whose mandatory command never ran for the current fingerprint, when the baseline passes and completion is submitted, then completion is rejected naming the missing command, and the baseline row alone never satisfies the gate.

### Intermediate checks are inert

Given an implementer child calls `run_project_checks`, then the run is persisted as `Intermediate`, no `verification.started` is emitted, the request phase is unchanged, and a later final verification for the same fingerprint still runs the effective policy once.

### Stale result at Begin

Given a green final verification followed by a child mutation before completion is submitted, when completion is submitted, then `Begin` is rejected with `verification_stale`, admission stays open, and one new verification attempt for the new fingerprint is permitted.

### Upgrade keeps gating

Given a pre-upgrade Project with persisted `default` runs on a node that still advertises `default`, after upgrade the Project's selection is `default`, the migration is audited, and the first post-upgrade request requires the `default` mandatory commands to pass.

### Honest UI

Given baseline-only success, the request page says baseline checks passed and no project test suite was configured. It never says all tests passed.

### Restart and replay

Given the control plane restarts after persisted verification success but before terminalization, reconciliation reuses the matching green fingerprint and does not rerun commands or release assignment ownership early.

## Implementation sequence

1. **Lock down the failure with tests**
   - Reproduce empty profiles plus a ready verifier route.
   - Assert the current free-form request fails and incorrectly advances status.
   - Add completion/idempotency tests for the target behavior.

2. **Add baseline verification**
   - Implement the non-shell repository-integrity checks behind a focused internal interface.
   - Persist a mandatory `devfleet-baseline/repository-integrity` run.

3. **Add the verification coordinator**
   - Move policy resolution, leases, execution, persistence, events, and fingerprint reuse behind `IRequestVerificationCoordinator`.
   - Replace direct `IVerificationCommandRunner` use in orchestration callers.

4. **Remove agent-selected profiles**
   - Change `request_verification` to a parameterless semantic operation.
   - Replace the child `run_verification_command` with `run_project_checks` and the `Intermediate` run kind.
   - Add one-release compatibility handling and update Pi worker tests.

5. **Backstop completion**
   - Make `submit_completion` invoke or reuse final verification before terminalization.
   - Add the fingerprint re-inspection at `Begin` and the `verification_stale` rejection.
   - Replace the gate's "any mandatory row" rule with the policy-snapshot rule above.
   - Block once on failure and allow rerun only after repository/policy change.

6. **Correct status projection**
   - Add `verification.rejected`.
   - Emit `verification.started` only after admission and policy resolution.
   - Ensure rejected operations cannot advance `WorkRequest` to `Verifying`.

7. **Add Project policy and node catalog**
   - Persist nullable trusted-profile selection and revision.
   - Persist the assignment policy snapshot, including mandatory command ids.
   - Add readiness, authenticated callbacks, eligibility reason, and UI selection.
   - Add the one-time `default` selection migration and its audit/diagnostic output.

8. **Streamline UI copy and progress**
   - Clarify Verification agent routing without renaming the role id.
   - Add Project verification-policy card and separate Request verification rows.
   - Replace the Request page empty-state copy and render the new event types.

9. **Update canonical documentation and deployment**
   - Update `SPEC.md` §§13, 14 (remove the plan's `verificationProfile` field), 15.1 (routing and verification relationship), 18.1 (child tool list), 19, 20, 21, 22 (new event types), 24, 25.3–25.4 ("configured verification" wording), 29 (assignment snapshot, run columns, evidence), 31, 32 (gate rule), 35 (empty profiles valid), and 38 (new acceptance tests).
   - Update `README.md`, `docs/architecture.md`, `docs/protocols.md`, `docs/security.md`, and deployment examples.
   - Empty `Verification:Profiles` must be documented as a valid baseline-only installation.

10. **Verify end to end**
    - Run targeted .NET and TypeScript tests, then `./scripts/verify.sh`.
    - Exercise empty-profile and configured-profile requests through the web UI.
    - Keep real-provider execution opt-in.

## Current contract changes

This proposal intentionally supersedes these current behaviors:

- `request_verification` requiring agent-supplied `profileId` and optional `commandId`;
- the child `run_verification_command` tool accepting agent-supplied `profileId` and `commandId` and sharing the final-verification lifecycle;
- the completion gate accepting any passed mandatory row rather than the assignment's captured policy;
- empty `Verification:Profiles` making development completion impossible;
- `verification.started` being emitted before profile validation/admission;
- completion treating any historical mandatory failure as permanently blocking instead of evaluating the current fingerprint and policy;
- frontend role routing appearing sufficient while an undiscoverable backend profile requirement remains.

The delivered contract supersedes those behaviors. Verifier routing remains a model route for independent agent review; deterministic checks are the Project verification policy. Empty `Verification:Profiles` is a valid baseline-only installation.

## Decisions required before implementation (accepted 2026-09-06)

Each of these changes the data model or protocol. The operator accepted all three on 2026-09-06 with the recommended default; the text below records the alternatives that were considered.

1. **Child intermediate checks** — keep them (recommended, as `run_project_checks`) or remove child-initiated verification entirely.
2. **Whitespace as optional** — the baseline reports whitespace errors as a warning rather than a blocker. Confirm, or name the conditions under which it should be mandatory.
3. **`default` migration** — automatic selection for Projects with prior `default` runs. Confirm, or choose the startup-warning-only alternative, accepting that upgrade then weakens gating until an administrator acts.
