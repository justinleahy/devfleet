# Research: simple request queue — "send what I need and it is queued"

**Date researched:** 2026-09-06

**Primary sources:**

- GitHub REST create issue (route-scoped identity, one required field, 201): https://docs.github.com/en/rest/issues/issues
- GitHub issue creation UX (non-blocking duplicate suggestions): https://docs.github.com/en/issues/tracking-your-work-with-issues/using-issues/creating-an-issue
- GitHub Projects draft issues (capture-now / bind-later): https://docs.github.com/en/issues/planning-and-tracking-with-projects/managing-items-in-your-project/adding-items-to-your-project
- GitHub Projects fields and workflows (defaulted metadata after capture): https://docs.github.com/en/issues/planning-and-tracking-with-projects/learning-about-projects/about-projects
- Linear creating issues (required title + status only): https://linear.app/docs/creating-issues
- Linear triage (capture vs triage as separate stages): https://linear.app/docs/triage
- Slack Lists `slackLists.items.create` (auth token + destination required, fields optional, id returned; paid plan only): https://docs.slack.dev/reference/methods/slackLists.items.create/
- AWS SQS FIFO exactly-once processing (5-minute dedup, body-hash vs explicit dedup id): https://docs.aws.amazon.com/AWSSimpleQueueService/latest/SQSDeveloperGuide/FIFO-queues-exactly-once-processing.html
- AWS SQS FIFO `MessageGroupId` (per-group ordering): https://docs.aws.amazon.com/AWSSimpleQueueService/latest/SQSDeveloperGuide/using-messagegroupid-property.html
- Azure Service Bus duplicate detection (`MessageId`-keyed, or `MessageId`+`PartitionKey` when partitioned; drop-with-success, 20s–7d window): https://learn.microsoft.com/en-us/azure/service-bus-messaging/duplicate-detection
- oh-my-pi model/provider configuration (`@smol` role; local Ollama, llama.cpp, LM Studio, and tiny-model execution): https://raw.githubusercontent.com/can1357/oh-my-pi/main/docs/models.md and https://raw.githubusercontent.com/can1357/oh-my-pi/main/docs/settings.md

This note separates **facts** (owned by the sources above or by the current codebase), **observations** (interpretation of those facts), and **decisions** (the proposal).

---

## 1. Purpose and user problem

**Observation:** The operator wants to say "send what I need and it is queued." Today the intake asks them to classify their own request before the system will take it.

**Fact (codebase):** The current intake seam requires five fields. `QueueWorkRequestCommand` is `record QueueWorkRequestCommand(WorkRequestKind Kind, RequestPriority Priority, RiskLevel RiskLevel, string Title, string Prompt)` (`src/PiCommandCenter.Application/Requests/QueueWorkRequestCommand.cs`). The Blazor composer (`src/PiCommandCenter.Web/Components/Pages/Project.razor`) requires Title and Prompt and exposes Kind/Priority/Risk selects; `WorkRequest.Normalize` throws `ArgumentException` if Title or Prompt is empty after trim (`src/PiCommandCenter.Domain/Requests/WorkRequest.cs`).

## 2. Current five-field friction

**Fact (codebase):**

| Field | Who supplies it | Default if omitted |
|---|---|---|
| ProjectId | Route `/projects/{projectId}` | From route/context |
| Title | Operator (required text field) | None — UI and `Normalize` reject empty |
| Prompt | Operator (required textarea) | None |
| Kind | Operator select | UI default `Development` |
| Priority | Operator select | UI default `Normal` |
| RiskLevel | Operator select | UI default `Standard` |

**Observation:** Three of the five operator fields already default to the same values the UI pre-selects, and the domain treats Kind/Priority/RiskLevel as stored constructor data, not scheduling gates. Only Prompt carries operator intent; Title is a display requirement the domain happens to enforce. The form therefore measures the operator's patience, not the request's content. Queue ordering is `Priority` descending, then `CreatedAt` ascending (`src/PiCommandCenter.Infrastructure/Requests/RequestQueue.cs` `ListAsync`), so an operator who never touches the Priority select gets the same ordering semantics as one who does — minus the ceremony.

