# Pi Orchestration Command Center
## Proof-of-Concept Implementation Specification

**Document version:** 0.1  
**Status:** Ready for implementation  
**Date:** 2026-09-04  
**Intended implementer:** OMP or another coding agent  
**Primary platform:** Fedora Linux workstation  
**Primary application stack:** .NET 10, C#, ASP.NET Core, Blazor, EF Core, SQLite, TypeScript, Node.js  

---

## 1. Executive Summary

Build a project-centric web command center for autonomous software-development work.

The system must allow a user to:

1. Register multiple local software projects.
2. Submit and queue development requests for any project from a web interface.
3. Start a root Pi orchestration session for each active request.
4. Make orchestration the mandatory default behavior for development work.
5. Let the Pi orchestrator delegate work to managed child agents running through:
   - Pi with local or API-backed models.
   - The official, unmodified Claude Code CLI for Anthropic subscription usage.
   - The official Antigravity CLI for Google subscription usage.
6. Display every root and child session in a live agent tree.
7. Show whether each request and session is active, idle, blocked, completed, failed, cancelled, or disconnected.
8. Let multiple agents work concurrently in one shared project repository without Git worktrees.
9. Prevent overlapping edits by requiring strict, path-based reservation leases before any agent can modify a file.
10. Provide a mail-like coordination system for agent-to-agent messages, acknowledgements, reservation conflicts, and reservation handoffs.
11. Preserve request, session, event, message, reservation, verification, and result history across browser and control-plane restarts.

The proof of concept is single-user and single-node, but its contracts must not prevent later multi-node or multi-user deployment.

---

## 2. Product Vision

The frontend is the authoritative command center for development across many projects.

```text
Web Command Center
        │
        ├── Project A queue
        ├── Project B queue
        ├── Project C queue
        └── Attention inbox
                 │
                 ▼
        Project Orchestration Node
                 │
                 ▼
        Root Pi Orchestrator
                 │
        ┌────────┼──────────┐
        ▼        ▼          ▼
     Pi child  Claude Code  Antigravity
     agent     child agent  child agent
```

The root Pi session coordinates work. It does not directly implement changes. It plans, delegates, monitors, resolves coordination issues, requests verification, and submits completion evidence.

Provider-specific official harnesses are treated as complete agent runtimes, not as raw model providers behind Pi.

---

## 3. Non-Negotiable Product Decisions

The implementation must preserve all decisions in this section.

### 3.1 No Git worktrees

The system must not create or use Git worktrees for task or agent isolation.

All agents assigned to a project operate against one canonical shared project workspace.

### 3.2 Strict file reservations

Write-capable agents may operate concurrently only when their reserved file scopes do not conflict.

Reservations are mandatory, server-enforced leases. They are not merely prompt instructions or advisory records.

### 3.3 Mail-like coordination

Agents must have identities, inboxes, request threads, acknowledgements, and direct messages. Reservation conflicts and handoffs must be represented through this coordination system.

### 3.4 Orchestration is the default

Every development request starts a root Pi orchestration session. The root session must delegate implementation and independent verification.

The root Pi session must not directly modify project files or run arbitrary development shell commands.

### 3.5 Official provider harnesses remain unmodified

Subscription-backed Anthropic work must launch the official, unmodified Claude Code binary using credentials managed by Claude Code.

Subscription-backed Google work must launch the official Antigravity CLI using credentials managed by Antigravity.

The Command Center must never read, copy, store, relay, or transform provider subscription credentials.

### 3.6 One canonical project workspace

A project has one registered repository path on the node. All managed agents for that project use that path.

The system may create ordinary Git branches and request-level commits, but it must not create additional working trees.

### 3.7 Git state is supervisor-owned

Managed agents must not independently run commands that change repository-wide Git state, including:

- `git add`
- `git commit`
- `git reset`
- `git checkout`
- `git switch`
- `git stash`
- `git clean`
- `git merge`
- `git rebase`

The node supervisor owns branch setup, staging, commits, recovery, and final request checkpoints.

---

## 4. PoC Goals

The PoC must prove the following end-to-end flow:

```text
1. User opens web interface.
2. User selects a registered project.
3. User queues a development request.
4. Project runner claims the request.
5. Root Pi orchestrator starts.
6. Root Pi creates a plan.
7. Root Pi delegates work to child agents.
8. Child agents reserve disjoint files.
9. Child agents work concurrently in the shared repository.
10. A conflicting reservation is denied and displayed as blocked.
11. Agents coordinate through messages and reservation handoff.
12. Reviewer and verification stages run.
13. The request passes a completion gate.
14. The frontend displays the final summary, changed files, tests, findings, and agent history.
```

---

## 5. PoC Success Criteria

The PoC is successful only when all criteria below are demonstrated.

### 5.1 Project and queue

- At least two local Git projects can be registered.
- A request can be created from the browser for either project.
- Requests are persisted and shown in project order.
- Only one write request per project is active in the PoC.
- Read-only requests may run concurrently if configured.

### 5.2 Root orchestration

- Each active development request starts a root Pi session.
- The root uses Pi through its SDK, not terminal scraping.
- The root has read/search and orchestration tools only.
- Direct root file writes are technically blocked.
- The root delegates implementation to at least one child.
- The root delegates independent review or verification to a different child session.

### 5.3 Runtime adapters

- A Pi child can be launched and monitored.
- An official Claude Code child can be launched and monitored.
- An official Antigravity child can be launched and monitored in read-only reviewer mode.
- Runtime-specific events are converted into one normalized event contract.
- Provider authentication remains managed by the official CLI.

### 5.4 Status and hierarchy

- The live frontend shows root and child relationships.
- Sessions move correctly among starting, active, idle, blocked, completed, failed, cancelled, and disconnected.
- The displayed status includes a human-readable reason.
- Browser refresh does not lose current state.

### 5.5 Mail and reservations

- Agents have unique identities within a project.
- Agents can send, receive, reply to, and acknowledge messages.
- An agent can atomically reserve one or more files.
- Two agents can hold non-overlapping reservations concurrently.
- A conflicting reservation is denied before the second agent writes.
- A blocked agent can request a reservation handoff.
- Ownership can be transferred atomically.
- A stale fencing token is rejected.
- Expired reservations pass through recovery inspection before reuse.

### 5.6 Completion

- The request cannot become completed until mandatory stages pass.
- Final changed files are known.
- Verification results are persisted.
- Blocking review findings are resolved or explicitly overridden by the user.
- Active reservations are released.
- A request-level result summary is displayed.

### 5.7 Durability

- Control-plane restart does not lose projects, requests, sessions, messages, reservations, or historical events.
- The node reconnects after a control-plane restart.
- Events created while disconnected are replayed without duplication.

---

## 6. PoC Scope

### 6.1 Included

- One user.
- One Fedora workstation node.
- Multiple registered local projects.
- ASP.NET Core control plane.
- Blazor web interface.
- SQLite persistence.
- SignalR live browser updates.
- Separate .NET node worker process.
- TypeScript Pi worker process.
- Pi root and Pi child sessions.
- Claude Code adapter.
- Antigravity adapter in read-only mode.
- Internal mail-like coordination service.
- Strict reservation service.
- Shared canonical project repository.
- Request-level branch and commit support controlled by the supervisor.
- Basic verification command profiles.
- Cancellation and human guidance.

### 6.2 Explicitly deferred

- Git worktrees.
- Multiple users.
- Multi-tenant isolation.
- Public cloud hosting.
- Multiple execution nodes.
- PostgreSQL.
- Discord integration.
- Beads integration.
- MCP Agent Mail as the authoritative reservation store.
- CASS or Cass Memory integration.
- Subscription quota dashboards.
- Automatic pull-request creation.
- Automatic merge into the default branch.
- Arbitrary nested provider-native subagents.
- Unrestricted shell access for managed agents.
- Full container or micro-VM sandboxing.
- Mobile native applications.
- Production-grade high availability.

---

## 7. Terminology

