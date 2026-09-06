# Adding Cursor support

## Status

This document is a design proposal, not a description of current behavior. DevFleet does not currently expose a `cursor/*` runtime or a Cursor row on `/usage`. The current source of truth reserves `claude-code`, `antigravity`, and `muse` as external harnesses; every other selector prefix is sent to Pi.

The recommended integration is a new node-owned Cursor runtime backed by the official [`@cursor/sdk`](https://cursor.com/docs/sdk/typescript) in **local** mode. It must use Cursor's public SDK and authentication surfaces. It must not copy Oh My Pi's built-in `cursor` provider or call Cursor's private `api2.cursor.sh` Connect/protobuf endpoints directly.

Before implementation, update `SPEC.md`, `README.md`, `docs/architecture.md`, `docs/protocols.md`, and `docs/security.md` together. Those files currently state that Cursor is unsupported and enumerate the only reserved runtime prefixes.

## Decision summary

| Question | Decision |
|---|---|
| Selector | Reserve `cursor`; use `cursor/<SDK-model-id>` (never a model id of `default`) |
| Runtime | Dedicated `CursorRuntimeAdapter`, not the Pi adapter |
| Cursor integration | Official `@cursor/sdk`, local agent runtime |
| Root orchestration | Keep Pi as the mandatory root; Cursor is initially a child runtime |
| Authentication | Official SDK user or service-account API key; prefer SDK-managed browser login storage |
| Workspace | Cursor receives an application-owned session directory as `local.cwd`, not the repository |
| Repository access | Node-owned custom tools only; all paths resolve against the immutable ExecutionAssignment workspace |
| Writes | Reservation-aware custom tools only; no Cursor native edit, write, shell, Git, or subagent tools |
| Model discovery | `Cursor.models.list()`, account-specific and fail-closed |
| Events | A separate versioned Cursor-worker NDJSON protocol normalized by the node |
| Cloud Agents | Out of scope because they clone into another workspace and own branch/PR behavior |
| Subscription quota | No `/usage` row until Cursor exposes a suitable public remaining-quota contract |

## Why OMP's built-in Cursor provider is not the model

OMP supports a Cursor subscription by obtaining an OAuth access token and implementing Cursor's private client protocol. Its source logs in through Cursor's deep-control flow, discovers models through `GetUsableModels`, and sends HTTP/2 Connect/protobuf requests directly to the private Agent service.

That mechanism is unsuitable for DevFleet:

- It is not the public Cursor SDK, CLI, or Cloud Agents API.
- It would make DevFleet responsible for a reverse-engineered protocol and token refresh behavior.
- It conflicts with DevFleet's rule that provider authentication remains inside an official provider surface.
- Cursor staff have stated that OMP's provider is an unauthorized client using private endpoints, is contrary to Cursor's Use Restrictions, and may trigger account enforcement. See the [Cursor staff response](https://forum.cursor.com/t/does-using-oh-my-pi-s-cursor-provider-or-an-openai-compatible-proxy-to-the-same-endpoints-violate-cursor-s-tos/167778/5) and [Cursor Terms §1.5](https://cursor.com/terms-of-service).

Do not add `cursor` to Pi's provider configuration, import OMP's provider code, accept a `CURSOR_ACCESS_TOKEN`, reproduce Cursor client headers, or depend on an undocumented endpoint. A `cursor/*` selector must resolve only to the dedicated official-SDK adapter.

## Evaluation of official integration surfaces

### Official TypeScript SDK — recommended

The SDK is the best fit for the current architecture:

- `Agent.create()` runs Cursor's agent loop locally against a chosen `cwd`.
- `run.stream()` emits structured assistant, thinking, tool, and status events.
- `run.wait()` returns terminal status, final text, model, duration, and token usage when available.
- `run.cancel()` supports bounded cancellation.
- `Agent.resume()` and an explicit local store can support later restart reconciliation.
- `Cursor.models.list()` returns the models and parameters available to the authenticated account or team.
- `local.customTools` exposes host functions through Cursor's MCP path.
- `tools` and `disallowedTools` can remove Cursor's native file, shell, task, and web tools.

The SDK runs Cursor's complete agent harness; it is not a raw chat-completions or model-inference API. DevFleet should treat it as an external runtime in the same architectural sense as Claude Code, Antigravity, and Muse Code.

### Official CLI over ACP — fallback for investigation

The official Cursor CLI can run `agent acp` over newline-delimited JSON-RPC. ACP provides initialization, authentication, session creation/loading, prompts, streaming updates, cancellation, and permission requests.

Do not use ACP for the first write-capable adapter. An ACP permission response authorizes a Cursor-owned tool; it is not equivalent to DevFleet's per-path reservation and fencing check. Cursor's documented non-interactive CLI also has full write access. ACP could be evaluated later for a read-only `ask`-mode adapter after contract tests prove its filesystem and configuration boundaries.

### Cloud Agents API — incompatible with the current workspace model

Cloud agents clone repositories into Cursor-managed or self-hosted agent environments and can create branches or pull requests. That conflicts with DevFleet's current requirements:

- one node-local canonical WorkspaceBinding;
- no repository mobility or transparent alternate checkout;
- no Git worktrees;
- supervisor-owned branches, index, commits, and completion gate.

Supporting Cloud Agents would require a new request kind and placement/merge model. It must not be hidden behind the existing `cursor/*` local selector.

## Target architecture

```text
Pi root orchestrator
       │ spawn role whose route contains cursor/<model>
       ▼
.NET Node: CursorRuntimeAdapter
       │ strict NDJSON, one worker per DevFleet session
       ▼
runtime/cursor-worker (Node.js + official @cursor/sdk)
       ├── SDK auth store: ~/.cursor/sdk/auth.json
       ├── SDK state: application-owned cursor-runtime/<session>/state
       ├── local.cwd: application-owned cursor-runtime/<session>/workspace
       └── custom tool requests ───────────────┐
                                               ▼
                                      .NET Node gateways
                                      ├── repository path policy
                                      ├── assignment authorization
                                      ├── reservation/fencing authority
                                      ├── mail/orchestration
                                      └── verification profiles
                                               │
                                               ▼
                              ExecutionAssignment canonical workspace
```

The Cursor worker should never receive the canonical repository as a writable process working directory. Set `local.cwd` to an empty, owner-only, application-managed session directory. Cursor learns about and changes the real repository only through DevFleet custom tools. This preserves the same deep boundary used by the Pi worker: model-driven file operations round-trip through the node.

## Authentication and billing boundary

The official SDK supports user API keys and service-account API keys. Its documentation says SDK runs use the same pricing pools and Privacy Mode rules as IDE and Cloud Agent runs; a user key bills to that user's plan and service-account keys bill to their team.

For a local single-user installation:

1. Install the pinned Cursor SDK with the deployed Cursor worker.
2. Outside a managed agent session, run a small operator command that calls `Cursor.auth.login()` from the official SDK.
3. Let the SDK open the browser and mint its user key.
4. Let the SDK store the key at its default `~/.cursor/sdk/auth.json` path. The documented default lifetime is 90 days.
5. The Cursor worker calls SDK APIs without receiving the key over DevFleet's control-plane or worker protocol; the SDK resolves its own stored login.

Security rules:

- Never put `CURSOR_API_KEY` in SQLite, appsettings, SignalR DTOs, the node spool, logs, or an agent prompt.
- Prefer SDK-managed storage over a systemd-wide `CURSOR_API_KEY`, because a service-wide environment variable is inherited by unrelated children.
- The control plane and browser never read the SDK auth file.
- The .NET node should not parse, copy, refresh, or return the key. Only the official SDK process may open its store.
- Treat `~/.cursor/sdk/auth.json` as a credential path: owner-only, excluded from diagnostics, and hidden from Pi, Claude Code, Antigravity, Muse, verification, and other model-driven processes.
- Authentication failure becomes `Attention=InputRequired`, `WorkState=Blocked`, with a fixed operator-safe reason such as `Complete Cursor SDK login locally`. Never include raw stderr, e-mail, user id, key name, or provider response body in that reason.

For unattended team nodes, a Cursor service account is preferable. Provision its key directly to the Cursor worker with a node-local secret mechanism, not through the control plane. Document the secret mechanism and process environment allowlist before enabling it.

## Selector and routing changes

Add `cursor` to `AgentModelSelector` as a reserved external provider prefix. Without that reservation, the current catch-all behavior incorrectly sends `cursor/<model>` to Pi.

Expected selector behavior:

```text
cursor/composer-2.5  -> model: { id: "composer-2.5" }
```

Initial restrictions:

- Cursor may appear in child routes only. `Pi:Model` remains Pi-backed because every request must start a Pi root orchestrator.
- Do not encode SDK parameter objects into the model-id string.
- Discovery may display parameter/variant information, but route candidates initially store only the base model id. Cursor applies the model's documented/default parameter values.
- Adding explicit reasoning, context, Router optimization, or fast-mode parameters requires a versioned extension to the route-candidate contract; do not invent an ambiguous string encoding.
- The node continues trying candidates in route order. Missing/expired Cursor authentication or SDK startup failure skips the candidate and eventually reports `runtime_route_exhausted` if nothing starts.

A possible read/write route after the adapter passes its safety gates:

```yaml
Pi:
  RoleRoutes:
    architect:
      - Model: cursor/composer-2.5
      - Model: codex/gpt-5.6-sol
    implementer:
      - Model: cursor/composer-2.5
      - Model: codex/gpt-5.6-sol
    reviewer:
      - Model: cursor/composer-2.5
      - Model: antigravity/gemini-3-pro
    verifier:
      - Model: cursor/composer-2.5
      - Model: codex/gpt-5.6-sol
```

Do not add Cursor to default routes until the operator has enabled the adapter and discovery/readiness positively verifies the installed SDK and authenticated account.

## Cursor worker contract

Create a separate `runtime/cursor-worker` rather than extending the Pi SDK worker. Reuse the framing conventions where useful, but document it as an independent protocol so Cursor SDK changes cannot accidentally alter Pi protocol v1.

Minimum transport:

- strict LF-delimited JSON;
- explicit `protocolVersion`;
- 1 MiB maximum UTF-8 frame excluding newline;
- protocol stdout only, diagnostics on bounded stderr;
- globally unique message ids and correlated responses;
- malformed-frame errors that do not desynchronize later frames;
- heartbeat while a session is active.

Node-to-worker requests:

```text
session.start
session.input
session.cancel
session.snapshot
goodbye
model.list
auth.status
```

Worker-to-node requests reuse the same node-owned operations already exposed to Pi where the semantics match:

```text
agent/message operations
reservation operations
repository read/search operations
reservation-aware mutation operations
verification.request
```

The worker maps SDK objects to neutral payloads. It must never emit an API key, Cursor account/team identity, raw request headers, environment dump, or unbounded exception body.

### SDK lifecycle mapping

At `session.start`:

1. Validate mode, model, session directory, and protocol fields.
2. Construct one explicit local store under the owner-only session state directory.
3. Create the Cursor agent with the application-owned empty `local.cwd`.
4. Pass the role-specific custom tools and strict tool allowlist.
5. Send the initial prompt and begin streaming.
6. Return `agent.agentId` as `ProviderSessionId`; retain each Cursor run id only as bounded event metadata.

Map events approximately as follows:

| Cursor SDK fact | Normalized event |
|---|---|
| run starts/status `running` | `turn.started` / active snapshot |
| assistant text blocks | `message.started`, `message.delta`, `message.completed` |
| thinking events | reasoning activity/events using existing normalized policy |
| custom tool starts/completes | `tool.started`, `tool.completed` or `tool.failed` |
| terminal `finished` | `turn.completed` |
| terminal `error` | `session.failed` or turn failure, depending on recoverability |
| terminal `cancelled` | `session.cancelled` |
| `RunResult.usage` | final `message.completed` or runtime usage event for `/statistics` |

`SendAsync` should steer the active local run when `run.steer` is available. If the SDK returns `revert_to_followup`, wait for the active run and submit a new `agent.send()`. Persist the disposition so a message is never delivered twice. `CancelAsync` calls `run.cancel()`, waits for a bounded terminal result, disposes the agent, and kills the worker process if the SDK does not settle within the node's grace period.

Restart reattachment is a later capability. Initially, a node restart follows the existing conservative rule: if the worker cannot be reattached, the session fails and its assignment remains `RecoveryRequired` until process/repository quiescence is proved. Do not infer safe release from a missing Cursor process.

## Tool and reservation enforcement

Cursor SDK headless local agents auto-approve tools by default, and SDK sandboxing is off by default. Therefore merely prompting the agent not to write is insufficient.

Use all of these controls:

1. Set `local.settingSources` to an empty allowlist so repository/user Cursor MCP servers, hooks, plugins, and subagents are not inherited. Contract-test the pinned SDK to prove what an empty setting-source list suppresses.
2. Do not pass project `mcpServers` or Cursor subagent definitions.
3. Do not offer Cursor's native `read`, `edit`, `write`, `shell`, Git, web, or `task` tools.
4. Offer only the SDK's MCP capability needed for `local.customTools`; with no other MCP source, the available tool catalog is the node-defined DevFleet catalog.
5. Keep `task` disabled so Cursor cannot create provider-native nested workers outside DevFleet's assignment and child limits.
6. Enable the SDK sandbox as defense in depth, but do not treat auto-review or the SDK sandbox as the reservation boundary.
7. Run the worker itself with an OS boundary that makes the real repository read-only or absent. Repository mutation occurs only in the .NET node after authorization.

Role-specific custom tools:

- **All roles:** node-backed read, grep, find, and list operations plus bounded mail operations.
- **Read-only child:** no mutation tools and no reservation acquisition implying write intent.
- **Reserved-write child:** reservation acquire/expand/release and reserved edit/write/delete/move tools.
- **Cursor child:** no child-spawn or root plan/completion tools; Pi remains the orchestrator.

Every mutation request must include the DevFleet session, ExecutionAssignment, lease id, fencing token, normalized target path, and operation. The node then:

1. authenticates the request as belonging to the live worker/session;
2. verifies assignment/request/project/binding correlations;
3. normalizes the repository-relative path and rejects traversal, symlink escape, and `.git/`;
4. asks the reservation authority to authorize the exact mutation immediately before it occurs;
5. performs the mutation itself;
6. emits the repository and audit events.

Do not rely on Cursor SDK file hooks as the primary gate. The SDK documentation states that hooks are file-based only and provides no programmatic callback. A repository-controlled `.cursor/hooks.json` is untrusted under DevFleet's threat model.

## Model discovery and readiness

Add a bounded Cursor discovery command to the five-minute node discovery wave:

1. Start the installed Cursor worker in discovery mode.
2. Let the official SDK resolve its stored login.
3. Call `Cursor.models.list()`.
4. Map each valid SDK id to `cursor/<id>` and preserve its display name.
5. Validate every result with `AgentModelSelector` before returning it.
6. Return a `cursor` error catalog on timeout, missing SDK, missing authentication, malformed output, oversized output, or an empty usable model list.

Do not hard-code Composer or other model ids as proof of availability. Cursor's catalog is account- and team-specific and can include parameterized models.

Readiness must be stronger than credential-file presence. A bounded probe should call an official SDK account/catalog operation such as `Cursor.models.list()` or `Cursor.me()` and collapse the result to:

```text
Ready       official SDK call succeeded and at least one canonical model is usable
Unavailable known missing/expired login, missing runtime, or incompatible SDK
Unknown     timeout, transient network failure, or unclassified SDK drift
```

Only `Ready` is schedulable. Cross-node status includes only the typed state, stable evidence source, observation time, and routing revision—not model response bodies or account identity.

## Usage and statistics

The first implementation should record `RunResult.usage` token counts when present and feed them into the existing `/statistics` normalization. Missing fields stay null; a reported zero remains zero. Do not calculate dollars from local rate tables.

Do not add Cursor to `/usage` initially. Cursor's SDK exposes run usage and billed usage, but DevFleet's `/usage` contract requires coherent percentage-based remaining windows. Unless Cursor publishes a compatible remaining-quota endpoint, report no Cursor subscription card rather than estimate one or call a private client endpoint.

If a later public SDK/API returns billed dollar cost for a specific run, persist it only with an explicit source label and the existing “provider/client estimate, not invoice” UI wording.

## Process and filesystem isolation

The new process boundary must preserve current DevFleet rules:

- Launch with explicit `ProcessStartInfo.ArgumentList`; never a shell string.
- Pin `@cursor/sdk` and Node versions and verify them in an opt-in contract lane.
- Use one worker per DevFleet session for simple ownership and cancellation.
- Create session directories as `0700` and state files as owner-only where supported.
- Keep the SDK auth store separate from SDK conversation state.
- Bound stdout frames, stderr tails, event payloads, start time, request time, cancel grace, and process-tree termination.
- Never send the node credential or another provider's environment to the worker.
- Give the Cursor worker only its SDK auth path, app-owned state directory, empty SDK workspace, required runtime files, and network access.
- Mask the Cursor credential path from Claude, Antigravity, Muse, Pi, and verification sandboxes; mask their credential paths from Cursor.
- Treat SDK state as sensitive because it may contain prompts, tool arguments/results, local paths, and conversation checkpoints. Do not expose it through the UI or commit it.

The official SDK requires Node.js 22.13 or later. DevFleet already requires Node.js 26 or later, so no platform downgrade is needed.

## Implementation slices

### Slice 1 — read-only SDK contract spike

- Add a standalone, non-production script under `runtime/cursor-worker`.
- Authenticate through `Cursor.auth.login()` outside managed work.
- Prove `Cursor.models.list()`, local `Agent.create()`, streaming, token usage, cancellation, and disposal against a pinned SDK version.
- Prove that the session `cwd` is app-owned and no repository is directly available.
- Prove that an empty settings-source list and strict tool allowlist prevent native shell, write, task, project MCP, user MCP, hooks, and subagents.
- Do not enable `RUN_REAL_CURSOR_TESTS` without explicit operator approval because it consumes Cursor quota.

Exit condition: a read-only child answers using only node-backed custom read/search tools, and attempts to use native or inherited tools fail closed.

### Slice 2 — selector, discovery, and read-only adapter

Likely affected paths:

- `src/PiCommandCenter.Application/Runtime/AgentModelSelector.cs`
- `src/PiCommandCenter.Application/Runtime/AgentRuntimeKinds.cs`
- `src/PiCommandCenter.Node/Runtime/Cursor/*`
- `src/PiCommandCenter.Node/Runtime/AgentRuntimeRegistry.cs`
- `src/PiCommandCenter.Node/RuntimeRouting/RuntimeModelDiscovery.cs`
- `src/PiCommandCenter.Node/NodeServiceCollectionExtensions.cs`
- `runtime/package.json` and lock file
- `runtime/cursor-worker/src/*`
- focused Application, Node, runtime, and end-to-end tests

Implement discovery, readiness, start/watch/send/cancel/snapshot, fixed login diagnostics, and read-only custom tools. Add `cursor` to architect/reviewer routes only when explicitly configured; do not change defaults yet.

Exit condition: the routing page offers only models returned by the authenticated official SDK, and a Cursor reviewer appears in the normalized agent tree without direct repository writes.

### Slice 3 — reserved writes

- Add reservation-aware custom mutation tools.
- Reuse node authorization, path policy, mutation, mail, verification, and audit gateways rather than implementing filesystem operations in TypeScript.
- Add process isolation and sibling-credential masks.
- Add Cursor as an optional implementer/verifier candidate only after safety tests pass.

Exit condition: two writers can edit disjoint scopes; a Cursor conflicting write is denied before mutation; handoff rotates the fencing token; the old token cannot write; shell and Git mutation remain unavailable.

### Slice 4 — operations and documentation

- Install the worker and pinned SDK under `~/.local/lib/devfleet`.
- Add an explicit local Cursor SDK login/status/logout operator command.
- Add `RUN_REAL_CURSOR_TESTS=1` to the documented opt-in contract lane, default off.
- Add systemd access only for the exact Cursor SDK auth and application state paths.
- Update the primary product, architecture, protocol, security, setup, configuration, and demonstration documentation.

Exit condition: install, login, discovery, one real read-only session, one reservation-gated write session, cancellation, restart recovery, and redaction checks are demonstrated without exposing a credential.

## Required tests

### Quota-free tests

- `cursor` is reserved and never falls through to Pi.
- A model id of `default` is rejected; concrete ids pass through unchanged as the SDK `model.id`.
- Invalid or duplicate model selectors fail closed.
- Model discovery validates, deduplicates, sorts, times out, and rejects malformed/oversized output.
- Missing auth maps to fixed blocked/input-required state without raw diagnostics.
- Protocol handles split frames, CRLF, malformed JSON, unknown messages, oversize frames, and continued synchronization.
- SDK event fixtures map to monotonic normalized events.
- Send/steer/follow-up disposition does not duplicate input.
- Cancellation has a bounded process-tree fallback.
- Cursor native shell/edit/write/task and inherited MCP/hooks/settings are absent from captured tool catalogs.
- Read-only starts reject write authorization.
- Reserved mutations require matching assignment, lease, fencing token, session, and path.
- Traversal, absolute paths, symlink escape, `.git/`, stale token, wrong session, and wrong assignment are denied.
- Runtime route fallback proceeds after Cursor unavailable/start failure.
- Cursor credentials, account identity, headers, and raw SDK errors never enter events, logs, DTOs, or snapshots.

### Opt-in real contract tests

Gate all tests with `RUN_REAL_CURSOR_TESTS=1` and record the SDK/CLI version:

- official SDK stored-login status;
- account-specific `Cursor.models.list()`;
- one minimal local run;
- custom tool invocation;
- streaming terminal event;
- token usage shape when present;
- cancellation;
- absence of disallowed tools and project/user settings sources.

Never run these in the default verification lane.

## Acceptance checklist

Cursor support is ready only when all are true:

- [ ] Only public, documented Cursor SDK/CLI/API surfaces are used.
- [ ] `cursor/*` selects a dedicated adapter and cannot fall through to Pi.
- [ ] Pi remains the root orchestrator.
- [ ] The control plane, browser, SQLite, events, and logs never receive Cursor credentials.
- [ ] The Cursor process cannot directly mutate the canonical workspace.
- [ ] Repository writes pass through node assignment, path, reservation, and fencing checks.
- [ ] Cursor native shell, edit/write, Git, web, subagent, and inherited project/user tools are absent.
- [ ] Model discovery and readiness use official SDK calls and fail closed.
- [ ] Cancellation and process death retain assignment ownership until quiescence is proved.
- [ ] Default tests are quota-free; real Cursor tests are explicit opt-in.
- [ ] `/statistics` preserves nullable usage semantics and `/usage` does not fabricate quota windows.
- [ ] `README.md`, `SPEC.md`, architecture, protocols, security, deployment, and examples agree.

## Open decisions before implementation

1. Whether the first production slice should be read-only only, or whether reserved-write support must ship in the same release.
2. Whether SDK login should use the default `~/.cursor/sdk/auth.json` or an SDK-provided `FileCredentialStore` under a DevFleet-specific provider-owned directory. Either choice must remain outside the control plane and isolated from sibling agents.
3. Whether Cursor model parameters require a richer route DTO. The initial proposal deliberately supports only base model ids.
4. Whether local Cursor SDK state should use the SDK SQLite store or `JsonlLocalAgentStore`. SQLite is less exposed to casual inspection; JSONL is easier to contract-test and recover. Both contain sensitive conversation data.
5. Whether Cursor's official ACP surface adds value after the SDK adapter exists. Do not maintain two adapters without a concrete capability need.

## Primary references

- [Cursor TypeScript SDK](https://cursor.com/docs/sdk/typescript)
- [Cursor CLI usage](https://cursor.com/docs/cli/using)
- [Cursor CLI ACP](https://cursor.com/docs/cli/acp)
- [Cursor Cloud Agents API](https://cursor.com/docs/cloud-agent/api/endpoints)
- [Cursor Terms of Service](https://cursor.com/terms-of-service)
- [Cursor staff statement about OMP/private endpoints](https://forum.cursor.com/t/does-using-oh-my-pi-s-cursor-provider-or-an-openai-compatible-proxy-to-the-same-endpoints-violate-cursor-s-tos/167778/5)
- Current DevFleet contracts: [`SPEC.md`](../SPEC.md), [`architecture.md`](architecture.md), [`protocols.md`](protocols.md), and [`security.md`](security.md)