## 3. External patterns (facts with sources)

1. **Identity on the route, one required text field, immediate 201.** GitHub's `POST /repos/{owner}/{repo}/issues` puts repository identity in the path, requires only `title` in the body, and returns 201 Created; `body`, labels, assignees, milestone are optional. https://docs.github.com/en/rest/issues/issues
2. **Duplicate hints never block capture.** GitHub may suggest existing issues as you type; the suggestions are non-blocking and do not prevent creating the issue. https://docs.github.com/en/issues/tracking-your-work-with-issues/using-issues/creating-an-issue
3. **Capture-now / bind-later.** GitHub Projects draft issues exist only in the project — "type your idea, then press Enter"; repository, labels, and milestones require a later conversion step. https://docs.github.com/en/issues/planning-and-tracking-with-projects/managing-items-in-your-project/adding-items-to-your-project
4. **Metadata defaults after capture are first-party.** Creating an issue in a view grouped by a field auto-sets that field to the group's value (adding-items page); separately, built-in project workflows can set fields when items are added or changed (about-projects page). https://docs.github.com/en/issues/planning-and-tracking-with-projects/managing-items-in-your-project/adding-items-to-your-project and https://docs.github.com/en/issues/planning-and-tracking-with-projects/learning-about-projects/about-projects
5. **Linear requires title and status at creation; the rest is optional.** Issues are "required to have a title and a status — all other properties and relations are optional"; status is a required create field, not deferred policy. Property edits within the first 3 minutes are treated as part of creation and skip the activity log. https://linear.app/docs/creating-issues
6. **Capture and triage are separate stages.** Linear Triage is a distinct inbox; optional policy can require priority before an issue *leaves* triage, never before it is created. https://linear.app/docs/triage
7. **Minimal insert returns a durable id immediately.** Slack's `slackLists.items.create` requires the auth `token` plus the destination `list_id`; `initial_fields` is optional, and success returns the new `item.id` (Slack RPC `{ok: true, item}` rather than an HTTP 201; Lists are paid-plan only). https://docs.slack.dev/reference/methods/slackLists.items.create/
8. **Retry dedup is windowed and keyed on a body hash or an explicit id.** SQS FIFO suppresses duplicate sends within a 5-minute window via a SHA-256 hash of the *body only* (content-based dedup) or an explicit `MessageDeduplicationId`, and orders strictly per `MessageGroupId`, not globally. https://docs.aws.amazon.com/AWSSimpleQueueService/latest/SQSDeveloperGuide/FIFO-queues-exactly-once-processing.html and https://docs.aws.amazon.com/AWSSimpleQueueService/latest/SQSDeveloperGuide/using-messagegroupid-property.html Azure Service Bus duplicate detection keys on the application `MessageId` alone — or `MessageId`+`PartitionKey` when partitioning is enabled; a duplicate send *succeeds* but the copy is dropped, and the window defaults to 10 minutes (20 seconds–7 days). https://learn.microsoft.com/en-us/azure/service-bus-messaging/duplicate-detection

**Observations:** Across these sources, destination identity is carried in the address or a routing argument rather than scattered through the payload, and capture is kept cheap — but not uniformly down to one operator text field: GitHub REST requires `title`; Linear requires title *and* status at creation; Slack Lists require `list_id` (plus the auth token) and are paid-plan only. None of the cited pages requires labels, priority, or similar classification at create, except Linear's required status; dedup hints, enrichment, and further classification are treated as post-capture concerns rather than gates on initial persistence.

## 4. Alternatives and decision matrix

**A — Description-only seam with deterministic internal defaults.** External interface takes project identity from the route plus one required `description`; the server fills Prompt from the description and derives Title/Kind/Priority/RiskLevel internally before `WorkRequest.Enqueue`.