| Term | Meaning |
|---|---|
| Control Plane | ASP.NET Core service that stores authoritative application state and serves the web UI/API. |
| Node | .NET worker service installed on an execution machine. It owns project runners and agent processes. |
| Project | A registered source repository and its policies. |
| Project Runner | Logical node component that owns one project's queue, workspace state, and execution policy. |
| Work Request | User-submitted unit of development work. |
| Root Session | Pi session that plans and orchestrates one request. |
| Child Session | Delegated agent session running through Pi, Claude Code, Antigravity, or another runtime. |
| Runtime Adapter | Component that starts, observes, controls, and normalizes one agent harness. |
| Agent Identity | Project-scoped human-readable identity used for messages and ownership. |
| Reservation | Time-limited exclusive lease over a file, directory prefix, or shared resource. |
| Fencing Token | Monotonically increasing lease epoch required for mutation. |
| Request Thread | Mail-like discussion thread associated with a work request. |
| Completion Gate | Supervisor validation that determines whether a request may become completed. |
| Canonical Workspace | The one shared repository path registered for a project. |

---

## 8. High-Level Architecture

```text
┌────────────────────────────────────────────────────────────┐
│                    Blazor Web Interface                    │
│                                                            │
│ Fleet dashboard  Project queue  Agent tree                 │
│ Request detail   Messages       Reservations               │
│ Attention inbox  Results        Verification               │
└───────────────────────────┬────────────────────────────────┘
                            │ HTTPS / SignalR
                            ▼
┌────────────────────────────────────────────────────────────┐
│                   ASP.NET Control Plane                    │
│                                                            │
│ Project registry          Request scheduler                │
│ Session registry          Event reducer                    │
│ Message service           Reservation authority            │
│ Runtime profiles          Completion gates                 │
│ EF Core / SQLite          SignalR publisher                │
└───────────────────────────┬────────────────────────────────┘
                            │ Authenticated node stream
                            ▼
┌────────────────────────────────────────────────────────────┐
│                    .NET Node Service                       │
│                                                            │
│ Project runners           Runtime adapters                 │
│ Process supervision       Repository manager               │
│ Local event spool         Hook gateway                     │
│ Verification runner       Health and heartbeat             │
└────────────┬────────────────────┬───────────────────────────┘
             │                    │
             │ NDJSON stdio       │ Process stdio
             ▼                    ▼
┌───────────────────────┐   ┌───────────────────────────────┐
│ Pi Worker Process     │   │ Provider Harness Processes    │
│                       │   │                               │
│ Root orchestrator     │   │ Claude Code                  │
│ Pi child              │   │ Antigravity                  │
│ Reservation tools     │   │ Future adapters              │
└───────────┬───────────┘   └──────────────┬────────────────┘
            │                              │
            └──────────────┬───────────────┘
                           ▼
                 Canonical Project Workspace
                 + strict reservation gateway
```

### 8.1 Process boundaries

The PoC uses these operating-system processes:

1. `CommandCenter.ControlPlane`
   - ASP.NET Core API and Blazor host.
   - EF Core and SQLite.
   - SignalR hub.

2. `CommandCenter.Node`
   - .NET Worker Service.
   - Runs as a systemd user service.
   - Launches and supervises agent processes.

3. `command-center-pi-worker`
   - TypeScript/Node.js process.
   - Embeds Pi through `@earendil-works/pi-coding-agent`.
   - Runs in root-orchestrator or child-worker mode.

4. Provider-specific child processes
   - `claude`
   - `agy`

The browser never launches or communicates directly with agent processes.

---

## 9. Authoritative Ownership Boundaries

| Concern | Authoritative component |
|---|---|
| Projects and requests | Control Plane |
| Current request state | Control Plane reducer |
| Session process lifecycle | Node |
| Root Pi context and messages | Pi worker |
| Provider-specific conversation | Provider harness |
| Agent hierarchy | Control Plane, based on Node events |
| Mail messages | Control Plane coordination service |
| File reservations | Control Plane reservation authority |
| Write enforcement | Node and runtime-specific hooks/tools |
| Git branch/index/commit | Node repository manager |
| Browser presentation | Blazor UI |
| Provider credentials | Official provider harness or Pi model runtime |

No component may silently replace another component's authority.

---

## 10. Project Model

A project registration contains:

```json
{
  "id": "agent-command-center",
  "displayName": "Agent Command Center",
  "nodeId": "fedora-workstation",
  "repositoryPath": "/home/user/Developer/agent-command-center",
  "defaultBranch": "main",
  "enabled": true,
  "maxActiveWriteRequests": 1,
  "maxReadOnlyRequests": 2,
  "maxChildAgentsPerRequest": 4,
  "requireCleanStart": true,
  "createRequestBranch": true,
  "createRequestCommit": true,
  "autoMerge": false
}
```

### 10.1 Project validation

When a project is registered, the node must verify:

- Path exists.
- Path is a directory.
- Path contains a Git repository.
- Resolved path is not outside approved project roots.
- Node process can read the repository.
- Git executable is available.
- Configured default branch exists locally or remotely.
- The repository is not itself a Git worktree created by this application.

### 10.2 Canonical workspace rule

All sessions for one project use exactly the registered repository path.

The application must not duplicate the workspace per agent.

---

## 11. Work Request Model

A work request is the primary user-facing work unit.

```json
{
  "id": "DEV-00042",
  "projectId": "agent-command-center",
  "title": "Add strict reservation handoff",
  "prompt": "Implement atomic reservation handoff with stale-token rejection.",
  "priority": "normal",
  "kind": "development",
  "riskLevel": "standard",
  "baseBranch": "main",
  "continuationOfRequestId": null,
  "status": "queued",
  "createdAt": "2026-09-04T19:30:00Z"
}
```

### 11.1 Request kinds

- `development`: May change source files. Requires implementer and independent verification.
- `analysis`: Read-only. Requires at least one delegated specialist but no file reservations.
- `review`: Read-only review of current repository state or diff.

The PoC UI defaults to `development`.

### 11.2 Request lifecycle

```text
Queued
  ↓
Starting
  ↓
Planning
  ↓
Executing
  ↓
Reviewing
  ↓
Verifying
  ↓
Completed
```

Alternative states:

```text
Blocked
Failed
Cancelled
```

A blocked request retains its last active phase separately so it can resume correctly.

---

## 12. Project Queue and Scheduling

### 12.1 PoC scheduling rules

- A project may have only one active `development` request.
- A project may run up to the configured number of read-only requests.
- Different projects may run concurrently.
- Child-agent concurrency is limited by project and runtime profile.
- Queue ordering is priority first, then creation time.
- A blocked development request continues to own the active write-request slot unless explicitly suspended or cancelled.

### 12.2 Request claiming

The node claims a queued request through an atomic control-plane operation.

A claim contains:

```json
{
  "requestId": "DEV-00042",
  "nodeId": "fedora-workstation",
  "claimToken": "opaque-token",
  "claimedAt": "2026-09-04T19:31:00Z",
  "leaseExpiresAt": "2026-09-04T19:32:00Z"
}
```

The node renews the claim while the project runner is healthy.

---

## 13. Default Orchestration Behavior

### 13.1 Root Pi responsibilities

The root Pi orchestrator must:

1. Read the request and project policy.
2. Inspect only enough repository context to plan correctly.
3. Classify the work as small, standard, or high risk.
4. Produce a structured execution plan.
5. Assign logical roles.
6. Propose initial reservation scopes for write-capable roles.
7. Spawn child sessions through supervisor tools.
8. Monitor child progress and messages.
9. Resolve reservation conflicts through task adjustment, messaging, waiting, or handoff.
10. Request independent review.
11. Request controlled verification.
12. Address blocking findings through the responsible implementer.
13. Submit completion evidence.

### 13.2 Root Pi prohibited actions

The root Pi process must not have access to:

- Built-in Pi `edit`.
- Built-in Pi `write`.
- Built-in Pi `bash`.
- Built-in Pi `powershell`.
- Direct Git mutation tools.
- Provider credential files.
- Reservation administration overrides.

It may receive Pi built-ins:

```text
read
grep
find
ls
```

It receives custom orchestration tools described below.

### 13.3 Root tool surface

