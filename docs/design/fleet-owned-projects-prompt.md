# Design prompt: fleet-owned projects, not node-owned projects

Produce a design proposal and an incremental implementation plan for decoupling project ownership from execution placement in DevFleet. This is a design task, not authorization to implement the feature. Read `AGENTS.md`, `README.md`, the relevant sections of `SPEC.md`, `docs/architecture.md`, `docs/protocols.md`, `docs/security.md`, and the current project registration, validation, persistence, request claiming, node transport, and UI code before proposing changes. Identify discrepancies between requirements and implementation explicitly. All source paths in this prompt are relative to the repository root.

## Goal and terminology

A project belongs to the fleet. A node is a possible execution location, not the project's owner. The eventual goal is for any eligible available node to claim a queued request; repository distribution and mobility are deliberately deferred.

Separate these concepts in the proposal:

- **Project:** stable fleet-owned identity, display name, policies, request queue, and history. Registration must not require a node, an online machine, a local checkout, or a repository URL; current repositories may exist only on one machine.
- **Workspace binding:** an explicit association between a project and a node-local repository path, provisioned manually and validated by that node. This records execution readiness, not project ownership.
- **Execution assignment:** the node and workspace authorized to run a particular request, selected atomically when work is claimed and retained in execution history.

## Required behavior and constraints

1. Projects can be registered, viewed, and accept queued requests with zero workspace bindings and zero connected nodes. Distinguish registration validation from node-local workspace validation.
2. No eligible node means the request stays queued with an actionable waiting reason, rather than failing or receiving a fabricated node identity. Distinguish missing workspace, invalid workspace, offline node, and unavailable runtime/capacity.
3. Eligibility must include a validated designated workspace, authenticated node identity, liveness, required runtime availability, capacity, and project execution policy. Do not equate connected or idle with eligible.
4. For the initial phase, retain one designated canonical workspace per project. Independent clones are not interchangeable just because they share a project id or path. Do not enable automatic cross-node reassignment or assume synchronization has already been solved. Explain how request-time assignment remains useful even when only one node is currently eligible.
5. Keep each request and its children on the assigned node and shared canonical workspace. Preserve the no-worktree rule, reservation enforcement, project-wide concurrency limits, and supervisor-owned Git boundary.
6. Define atomic claim/assignment semantics, duplicate-claim prevention, cancellation, node disconnect/reconnect, and restart recovery. Do not treat heartbeat expiry as permission to start a second writer; uncertain ownership must require safe reconciliation or recovery.
7. Preserve existing projects, queues, and history during migration: convert the current node/path association into a workspace binding without losing stable project identity. Audit every assumption that a project always has a node or repository path, including DTOs, database constraints, routing, authorization, and UI projections.

## Explicitly out of scope

SSH tunnels, Git hosting, automatic cloning, repository replication/synchronization, shared filesystems, uncommitted-change transfer, transparent failover, automatic workspace relocation, distributing one request's agents across machines, credential distribution, and multi-user tenancy. Do not silently relax the canonical-workspace policy to enable these. Do not introduce a GitHub/GitLab dependency or an unnecessary multi-fleet tenancy model.

## Deliverables and acceptance scenarios

Provide the proposed domain relationships and invariants; registration/binding/claim lifecycle; persistence migration; application and transport contract changes; UI states; security and recovery implications; trade-offs and open questions; and small implementation steps with affected paths and tests. Compare a minimal safe design with a future extension for multiple eligible workspaces, without implementing that future infrastructure. List the documentation changes needed to replace today's node-owned registration assumptions.

The test plan must cover registration and enqueue with no nodes; waiting without a binding; node-side workspace validation; execution once the designated workspace is ready; ineligible-node rejection; competing claims; disconnect without unsafe reassignment; retained assignment history; and migration of an existing node-bound project. Include a scenario where another idle node has an independent clone but must not claim the request in this phase.

Leave the checklist item in `TODO.md` open until the design proposal and implementation plan have been produced and reviewed; adding this prompt alone does not complete the design task.