**B — Inbox item, then asynchronous classification before queueing.** A new `InboxItem` aggregate is promoted to a `WorkRequest` by a classifier. Rejected: the description would be durable but not a queued `WorkRequest`, contradicting SPEC §3 "submit and queue development requests" and the current enqueue-is-`Queued` contract; classifying Kind after claim would break the immutable `ExecutionAssignment` snapshot that copies `RequestPrompt`, `Kind`, and `RiskLevel` (`src/PiCommandCenter.Application/Requests/ExecutionAssignmentDto.cs`).

**C — Conversational append-only thread as the request.** The existing message thread becomes the intake. Rejected: agent mail is coordination after assignment (SPEC §3.3), not intake; appending after claim would conflict with the immutable assignment prompt snapshot, and it creates two request identities (thread id vs `WorkRequestId`).

| Axis | A (description-only) | B (inbox + classifier) | C (thread-as-request) |
|---|---|---|---|
| Operator-visible inputs | 1 (+ route project) | 1 + wait for promote | 1 + implicit "ready" act |
| Durable enqueue on send | Yes, immediately | No — not a WorkRequest yet | Ambiguous |
| Claim-snapshot immutability | Preserved | Violated if Kind/Prompt mutate post-claim | Violated by appends |
| One nonterminal development writer | Preserved (default Development) | Classifier can surprise the writer slot | Unclear until parse |
| Module depth | Deepens existing seam | New shallow aggregate | Conflates mail with queue |

**Decision:** Ship A. Deepen the existing enqueue module: narrow its interface, keep its internals. No new aggregates, no admission-control classifier.

## 5. Recommended interface and defaults

**Decision — external seam:**

```
POST /api/projects/{projectId}/requests
Headers: Idempotency-Key: <client key>     // optional; dedups retries (§7)
Body: { "description": string }            // required, non-empty after trim
201 → WorkRequestDto (the durable request, Status = Queued)
400 → description empty/whitespace
404 → project missing
409 → Idempotency-Key reused with a different description (§7)
```

- Project identity comes from the route/context only (Pattern 1, 7); the project is never a body field.
- `description` maps to the stored `Prompt`. A successful enqueue returns the durable request itself (id, status, scheduling reason), so the operator sees it is queued even while the project is unbound or its node is offline (architecture.md data-flow step 3).

**Decision — deterministic internal defaults for the initial cutover:**

| Stored field | Internal value | Note |
|---|---|---|
| Prompt | The operator's `description`, trimmed (leading/trailing whitespace removed) | Matches `WorkRequest.Normalize`: stored value is the trimmed description, not the verbatim bytes |
| Title | Display projection derived from description (see §6) | Not a second required input |
| Kind | `Development` | Deliberate tradeoff below |
| Priority | `Normal` | Matches current UI default |
| RiskLevel | `Standard` | Matches current UI default |

**Decision (stated deliberately):** defaulting `Kind=Development` for every request preserves writer safety — Development occupies the single nonterminal development writer slot (SPEC §5.1), so the default can never silently grant read-only concurrency to work that mutates files. The cost is that operators lose the ability to enqueue new `Analysis`/`Review` requests through this seam, and the concurrency those read-only kinds allow. That is an accepted initial-cutover tradeoff: wrong-direction safety (treating analysis as development) costs writer-slot utilization; wrong-direction danger (treating development as read-only) would violate the one-writer invariant. If read-only intake returns later, it must be an explicit operator act, not a guess.

**Observation:** `Kind=Development`, `Priority=Normal`, and `RiskLevel=Standard` are enqueue-time constants, not inference. The durable queue path does not require an AI classification step. An optional local CPU model may enrich the already-persisted request afterward (§9).