```text
create_plan
revise_plan
spawn_agent
spawn_agents
get_agent_status
await_agent
send_agent_message
read_agent_inbox
acknowledge_message
request_reservation_handoff
cancel_agent
inspect_project_diff
request_verification
submit_completion
block_request
```

### 13.4 Mandatory minimum pipeline

Even a small source change requires:

```text
Root Pi
├── Implementer
└── Independent reviewer or verifier
```

A standard change should use:

```text
Root Pi
├── Architect or scout
├── Implementer
├── Reviewer
└── Verification stage
```

A high-risk change should add a specialist reviewer, such as security or migration review.

### 13.5 Completion cannot be self-declared

A model statement such as “done” is not completion.

The root must call `submit_completion`. The supervisor accepts or rejects the request based on objective evidence.

---

## 14. Structured Orchestration Plan

The root submits a plan in this form:

```json
{
  "summary": "Implement strict reservation handoff and stale-token rejection.",
  "riskLevel": "standard",
  "tasks": [
    {
      "taskKey": "domain-model",
      "role": "implementer",
      "description": "Add domain records and state transitions.",
      "dependencies": [],
      "requestedWriteScopes": [
        { "kind": "directory", "path": "src/CommandCenter.Domain/Reservations" },
        { "kind": "directory", "path": "tests/CommandCenter.Domain.Tests/Reservations" }
      ]
    },
    {
      "taskKey": "gateway",
      "role": "implementer",
      "description": "Implement atomic handoff in the reservation service.",
      "dependencies": ["domain-model"],
      "requestedWriteScopes": [
        { "kind": "file", "path": "src/CommandCenter.ControlPlane/Reservations/ReservationService.cs" },
        { "kind": "file", "path": "tests/CommandCenter.ControlPlane.Tests/ReservationServiceTests.cs" }
      ]
    },
    {
      "taskKey": "review",
      "role": "reviewer",
      "description": "Review concurrency and stale-token behavior.",
      "dependencies": ["domain-model", "gateway"],
      "requestedWriteScopes": []
    }
  ],
  "verificationProfile": "default"
}
```

The supervisor validates:

- Role exists.
- Dependencies form an acyclic graph.
- Requested paths are repository-relative.
- Parallel write scopes do not conflict.
- Maximum child count is not exceeded.
- Risk policy stages are present.

The root may revise a rejected plan.

---

## 15. Role and Runtime Routing

Pi requests logical roles. The supervisor resolves runtime profiles.

Example configuration:

```yaml
roles:
  architect:
    runtimeOrder:
      - claude-personal
      - google-personal
      - local-pi
    permissionProfile: read-only

  implementer:
    runtimeOrder:
      - local-pi
      - claude-personal
    permissionProfile: reserved-write

  reviewer:
    runtimeOrder:
      - google-personal
      - claude-personal
      - local-pi
    permissionProfile: read-only

  security-reviewer:
    runtimeOrder:
      - claude-personal
      - google-personal
    permissionProfile: read-only

  tester:
    runtimeOrder:
      - local-pi
    permissionProfile: verification
```

### 15.1 Runtime profiles

```yaml
runtimeProfiles:
  local-pi:
    adapter: pi
    modelProfile: local-qwen
    maxConcurrency: 4

  claude-personal:
    adapter: claude-code
    executable: claude
    authentication: provider-managed
    maxConcurrency: 2

  google-personal:
    adapter: antigravity
    executable: agy
    authentication: provider-managed
    maxConcurrency: 2
```

Agent-generated content must never be allowed to select an arbitrary executable, credential path, Unix user, or unregistered runtime profile.

---

## 16. Agent Identity and Mail-Like Coordination

### 16.1 Agent identities

Every session receives:

- Internal session ID.
- Project-scoped agent identity.
- Display name.
- Role.
- Runtime.
- Parent session ID.

Example:

```json
{
  "sessionId": "01K...",
  "agentName": "GreenCastle",
  "role": "implementer",
  "runtime": "pi",
  "parentSessionId": "01J..."
}
```

Agent names must be unique among active sessions in one project and should be easy for humans and agents to reference.

### 16.2 Request thread

Each work request automatically receives one primary message thread.

```text
Thread ID: DEV-00042
Subject: [DEV-00042] Add strict reservation handoff
```

### 16.3 Required message operations

- Send direct message.
- Send to multiple recipients.
- Reply in thread.
- Fetch unread inbox.
- Fetch thread.
- Mark read.
- Acknowledge when requested.
- Send high-priority human guidance.

### 16.4 Message schema

```json
{
  "id": "msg-01K...",
  "projectId": "agent-command-center",
  "requestId": "DEV-00042",
  "threadId": "DEV-00042",
  "senderSessionId": "session-a",
  "recipientSessionIds": ["session-b"],
  "subject": "Reservation handoff requested",
  "bodyMarkdown": "I need src/.../DependencyInjection.cs.",
  "importance": "high",
  "acknowledgementRequired": true,
  "createdAt": "2026-09-04T20:00:00Z"
}
```

### 16.5 Human guidance

The browser must allow the user to send a high-priority message to:

- Root session.
- A specific child.
- All active agents in a request.

Human guidance is recorded in the same thread and clearly marked as human-originated.

---

## 17. Strict Reservation System

The reservation system is a central PoC feature.

### 17.1 Reservation authority

The Control Plane is the authoritative reservation store.

Runtime hooks and tools query the authority before allowing mutation.

An external coordination system may mirror reservation information, but it must not be the enforcement authority unless it provides equivalent strict atomic behavior.

### 17.2 Supported scope types for PoC

Avoid arbitrary glob-overlap analysis in the PoC. Support deterministic scopes:

1. Exact file
   - `src/CommandCenter.Domain/FileReservation.cs`

2. Directory prefix
   - `src/CommandCenter.Domain/Reservations/`
   - Represents the directory and all descendants.

3. Named shared resource
   - `project-build`
   - `project-format`
   - `project-git-index`

All paths are repository-relative POSIX paths.

### 17.3 Conflict rules

- File conflicts with the same file.
- File conflicts with any directory scope containing it.
- Directory conflicts with another directory when either prefix contains the other.
- Resource conflicts with the same resource name.
- Read access does not require a reservation.
- All source mutation reservations are exclusive in the PoC.

Examples:

| Existing | Requested | Conflict |
|---|---|---|
| `src/Foo.cs` | `src/Foo.cs` | Yes |
| `src/` | `src/Foo.cs` | Yes |
| `src/Foo/` | `src/Foo/Bar.cs` | Yes |
| `src/Foo/` | `src/Foobar/` | No |
| `tests/A.cs` | `src/A.cs` | No |
| resource `project-build` | resource `project-build` | Yes |

### 17.4 Atomic acquisition

A multi-scope request is all-or-nothing.

```json
{
  "sessionId": "session-a",
  "requestId": "DEV-00042",
  "scopes": [
    { "kind": "file", "path": "src/A.cs" },
    { "kind": "file", "path": "tests/ATests.cs" }
  ],
  "reason": "Implement domain model"
}
```

If any scope conflicts, no scope in the request is granted.

### 17.5 Lease group

A successful acquisition creates a lease group:

```json
{
  "leaseId": "lease-01K...",
  "sessionId": "session-a",
  "requestId": "DEV-00042",
  "fencingToken": 27,
  "state": "active",
  "acquiredAt": "2026-09-04T20:01:00Z",
  "expiresAt": "2026-09-04T20:03:00Z",
  "scopes": [
    { "kind": "file", "path": "src/A.cs" },
    { "kind": "file", "path": "tests/ATests.cs" }
  ]
}
```

### 17.6 TTL and renewal

PoC defaults:

```text
Lease duration: 120 seconds
Renewal interval: 30 seconds
Suspect after: 60 seconds without renewal
Expired after: lease deadline
```

The node renews leases only while the owning process is alive and the session remains valid.

### 17.7 Fencing tokens

Every grant or ownership transfer increments a project-scoped monotonic token.

Every mutation authorization request must include:

- Lease ID.
- Fencing token.
- Session ID.
- Target path.
- Operation.

A stale token must be rejected even if the former owner's process is still alive.

### 17.8 Reservation handoff

Handoff is atomic.

```text
Current owner requests or accepts handoff
          │
          ▼
Reservation authority validates both sessions
          │
          ▼
Old token invalidated
          │
          ▼
Ownership changed and new token issued
          │
          ▼
Both agents receive an event and mail message
```

The scope must never be simultaneously owned by both agents.

### 17.9 Expiration and recovery

A lease is not immediately reusable solely because a heartbeat stopped.

When a lease expires:

1. Mark it `recovery-required`.
2. Ask the node whether the process is alive.
3. Record repository status and affected-file metadata.
4. Reject new ownership while the old process may still write.
5. Release only when the node confirms the process is stopped or an administrator force-releases it.
6. Emit an audit event.

### 17.10 Force release

Force release is a human-only action in the PoC.

It requires:

- Explicit confirmation.
- Reason.
- Current repository status snapshot.
- Audit event.
- Fencing-token increment.

### 17.11 Path normalization

Before comparing or authorizing paths:

- Convert separators to `/`.
- Reject absolute paths.
- Reject `..` traversal.
- Resolve the target against the canonical repository path.
- Reject targets resolving outside the repository.
- Reject symlink traversal outside the repository.
- Preserve filesystem case sensitivity.
- Reject `.git/` targets for normal agent reservations.

### 17.12 Creation, rename, and delete

- New file: reserve the exact intended file path before creation.
- Rename: reserve both source and destination.
- Delete: reserve the exact target or containing directory.
- Directory-wide formatter/generator: reserve the target directory and `project-format` or equivalent resource.

---

## 18. Reservation Enforcement

Prompts alone are not enforcement.

### 18.1 Pi child enforcement

Do not give write-capable Pi children the default unrestricted `edit`, `write`, or `bash` tools.

Implement custom tools:

```text
reserved_read
reserved_write
reserved_edit
reserved_delete
reserved_move
reserve_files
expand_reservation
release_reservation
run_verification_command
```

Every mutation tool calls the node, which calls the reservation authority before touching the filesystem.

The tool request includes the lease ID and fencing token.

### 18.2 Claude Code enforcement

Claude Code must remain unmodified.

Use supervisor-managed settings and hooks outside agent-controlled repository content.

At minimum:

- `PreToolUse` hook for `Edit` and `Write`.
- Hook calls a local reservation validation endpoint or executable.
- Invalid operations exit with a blocking result.
- `PostToolUse` hook reports the completed mutation for audit.

For the PoC, do not grant unrestricted Bash to a write-capable Claude Code child. Project verification runs through typed supervisor operations.

### 18.3 Antigravity enforcement

The PoC Antigravity adapter is read-only reviewer mode. It must not receive write permissions.

Write-capable Antigravity support is deferred until its exact hook and permission behavior is contract-tested against the pinned version.

### 18.4 Shell escape prevention

A write-capable agent must not be able to bypass reservations through commands such as:

- `sed -i`
- `perl -pi`
- `python` file writes
- `dotnet format`
- code generators
- package installers
- Git checkout/reset

Therefore:

- Root Pi has no shell.
- Pi implementers have no unrestricted shell.
- Claude implementers have no unrestricted shell.
- Verification commands are configured and run by the node.
- Broad mutation tools require directory and resource reservations.

---

## 19. Shared Repository and Git Workflow

### 19.1 Request start

For a development request, the node must:

1. Confirm no other development request is active for the project.
2. Confirm repository state is permitted by project policy.
3. Record current branch and base commit.
4. If configured, create and checkout a request branch:
   - `command-center/DEV-00042-short-title`
5. Record baseline Git status.
6. Start the root session.

### 19.2 During execution

- Agents modify only reserved files.
- Agents do not stage or commit.
- The node tracks file changes and reservation ownership.
- Review agents may read the entire repository and current diff.
- Verification commands acquire the `project-build` resource.
- Source mutations pause while final verification is running.

### 19.3 Request completion

The node must:

1. Confirm no active mutation operation.
2. Confirm all reservations are released or deliberately retained for recovery.
3. Capture changed files and diff summary.
4. Run configured verification.
5. Optionally stage and create one request-level commit.
6. Never merge into the default branch automatically in the PoC.
7. Persist the final branch and commit identifiers.

### 19.4 Dirty start

Default project policy:

```yaml
repository:
  requireCleanStart: true
  allowUntrackedFiles: false
  automaticStash: false
  automaticHardReset: false
  automaticClean: false
```

If the repository is unexpectedly dirty, the request becomes blocked and the UI lists affected files.

The application must never silently delete, reset, or stash user changes.

### 19.5 External modifications

The node monitors the workspace for changes that cannot be attributed to an active reservation holder.

Unexpected changes produce:

```text
BLOCKED — Unattributed external repository modification
```

The user can inspect, accept as baseline, or cancel the request.

---

## 20. Verification Model

Verification runs are typed node operations, not arbitrary root-agent shell access.

Example project configuration:

```yaml
verificationProfiles:
  default:
    commands:
      - id: dotnet-test
        executable: dotnet
        arguments: ["test", "--no-restore"]
        workingDirectory: "."
        timeoutSeconds: 900
      - id: runtime-test
        executable: npm
        arguments: ["test"]
        workingDirectory: "runtime"
        timeoutSeconds: 600
```

### 20.1 Verification rules

- Executable and arguments come from trusted project configuration.
- Agent prompts cannot supply arbitrary executable paths.
- Verification obtains `project-build` resource lease.
- Final verification is incompatible with active source mutation.
- Standard output and error are captured with size limits.
- Exit code, duration, and output summary are persisted.
- A failing mandatory command blocks completion.

---

## 21. Status Model

The frontend must show the simple statuses requested by the user, while the backend stores independent dimensions.

### 21.1 Liveness

```text
Starting
Online
Disconnected
Exited
```

### 21.2 Activity

```text
Idle
Planning
Reasoning
Responding
RunningTool
WaitingForReservation
WaitingForChild
WaitingForMessage
Reviewing
Verifying
Finalizing
```

### 21.3 Attention

```text
None
InputRequired
ApprovalRequired
ReservationConflict
Warning
Error
```

### 21.4 Work state

```text
Queued
Starting
Planning
Executing
Reviewing
Verifying
Blocked
Completed
Failed
Cancelled
```

### 21.5 User-facing projection

| Condition | User-facing status |
|---|---|
| Active model stream, tool, child coordination, review, or verification | Active |
| Healthy session with no current operation and no blocker | Idle |
| Reservation conflict, input request, approval, failed required verification, or external change | Blocked |
| Accepted request completion gate or completed child assignment | Completed |
| Missed heartbeat past threshold | Disconnected |
| Unexpected terminal failure | Failed |
| User or parent cancellation | Cancelled |

### 21.6 Precedence

For an individual session:

```text
Cancelled
Failed
Disconnected
Blocked
Active
Completed
Idle
Starting
```

The reducer must not infer `Idle` from silence. Idle requires an explicit turn-completed or runtime snapshot signal.

### 21.7 Human-readable reason

Every status projection must include a reason, for example:

```text
ACTIVE — Editing ReservationService.cs
ACTIVE — Waiting for Claude reviewer
BLOCKED — File reserved by BlueLake
BLOCKED — dotnet test failed
IDLE — Awaiting parent instructions
COMPLETED — Review approved and assignment returned
DISCONNECTED — Last heartbeat 37 seconds ago
```

---

## 22. Normalized Event Contract

All runtime and supervisor activity is converted to this envelope:

```json
{
  "protocolVersion": 1,
  "eventId": "01K...",
  "nodeId": "fedora-workstation",
  "projectId": "agent-command-center",
  "requestId": "DEV-00042",
  "sessionId": "session-01K...",
  "parentSessionId": "session-root",
  "sequence": 147,
  "runtime": "pi",
  "type": "tool.started",
  "occurredAt": "2026-09-04T20:12:31Z",
  "payload": {}
}
```

### 22.1 Event requirements

- `eventId` is globally unique.
- `sequence` is strictly increasing per session.
- Events are idempotent by `eventId`.
- Unknown event types are stored and ignored safely by older reducers.
- Unknown payload properties are ignored safely.
- Raw provider events may be stored separately for debugging with retention limits.