**Decision (no migration for existing fields):** Every `WorkRequest` column — Kind, Priority, RiskLevel, Title, Prompt — stays exactly as today, and `WorkRequest.Enqueue`/`Rehydrate` signatures are unchanged. Existing queued rows already carry all five stored fields, so merely retaining the current fields requires no schema migration and no data backfill: old rows read back identically; only the *source* of the values changes (operator form → deterministic defaults plus the §6 projection). The *optional* retry-dedup guarantee of §7 is the one addition that does require storage: persisting the `Idempotency-Key` (either a new nullable column on `WorkRequest` or a separate enqueue-key table) plus a unique index on `(ProjectId, Key)` needs one small additive migration, named in §11. It affects no existing rows and no existing column.

**Observation (SPEC discrepancy, documented not resolved):** SPEC §12.1 states enqueue requires "an enabled existing Project," while SPEC §12.2 defines a `project_policy_disabled` waiting reason, which implies a disabled project can hold queued work that is merely ineligible. The current intake code observed in this research validates project *existence* only (`ProjectNotFoundException`); no enabled-state rejection was observed in the intake surface. This note does not invent a rejection rule: the description-only seam inherits whatever the current existence check does, and the §12.1/§12.2 wording gap is left for a spec fix.

**Observation (risk is reclassified at planning):** SPEC §13.1 requires the root Pi orchestrator to classify the work as small, standard, or high risk during planning. Defaulting intake `RiskLevel=Standard` is therefore an initial stored value, not a claim that all operator work is standard-risk; the authoritative risk classification still happens in the root's planning step.

## 6. Title derivation rule

**Decision:** Title is a display projection derived from the description, never a second required input. The rule is fully deterministic:

1. Take the **first meaningful line** of the trimmed description — the first line that is non-empty after trimming.
2. **Normalize whitespace**: collapse every run of interior whitespace (spaces, tabs, newlines) to a single space, and trim the ends.
3. **Truncate to a fixed bound of 80 Unicode scalar values** (never splitting a scalar). If the normalized line fits within 80 scalars, it is the title. Otherwise, cut at the **last word boundary** (last space) at or before scalar 80; if no space exists within the first 80 scalars, **hard-cut at exactly 80 scalars**.

The result is **guaranteed non-empty**: a description that passes the non-empty-after-trim check always yields a non-empty first meaningful line, and the cut never lands before the first scalar. The domain's existing non-empty-Title invariant (`WorkRequest.Normalize`) is therefore satisfied by construction. **Observation:** This mirrors Linear requiring a title while letting operators type one field — the projection supplies what the store demands without a second operator input.

## 7. Idempotency and retry semantics

**Facts:** SQS FIFO dedups retries by explicit `MessageDeduplicationId` or body hash within a 5-minute window; Service Bus dedups by application `MessageId` within a 10-minute default window and reports success while dropping the copy (sources 8).

**Decision:** The seam accepts an optional client-supplied idempotency key carried only in the `Idempotency-Key` HTTP header — never in the body, which stays exactly `{ "description" }`. When a key is present, it is **persisted** with the request (a nullable `IdempotencyKey` column on `WorkRequest`, or a separate durable enqueue-key record table) and guarded by a **unique constraint on `(ProjectId, Key)`**; dedup is enforced by that constraint and a lookup over stored keys, not over any unstored/transient key. Semantics:

- **Same key + same description** → the enqueue is a resubmission: return the original durable request (200/201 with the existing row); no second insert.
- **Same key + different description** → **409 Conflict**: the key names a different request; the caller must mint a new key.
- **No key** → the server mints a fresh request id and the call is not deduplicated.

The unique constraint also settles concurrent retries: two racing inserts with the same key resolve to one winner and one conflict-then-return-original.

**Observation (limits):** A fresh server-minted id cannot deduplicate lost-ack retries — if the operator retries after the 201 response was lost and no client key was supplied, a second row is created. Content-hash dedup is explicitly rejected: hashing the description would collapse two genuinely identical descriptions that are distinct work (the SQS body-hash caveat applies). The request-level idempotency key is distinct from the existing event/claim idempotency mechanisms (claim transaction, assignment token, `commandId` handles on session/turn requests); those remain untouched.