### 22.2 Minimum event types

```text
node.connected
node.disconnected

request.claimed
request.phase_changed
request.blocked
request.completed
request.failed
request.cancelled

session.registered
session.snapshot
session.heartbeat
session.disconnected
session.closed

turn.started
turn.completed
message.started
message.delta
message.completed

tool.started
tool.progress
tool.completed
tool.failed

child.requested
child.started
child.status_changed
child.completed
child.failed
child.cancelled

mail.sent
mail.received
mail.acknowledged

reservation.requested
reservation.granted
reservation.denied
reservation.renewed
reservation.handoff_requested
reservation.transferred
reservation.released
reservation.expired
reservation.recovery_required
reservation.force_released

verification.started
verification.completed
verification.failed

repository.changed
repository.external_change_detected
repository.checkpoint_created
```

### 22.3 Event reduction

The Control Plane maintains:

- Append-only `SessionEvents`.
- Current `SessionProjection`.
- Current `RequestProjection`.
- Current `ProjectProjection`.

State changes and SignalR updates occur transactionally after event persistence.

---

## 23. Node-to-Control-Plane Transport

The node opens an outbound authenticated connection to the Control Plane.

For the PoC, use either:

- gRPC bidirectional streaming, preferred; or
- a persistent SignalR client connection if it materially reduces implementation effort.

The contract must support:

- Node registration.
- Heartbeat.
- Request claim and assignment.
- Event batches.
- Event acknowledgement.
- Commands from Control Plane to node.
- Reconnect and replay.

### 23.1 Local event spool

The node stores unacknowledged events in local SQLite.

On reconnect:

1. Send node inventory.
2. Send active session snapshots.
3. Replay unacknowledged events in order.
4. Delete or mark acknowledged only after Control Plane confirmation.

---

## 24. Pi Worker Protocol

The Node launches a TypeScript Pi worker and communicates over strict newline-delimited JSON on stdin/stdout.

Do not mix logs with protocol stdout. Logs go to stderr.

### 24.1 Envelope

```json
{
  "protocolVersion": 1,
  "messageId": "01K...",
  "kind": "request",
  "sessionId": "session-01K...",
  "type": "agent.spawn",
  "payload": {}
}
```

Kinds:

```text
hello
event
request
response
heartbeat
goodbye
```

### 24.2 Required requests from Pi worker

```text
plan.submit
plan.revise
agent.spawn
agent.status
agent.await
agent.message.send
agent.inbox.read
agent.message.acknowledge
agent.cancel
reservation.acquire
reservation.expand
reservation.release
reservation.handoff.request
project.diff.inspect
verification.request
request.block
request.complete
```

### 24.3 Backpressure

- Protocol writes must be asynchronous.
- Message deltas may be sampled or coalesced.
- Lifecycle, blocker, reservation, error, and completion events must never be dropped.
- Maximum frame size must be documented and tested.

---

## 25. Pi Runtime Adapter

### 25.1 SDK integration

Use the Pi SDK from `@earendil-works/pi-coding-agent`.

Create sessions through the SDK and subscribe to structured session events.

Do not scrape Pi TUI output.

### 25.2 Root mode

Root mode configuration:

- Tools: `read`, `grep`, `find`, `ls`, plus orchestration tools.
- No unrestricted file mutation tools.
- No unrestricted shell.
- System prompt enforces orchestration role.
- Session persisted to an application-controlled path.
- Session events normalized and forwarded.

### 25.3 Child mode

Child mode configuration depends on permission profile.

Read-only child:

```text
read
grep
find
ls
mail tools
```

Reserved-write child:

```text
read
grep
find
ls
reservation tools
reservation-aware edit/write/move/delete
mail tools
verification request tool
```

### 25.4 Root system policy

The root system prompt must include equivalent requirements to:

```text
You are the root development orchestrator for one managed work request.
You coordinate work; you do not directly implement it.

For every development request:
1. Inspect enough context to create a correct plan.
2. Delegate implementation to managed child agents.
3. Assign non-overlapping file scopes to parallel writers.
4. Use messages and reservation handoff to resolve ownership conflicts.
5. Require independent review and configured verification.
6. Submit completion only after objective evidence is available.

You must not edit files, run arbitrary shell commands, mutate Git state,
or access provider credentials.
```

Technical tool restrictions remain authoritative even if the model ignores this policy.

---

## 26. Claude Code Runtime Adapter

### 26.1 Provider boundary

Launch the official, unmodified `claude` executable.

The adapter must not:

- Modify the Claude Code binary.
- Extract OAuth tokens.
- Copy credential stores.
- Implement Claude subscription authentication itself.
- Present Claude login inside the Command Center.

The user authenticates through Claude Code's own flow before managed use.

### 26.2 Process mode

Use Claude Code's non-interactive structured output mode.

The adapter must:

- Capture session ID.
- Parse structured lifecycle and tool events.
- Capture final result and usage metadata when available.
- Support cancellation through process signaling.
- Support resume only after contract tests validate the pinned CLI version.

### 26.3 Permissions

Profiles:

- Read-only reviewer: read/search only.
- Reserved-write implementer: `Edit` and `Write` subject to mandatory `PreToolUse` reservation hook.
- No unrestricted Bash in PoC write profiles.

### 26.4 Hook installation

Hooks must be created and managed by the node in a trusted application-owned configuration location.

Repository content must not be able to replace or disable the enforcement hook.

Hook validation request includes:

```json
{
  "sessionId": "session-01K...",
  "operation": "write",
  "path": "src/Foo.cs",
  "leaseId": "lease-01K...",
  "fencingToken": 27
}
```

The hook blocks on missing, expired, conflicting, or stale reservations.

---

## 27. Antigravity Runtime Adapter

### 27.1 Provider boundary

Launch the official `agy` executable and use its cached provider-managed authentication.

Do not copy or inspect Google subscription credentials.

### 27.2 PoC capability profile

The Antigravity adapter is read-only reviewer/researcher in the PoC.

Required capabilities:

- Start headless session.
- Parse NDJSON events.
- Capture conversation ID.
- Parse step activity, tool telemetry, subagent telemetry when emitted, usage, and final result.
- Keep stdin open for a multi-turn session when supported.
- Send a new prompt only after the previous result event.
- Close stdin for graceful completion.
- Cancel by terminating the process.

Unsupported controls must not appear in the UI.

### 27.3 Future write support

Write-capable Antigravity sessions are deferred until the implementation validates:

- Hook behavior.
- Permission configuration.
- Reliable path extraction for write tools.
- Prevention of shell-based bypass.

---

## 28. Runtime Adapter Interface

Implement a runtime-neutral contract in C#.

```csharp
public interface IAgentRuntimeAdapter
{
    string RuntimeKind { get; }

    AgentRuntimeCapabilities Capabilities { get; }

    Task<AgentSessionHandle> StartAsync(
        AgentStartRequest request,
        CancellationToken cancellationToken);

    IAsyncEnumerable<NormalizedAgentEvent> WatchAsync(
        string sessionId,
        CancellationToken cancellationToken);

    Task SendAsync(
        string sessionId,
        AgentInput input,
        CancellationToken cancellationToken);

    Task CancelAsync(
        string sessionId,
        CancellationToken cancellationToken);

    Task<AgentRuntimeSnapshot> GetSnapshotAsync(
        string sessionId,
        CancellationToken cancellationToken);
}
```

PoC implementations:

```text
PiRuntimeAdapter
ClaudeCodeRuntimeAdapter
AntigravityRuntimeAdapter
FakeRuntimeAdapter
```

Capabilities, not hardcoded version checks, determine available controls.

---

## 29. Data Model

Use EF Core with SQLite.

### 29.1 Required entities

#### Node

- Id
- DisplayName
- Version
- LastHeartbeatAt
- Status
- CapabilitiesJson

#### Project

- Id
- NodeId
- DisplayName
- RepositoryPath
- DefaultBranch
- ConfigurationJson
- Enabled
- CreatedAt
- UpdatedAt

#### WorkRequest

- Id
- ProjectId
- Title
- Prompt
- Kind
- Priority
- RiskLevel
- Status
- ActivePhase
- BaseBranch
- RequestBranch
- BaseCommit
- FinalCommit
- BlockedReason
- CreatedAt
- StartedAt
- CompletedAt

#### AgentSession

- Id
- ProjectId
- RequestId
- ParentSessionId
- AgentName
- Role
- Runtime
- RuntimeProfile
- ProviderSessionId
- Liveness
- Activity
- Attention
- WorkState
- StatusReason
- CurrentOperation
- ProcessId
- StartedAt
- LastHeartbeatAt
- EndedAt

#### SessionEvent

- EventId
- SessionId
- Sequence
- Type
- OccurredAt
- PayloadJson
- ReceivedAt

#### AgentMessage

- Id
- ProjectId
- RequestId
- ThreadId
- SenderSessionId nullable for human
- Subject
- BodyMarkdown
- Importance
- AcknowledgementRequired
- CreatedAt

#### AgentMessageRecipient

- MessageId
- RecipientSessionId
- ReadAt
- AcknowledgedAt

#### ReservationLease

- Id
- ProjectId
- RequestId
- OwnerSessionId
- FencingToken
- State
- Reason
- AcquiredAt
- RenewedAt
- ExpiresAt
- ReleasedAt

#### ReservationScope

- Id
- LeaseId
- Kind
- NormalizedValue

#### VerificationRun

- Id
- RequestId
- ProfileId
- CommandId
- Status
- ExitCode
- StartedAt
- CompletedAt
- OutputSummary
- OutputArtifactPath

#### ApprovalOrAttentionItem

- Id
- ProjectId
- RequestId
- SessionId
- Type
- Title
- DetailsJson
- Status
- CreatedAt
- ResolvedAt

#### RequestResult

- RequestId
- SummaryMarkdown
- ChangedFilesJson
- ReviewFindingsJson
- VerificationSummaryJson
- CreatedAt

### 29.2 Concurrency

Use optimistic concurrency tokens on:

- WorkRequest.
- AgentSession projection.
- ReservationLease.
- Request claim.

Reservation acquisition and handoff require database transactions with immediate conflict checks.

---

## 30. API Surface

### 30.1 Projects

```text
GET    /api/projects
POST   /api/projects
GET    /api/projects/{projectId}
PATCH  /api/projects/{projectId}
POST   /api/projects/{projectId}/validate
GET    /api/projects/{projectId}/status
```

### 30.2 Requests

```text
GET    /api/projects/{projectId}/requests
POST   /api/projects/{projectId}/requests
GET    /api/requests/{requestId}
POST   /api/requests/{requestId}/cancel
POST   /api/requests/{requestId}/guidance
POST   /api/requests/{requestId}/retry
GET    /api/requests/{requestId}/events
GET    /api/requests/{requestId}/result
```

### 30.3 Sessions

```text
GET    /api/requests/{requestId}/sessions
GET    /api/sessions/{sessionId}
POST   /api/sessions/{sessionId}/message
POST   /api/sessions/{sessionId}/cancel
```

### 30.4 Messages

```text
GET    /api/requests/{requestId}/messages
POST   /api/requests/{requestId}/messages
POST   /api/messages/{messageId}/acknowledge
```

### 30.5 Reservations

```text
GET    /api/requests/{requestId}/reservations
POST   /internal/reservations/acquire
POST   /internal/reservations/{leaseId}/renew
POST   /internal/reservations/{leaseId}/expand
POST   /internal/reservations/{leaseId}/release
POST   /internal/reservations/{leaseId}/request-handoff
POST   /internal/reservations/{leaseId}/transfer
POST   /api/reservations/{leaseId}/force-release
POST   /internal/reservations/authorize-mutation
```

Internal endpoints require node authentication and are not exposed to the browser.

---

## 31. Frontend Requirements

Use a Blazor Web App with Interactive Server rendering for the PoC.

SignalR updates the UI as projections change.

### 31.1 Fleet dashboard

Display:

- Number of active projects.
- Active agents.
- Blocked agents or requests.
- Queued requests.
- Project cards.
- Global attention items.

Example:

```text
AGENT COMMAND CENTER

Needs attention: 2
Active projects: 3
Active agents: 7
Queued requests: 5

PROJECTS
Agent Command Center   1 active · 1 blocked · 2 queued
Project-It             idle · 2 queued
Jolera MCP             1 active · 3 agents
```

### 31.2 Project page

Display:

- Node status.
- Repository path and branch.
- New request composer.
- Active request.
- Queue.
- Recent completed requests.
- Active agent count.
- Active reservations.

### 31.3 Request page

Display:

- Original request.
- Current phase and status reason.
- Root and child agent tree.
- Structured plan.
- Live event timeline.
- Messages.
- Reservations.
- Current diff summary.
- Verification results.
- Review findings.
- Final result.
- Cancel and send-guidance actions.

### 31.4 Agent tree

Example:

```text
● Root Pi / Qwen                         ACTIVE
  ✓ Architect / Claude Code              COMPLETED
  ● Domain implementer / Pi-Qwen         ACTIVE
    Editing ReservationLease.cs
  ■ Gateway implementer / Claude Code    BLOCKED
    Waiting for ReservationService.cs
  ○ Reviewer / Antigravity                IDLE
```

### 31.5 Reservation panel

Display:

- Owner.
- Role/runtime.
- Request.
- Scopes.
- Expiration countdown.
- State.
- Conflict or handoff controls where authorized.

### 31.6 Attention inbox

Display:

- Human input requests.
- Reservation conflicts.
- Handoff requests.
- Failed verification.
- External repository changes.
- Disconnected agents retaining leases.

---

## 32. Completion Gate

`submit_completion` is accepted only when all required conditions are true.

### 32.1 Mandatory conditions

- Root plan exists.
- Required implementation assignments completed.
- Independent review completed.
- No unresolved blocking findings.
- Required verification commands passed.
- No active write operations.
- No active reservations, except explicitly quarantined recovery leases.
- Repository diff was captured.
- Changed-file ownership is known.
- Request result summary exists.

### 32.2 Rejection response

```json
{
  "accepted": false,
  "missingRequirements": [
    "Independent review has not completed.",
    "Reservation lease-01K remains active.",
    "dotnet-test verification failed."
  ]
}
```

The root receives this response and must resolve the missing requirements.

---

## 33. Failure and Recovery

### 33.1 Control Plane restart

- Node remains running.
- Agent processes remain running.
- Node spools events locally.
- Node reconnects and replays.
- Browser reloads current projections from SQLite.

### 33.2 Node restart

For the PoC, active child processes may be marked failed if they cannot be reattached.

The design must record enough process and provider-session metadata to support future reattachment.

### 33.3 Runtime process crash

- Capture exit code and stderr tail.
- Mark session failed.
- Preserve reservations in recovery-required state.
- Notify root and user.
- Do not automatically reuse scopes until recovery inspection.

### 33.4 Provider authentication missing

- Adapter emits blocked status, not generic failure.
- UI explains that provider-native authentication must be completed locally.
- The Command Center does not collect credentials.

### 33.5 Reservation service unavailable

- All mutation operations fail closed.
- Read-only work may continue.
- Existing leases are not assumed valid beyond locally cached expiry.

### 33.6 Malformed runtime output

- Preserve raw line in diagnostic log.
- Emit adapter warning.
- Do not crash the Node.
- Fail the session only when synchronization cannot continue safely.

---

## 34. Security Requirements

### 34.1 PoC authentication

- No anonymous web access.
- Use ASP.NET Core Identity with one local administrator account.
- Use cookie authentication and CSRF protection.
- Bind to loopback by default.
- Document private-LAN HTTPS configuration for phone access.

### 34.2 Node authentication

- Node uses an application-generated credential stored in a user-private file.
- File permissions must be `0600` on Linux.
- Node connection is authenticated.
- Commands include correlation IDs and request IDs.

### 34.3 Filesystem boundary

- Only registered project paths may be used.
- Resolve and validate every path.
- Reject path traversal and external symlink resolution.
- Agents cannot reserve or modify `.git/`.
- Application configuration and hooks live outside agent-writable project paths.