**Fact (codebase):** `POST /api/requests/{requestId}/retry` in SPEC §30.2 creates a *new linked Work Request* and never reuses or mutates the original request's ExecutionAssignment. **Observation:** This business-level retry is a separate concept from the transport-level resend the idempotency key governs — the key dedups accidental resubmission of one enqueue call; `/retry` deliberately creates another request.

## 8. Queue ordering

**Decision:** Unchanged. `Priority` descending, then `CreatedAt` ascending, per `RequestQueue.ListAsync` and SPEC §12.1 ("queue ordering is priority first, then creation time").


## 9. Optional local CPU model enrichment

**Fact (oh-my-pi):** OMP's `@smol` / `modelRoles.smol` is a configurable model-role alias, not inherently a local model. OMP can discover keyless local Ollama, llama.cpp, and LM Studio endpoints; its settings also expose `PI_TINY_DEVICE` for local tiny-model execution. The same provider shape can support DevFleet metadata enrichment without sending request text to a hosted provider (oh-my-pi model and settings sources above).

**Decision:** DevFleet may run a small CPU model after the request transaction commits. Enqueue still returns the durable `Queued` request immediately; inference availability, latency, malformed output, or model startup never changes that result.

The local model may produce a separate `RequestEnrichment` projection containing:

- a concise suggested display title;
- suggested kind, risk, and labels;
- the model selector/version and completion timestamp.

The deterministic title and conservative `Development` / `Normal` / `Standard` values from §§5–6 remain authoritative fallbacks. Enrichment is advisory: it never rejects or delays enqueue, changes queue priority, grants read-only concurrency, or mutates an `ExecutionAssignment`. If enrichment finishes before claim, a future explicit policy may accept selected suggestions into request metadata; after claim, execution-relevant fields are frozen.

**Implementation constraint:** run the model through a loopback-only local adapter configured on the Control Plane host (for example Ollama or llama.cpp), validate its response against a bounded structured schema, and store suggestions separately from `WorkRequest`. Do not turn the enrichment worker into a second queue or expose the local inference endpoint to the network.

Duplicate/similarity hints remain post-persist triage information, never an enqueue gate (Pattern 2).

## 10. Failure and edge cases

| Case | Behavior |
|---|---|
| Empty/whitespace description | 400 (`ArgumentException` from `Normalize`, as today) |
| Project does not exist | 404 (`ProjectNotFoundException`, as today) |
| Project unbound / node offline | 201, request stays `Queued` with computed scheduling reason (architecture data-flow 3) |
| Lost ack, retry with same idempotency key + same description | Original durable request returned; no duplicate row (§7) |
| Lost ack, retry without key | Second row created (accepted limitation, §7) |
| Same key, different description | 409 Conflict; caller must mint a new key (§7) |
| Identical description text twice (no key) | Two requests; content equality is not duplication (§7) |
| Single-line long description | Title = normalized truncated prefix (§6) |
| Description with leading blank lines | First meaningful line wins (§6) |
| Description with surrounding whitespace | Prompt stored trimmed, matching `WorkRequest.Normalize` (§5) |
| Claim later | Atomic; immutable `ExecutionAssignment` snapshot of Prompt/Kind/RiskLevel; unchanged |
| Development writer slot occupied | Request remains `Queued` with capacity reason; unchanged (SPEC §5.1) |

## 11. Incremental implementation plan (by affected paths)