### 34.4 Provider credentials

- Never store provider OAuth tokens in SQLite.
- Never return credential paths to an agent.
- Never copy provider credential files into session directories.
- Official provider login remains interactive and provider-controlled.

### 34.5 Prompt and repository content

Treat project files, instructions, tool output, and messages as untrusted content.

They must not be able to:

- Change runtime executable paths.
- Select privileged runtime profiles.
- Disable reservation hooks.
- Grant broader permissions.
- Alter project roots.
- Read provider credentials.
- Override completion gates.

### 34.6 Logging

- Redact known secret patterns.
- Limit captured stdout/stderr size.
- Do not log environment variables wholesale.
- Separate protocol stdout from diagnostic stderr.
- Audit all force-release, cancellation, completion override, and Git mutations.

---

## 35. Configuration

Suggested root configuration:

```yaml
commandCenter:
  dataPath: ~/.local/share/pi-command-center
  controlPlaneUrl: https://127.0.0.1:7443

node:
  id: fedora-workstation
  maxSessions: 12
  heartbeatSeconds: 10
  eventSpoolPath: ~/.local/share/pi-command-center/node-spool.db

orchestration:
  enabledByDefault: true
  rootMayModifyWorkspace: false
  requireImplementationWorker: true
  requireIndependentReview: true
  maxChildAgentsPerRequest: 4
  maxAgentDepth: 2

reservations:
  requiredForMutation: true
  leaseSeconds: 120
  renewalSeconds: 30
  atomicMultiScopeAcquisition: true
  failClosed: true

projects:
  - id: agent-command-center
    path: /home/user/Developer/agent-command-center
    defaultBranch: main
    maxActiveWriteRequests: 1
    maxReadOnlyRequests: 2
    createRequestBranch: true
    createRequestCommit: true
    autoMerge: false

runtimeProfiles:
  local-pi:
    adapter: pi
    modelProfile: local-qwen
    maxConcurrency: 4

  claude-personal:
    adapter: claude-code
    executable: claude
    maxConcurrency: 2

  google-personal:
    adapter: antigravity
    executable: agy
    maxConcurrency: 2
```

Use strongly typed options with startup validation.

---

## 36. Repository Structure

Create a monorepo:

```text
pi-command-center/
├── PiCommandCenter.sln
├── Directory.Build.props
├── Directory.Packages.props
├── global.json
├── README.md
├── docs/
│   ├── architecture.md
│   ├── protocols.md
│   └── security.md
│
├── src/
│   ├── PiCommandCenter.Domain/
│   ├── PiCommandCenter.Application/
│   ├── PiCommandCenter.Infrastructure/
│   ├── PiCommandCenter.ControlPlane/
│   ├── PiCommandCenter.Web/
│   ├── PiCommandCenter.Node/
│   └── PiCommandCenter.Contracts/
│
├── runtime/
│   ├── package.json
│   ├── tsconfig.json
│   ├── pi-worker/
│   │   ├── src/
│   │   │   ├── index.ts
│   │   │   ├── protocol.ts
│   │   │   ├── root-session.ts
│   │   │   ├── child-session.ts
│   │   │   └── tools/
│   │   └── tests/
│   └── hooks/
│       ├── claude-reservation-hook/
│       └── common/
│
└── tests/
    ├── PiCommandCenter.Domain.Tests/
    ├── PiCommandCenter.Application.Tests/
    ├── PiCommandCenter.Infrastructure.Tests/
    ├── PiCommandCenter.ControlPlane.IntegrationTests/
    ├── PiCommandCenter.Node.Tests/
    └── PiCommandCenter.EndToEndTests/
```

### 36.1 Dependency direction

```text
Domain              depends on nothing application-specific
Contracts           shared transport DTOs only
Application         depends on Domain
Infrastructure      implements Application interfaces
ControlPlane        composes Application + Infrastructure
Web                 presentation only
Node                depends on Contracts and node-specific abstractions
Runtime TypeScript  communicates only through versioned protocol
```

---

## 37. Suggested Implementation Sequence

Implementation must proceed in vertical slices and keep builds passing.

### Milestone 0 — Scaffold

- Create solution and TypeScript workspace.
- Add formatting, analyzers, nullable reference types, and test projects.
- Add SQLite migration baseline.
- Add health endpoints.
- Add basic CI script.

**Exit condition:** `dotnet test` and runtime tests pass.

### Milestone 1 — Projects and requests

- Implement Project and WorkRequest entities.
- Implement project registration and validation.
- Implement queue APIs.
- Build basic dashboard and request composer.

**Exit condition:** A persisted request can be queued for a valid project.

### Milestone 2 — Node connection

- Implement Node worker.
- Implement registration, heartbeat, claim, and event batch transport.
- Implement local event spool and replay.
- Show node online/offline in UI.

**Exit condition:** Node claims a request and survives Control Plane restart.

### Milestone 3 — Pi root session

- Implement TypeScript Pi worker protocol.
- Embed Pi SDK.
- Start read-only root orchestrator.
- Implement `create_plan`, `spawn_agent`, `get_agent_status`, and `submit_completion` stubs.
- Normalize Pi lifecycle events.

**Exit condition:** Root Pi starts from a queued request and produces a stored plan without editing files.

### Milestone 4 — Pi child session

- Implement Pi child mode.
- Implement parent-child hierarchy.
- Implement fake reservation-aware write tool first.
- Persist child results.

**Exit condition:** Root Pi launches a Pi child and the UI shows both sessions live.

### Milestone 5 — Mail coordination

- Implement agent identities.
- Implement request threads, inbox, replies, and acknowledgement.
- Add root and child mail tools.
- Add browser message panel and human guidance.

**Exit condition:** Root, child, and human exchange persisted messages.

### Milestone 6 — Strict reservations

- Implement deterministic scope normalization and conflict checks.
- Implement atomic lease acquisition.
- Implement TTL, renewal, fencing tokens, release, and recovery-required state.
- Implement handoff request and atomic transfer.
- Add reservation UI.
- Add reservation-aware Pi mutation tools.

**Exit condition:** Two Pi children edit disjoint files concurrently; a conflicting write is blocked and later resumes after handoff.

### Milestone 7 — Claude Code adapter

- Implement official CLI process launch.
- Implement structured output parser.
- Implement session projection.
- Install trusted reservation hooks.
- Implement read-only and reserved-write profiles without unrestricted Bash.

**Exit condition:** Root Pi delegates a write task to Claude Code, and an invalid unreserved edit is blocked.

### Milestone 8 — Antigravity adapter

- Implement official `agy` process launch.
- Implement streaming JSON parser.
- Implement read-only reviewer profile.
- Normalize conversation, step, tool, usage, and final-result events.

**Exit condition:** Root Pi delegates an independent review to Antigravity and receives a compact result.

### Milestone 9 — Verification and completion gate

- Implement trusted verification profiles.
- Implement build resource reservation.
- Implement completion gate.
- Store result summary, changed files, tests, and findings.

**Exit condition:** A request cannot complete until review and verification pass.

### Milestone 10 — Recovery and demonstration

- Add process crash handling.
- Add disconnected status.
- Add reservation recovery inspection.
- Add end-to-end demo fixtures.
- Document local setup and provider login prerequisites.

**Exit condition:** All acceptance scenarios pass.

---

## 38. Testing Requirements

### 38.1 Unit tests

Required areas:

- Path normalization.
- File/directory/resource conflict matrix.
- Atomic acquisition rollback.
- Fencing-token rejection.
- Handoff.
- Expiration and recovery transitions.
- Status reducer precedence.
- Completion-gate evaluation.
- Queue ordering and request claims.
- Runtime capability projection.

### 38.2 Integration tests

- EF Core transaction behavior under concurrent reservation requests.
- Node reconnect and event replay.
- Duplicate event idempotency.
- Pi worker JSONL framing.
- Runtime parser behavior with malformed lines.
- Claude hook allow and deny decisions using fixtures.
- Repository external-change detection.

### 38.3 Runtime contract tests

Tests against real CLIs must be opt-in because they may consume subscription quota.

Environment flags:

```text
RUN_REAL_PI_TESTS=1
RUN_REAL_CLAUDE_TESTS=1
RUN_REAL_ANTIGRAVITY_TESTS=1
```

Contract tests must record the detected CLI version and fail with an actionable compatibility message.

### 38.4 End-to-end scenarios

#### Scenario A — Normal delegated change

1. Submit “Add a health endpoint and tests.”
2. Root Pi plans.
3. Pi child implements.
4. Claude or Antigravity reviews.
5. Verification passes.
6. Request completes.

#### Scenario B — Concurrent disjoint writers

1. Root assigns domain file to Pi child.
2. Root assigns API file to Claude child.
3. Both reserve disjoint scopes.
4. Both edit concurrently.
5. UI shows both active.

#### Scenario C — Reservation conflict and handoff

1. Agent A reserves `DependencyInjection.cs`.
2. Agent B requests the same file.
3. Acquisition is denied atomically.
4. Agent B becomes blocked.
5. Agent B sends handoff request.
6. Agent A accepts.
7. Token changes.
8. Agent B resumes.
9. Agent A's stale token is rejected.

#### Scenario D — Crashed lease owner

1. Agent holds reservation.
2. Process is terminated.
3. Lease becomes suspect, then recovery-required.
4. New owner is denied until process check and recovery snapshot complete.
5. Scope is safely released.

#### Scenario E — Unattributed external change

1. Managed request runs.
2. A human modifies an unreserved file externally.
3. Node detects it.
4. Request becomes blocked.
5. UI shows file and action choices.

#### Scenario F — Control Plane restart

1. Agents remain active.
2. Stop and restart Control Plane.
3. Node spools events.
4. Node reconnects.
5. State and event history recover without duplicates.

---

## 39. Performance and Operational Requirements

- Dashboard projection query should complete under 500 ms on the PoC dataset.
- Session status updates should normally appear in the browser within 2 seconds.
- Reservation authorization should complete under 250 ms on the local machine.
- A slow browser must not backpressure agent runtimes.
- Streaming message deltas may be coalesced to 100–250 ms intervals.
- Event payloads and tool output require configurable size limits.
- SQLite must use WAL mode.
- Database migrations run at controlled startup with logging.

---

## 40. Coding and Delivery Standards

The implementation agent must:

- Use nullable reference types.
- Use async APIs and cancellation tokens for I/O.
- Avoid blocking waits in ASP.NET and worker code.
- Use `System.Text.Json` for protocol serialization.
- Use `TimeProvider` for testable time behavior.
- Use structured logging.
- Validate all configuration at startup.
- Avoid shell string concatenation; use `ProcessStartInfo.ArgumentList`.
- Keep provider adapters isolated behind interfaces.
- Add tests with each domain behavior.
- Keep raw provider-specific types out of Domain and UI projects.
- Document all protocol versions and compatibility assumptions.
- Never introduce Git worktrees.

---

## 41. Required Initial Deliverables

OMP should produce:

1. The complete repository scaffold.
2. Buildable .NET solution.
3. Buildable TypeScript runtime workspace.
4. EF Core schema and migrations.
5. Architecture documentation.
6. Protocol documentation.
7. Project/request queue vertical slice.
8. Node registration and event stream.
9. Root Pi session integration.
10. Child session tree.
11. Mail coordination.
12. Strict file reservations.
13. Pi, Claude Code, and Antigravity adapters at the PoC capability levels.
14. Blazor dashboard, project page, and request page.
15. Automated tests and demonstration script.
16. Fedora systemd user-service files for Control Plane and Node.
17. Setup documentation, including provider-native login prerequisites.

---

## 42. Implementation Guardrails for OMP

1. Do not expand scope into a full production platform.
2. Do not introduce worktrees.
3. Do not replace strict reservations with advisory warnings.
4. Do not allow the root Pi agent to edit files directly.
5. Do not grant unrestricted shell to managed writers in the PoC.
6. Do not store provider credentials.
7. Do not modify provider-specific official binaries.
8. Do not bind the core domain model to Claude-, Google-, or Pi-specific event types.
9. Do not infer idle solely from elapsed time.
10. Do not allow request completion based only on model text.
11. Do not silently reset, clean, or stash a repository.
12. Do not implement arbitrary glob reservations in the first version; use exact files, directory prefixes, and named resources.
13. Keep each milestone buildable and testable.
14. Prefer the smallest end-to-end vertical slice over unfinished infrastructure breadth.

---

## 43. First Demonstration

Use a small ASP.NET Core fixture repository.

Submit this request through the web UI:

> Add a `/health/details` endpoint, add tests, and update the README. Split the implementation so one agent changes the API and another changes the tests. Require independent review and run the configured test profile.

Expected result:

```text
Root Pi / local Qwen                  ACTIVE
├── API implementer / Pi             ACTIVE
│   Reservation: src/App/HealthEndpoint.cs
├── Test implementer / Claude Code   ACTIVE
│   Reservation: tests/App.Tests/HealthEndpointTests.cs
└── Reviewer / Antigravity           IDLE until dependencies complete
```

Then deliberately force both implementers to request the same registration file. Confirm:

- One reservation is denied.
- The affected agent is blocked.
- A handoff message is sent.
- Ownership transfers atomically.
- The stale token fails.
- Work resumes.
- Verification passes.
- The request becomes completed.

---

## 44. Future Extensions

After PoC acceptance, likely next capabilities are:

- Multiple nodes.
- Discord command interface.
- Beads-backed durable task graph.
- MCP Agent Mail mirroring or integration.
- CASS session search and project memory.
- Provider subscription usage analytics.
- Browser notifications.
- Passkey authentication.
- Container or micro-VM worker isolation.
- Write-capable Antigravity adapter.
- Native interactive terminal attachment.
- Request continuation and dependency chains.
- Pull-request creation and controlled integration.

These are not part of the PoC.

---

## 45. External Runtime Contracts and References

The implementer must pin and contract-test all external runtime versions rather than assuming their interfaces never change.

### Pi

- SDK and `AgentSession` behavior:  
  https://github.com/earendil-works/pi/blob/main/packages/coding-agent/docs/sdk.md
- Coding-agent package and RPC overview:  
  https://github.com/earendil-works/pi/blob/main/packages/coding-agent/README.md

Pi's SDK provides structured sessions, event subscription, prompting, steering, follow-up, abort, model control, and custom tool selection. The PoC must use those supported surfaces rather than TUI scraping.

### Claude Code

- Programmatic/headless use:  
  https://code.claude.com/docs/en/headless
- Hooks:  
  https://code.claude.com/docs/en/hooks-guide
- Legal and credential boundary:  
  https://code.claude.com/docs/en/legal-and-compliance

Claude Code must remain unmodified. The user authenticates through Anthropic's flow, and the Command Center supervises the official process.

### Google Antigravity CLI

- CLI overview:  
  https://antigravity.google/docs/cli/overview/
- Headless and streaming JSON mode:  
  https://antigravity.google/docs/cli/headless/

Use `agy` as the Google consumer-subscription harness. Its adapter must be capability-driven and version-tested.

### MCP Agent Mail reference behavior

- Project:  
  https://github.com/Dicklesworthstone/mcp_agent_mail

Agent Mail is a behavioral reference for identities, inboxes, threaded messages, acknowledgements, and leases. Its existing file reservations are advisory; this Command Center requires stricter atomic enforcement and fencing tokens.

---

## 46. Definition of Done

The PoC is done when:

- The first demonstration succeeds entirely through the web interface.
- Pi orchestrates by default and never directly edits the repository.
- Pi, Claude Code, and Antigravity sessions appear in one normalized agent tree.
- Agents coordinate through the internal mail system.
- Disjoint writers operate concurrently in one shared repository.
- Conflicting writes are technically prevented through strict reservations.
- Reservation handoff and stale-token rejection work.
- Statuses are accurate and live.
- Review and verification gate completion.
- State survives browser and Control Plane restarts.
- No Git worktrees are created.
- No provider subscription credential is stored or intercepted.
- Automated tests cover the reservation authority, status reducer, completion gate, node replay, and runtime parsers.