1. **Docs** — Record the defaults and the description-only contract in `SPEC.md` / `docs/architecture.md` (enqueue success criterion remains description + existing project, unbound OK).
2. **`src/PiCommandCenter.Application/Requests/QueueWorkRequestCommand.cs`** — Shrink the public intake shape to the description alone; the idempotency key arrives via the `Idempotency-Key` header, not the command body. Keep the filled internal fields flowing to the domain.
3. **`src/PiCommandCenter.Infrastructure/Requests/RequestQueue.cs`** — In `EnqueueAsync`, apply the deterministic defaults and the §6 title projection before `WorkRequest.Enqueue`; look up a stored `(ProjectId, IdempotencyKey)` match before insert and rely on the unique constraint against races.
4. **Persistence migration** — Add one additive migration: nullable `IdempotencyKey` on `WorkRequest` (or a new enqueue-key record table) plus a unique index on `(ProjectId, IdempotencyKey)` filtered to non-null keys. No existing column or row changes (§5).
5. **`src/PiCommandCenter.Api/RequestsEndpoints.cs`** — Bind the new body under the same project route; 201 with the durable DTO. Clean cutover: the old five-field body is removed, not dual-bound or shimmed.
6. **`src/PiCommandCenter.Web/Components/Pages/Project.razor`** — Replace the composer with a single description textarea; delete the Kind/Priority/Risk selects and the required Title field.
7. **Optional local enrichment** — Add a bounded background worker and loopback-only local-model adapter (Ollama or llama.cpp) that writes a separate `RequestEnrichment` projection. Failure is recorded as unavailable and does not change the `WorkRequest`.
8. **Unchanged:** `WorkRequest` aggregate (all existing columns kept), `ExecutionAssignment`, eligibility evaluator, claim path, root orchestration, reservations, supervisor-owned Git, cancel/list/get, and human guidance on assigned requests. The queue list continues to show authoritative Kind/Priority/Risk values and may show advisory enrichment separately.

## 12. Acceptance criteria

- The operator can type one description on a project page (or POST `{ "description" }` to the project route) and immediately receive the durable, `Queued` request — with no binding or node required.
- The composer exposes no Title, Kind, Priority, or RiskLevel inputs.
- Stored rows carry Prompt = trimmed description (matching `WorkRequest.Normalize`), derived Title, Kind = `Development`, Priority = `Normal`, RiskLevel = `Standard`.
- Empty description → 400; unknown project → 404; unbound project → queued with a scheduling reason.
- A retry with the same idempotency key and same description returns the original request without a second row; the same key with a different description returns 409.
- With local enrichment disabled, unavailable, slow, or malformed, enqueue behavior and deterministic defaults are identical. When enabled, the CPU model runs only after persistence and its bounded suggestions remain advisory.
- Claim still produces one immutable `ExecutionAssignment` per request; one nonterminal development assignment per project; mandatory root Pi orchestration; reservations and supervisor-owned Git intact.

## 13. Rejected designs

- **B, inbox-then-classify:** durable-but-not-queued violates enqueue-is-`Queued`; post-claim Kind mutation breaks the assignment snapshot.
- **C, thread-as-request:** conflates coordination mail with intake; appends conflict with the immutable prompt snapshot; dual request identities.
- **Synchronous or required AI classification:** rejected. Local CPU enrichment is allowed only after persistence; it remains non-blocking and advisory (§9).
- **Content-hash dedup of descriptions:** collapses distinct identical requests (§7).
- **Dual-bind/versioned compatibility for the old five-field body:** the cutover is clean; the old contract is removed, not shimmed.
- **Compatibility overload on `IRequestQueue.EnqueueAsync` alongside the narrowed seam:** a second intake convention beside the first is prohibited; the five-field command is migrated away, not preserved as an overload.
- **Enrichment that overwrites stored Kind/Priority/RiskLevel/Title after persist:** `WorkRequest` exposes no metadata update method today, and post-claim mutation would break the immutable `ExecutionAssignment` snapshot; enrichment is display-only until deliberate domain surface exists (§9).
- **Per-request operator priority in the simple seam:** deferred; queue order mechanics unchanged, policy-level defaults may come later.
