# Research: Pi SDK (`@earendil-works/pi-coding-agent`)

**Date researched:** 2026-09-04
**Primary sources:**

- Repo README: https://github.com/earendil-works/pi-mono
- Package README: https://raw.githubusercontent.com/earendil-works/pi-mono/main/packages/coding-agent/README.md
- SDK docs: https://raw.githubusercontent.com/earendil-works/pi-mono/main/packages/coding-agent/docs/sdk.md
- Package manifest: https://raw.githubusercontent.com/earendil-works/pi-mono/main/packages/coding-agent/package.json

---

## 1. Verified facts

### 1.1 Package and installation

- The SDK ships inside the main package `@earendil-works/pi-coding-agent`; no separate SDK package is required. Source: https://raw.githubusercontent.com/earendil-works/pi-mono/main/packages/coding-agent/docs/sdk.md ("The SDK is included in the main package. No separate installation needed.").
- Observed package version at research date: **0.85.0** (main branch manifest). Internal sibling packages (`pi-ai`, `pi-agent-core`, `pi-tui`, etc.) are version-ranged at `^0.85.0`, so the public API is still pre-1.0 and should be pinned exactly. Source: https://raw.githubusercontent.com/earendil-works/pi-mono/main/packages/coding-agent/package.json
- Declared Node engine: `node >= 22.19.0`. This satisfies the SPEC's Node 26 worker runtime. Source: same package.json `engines`.
- Install for CLI use: `npm install -g --ignore-scripts @earendil-works/pi-coding-agent` (upstream recommends `--ignore-scripts`; the package needs no lifecycle scripts). Source: package README Quick Start.
- The package publishes an `npm-shrinkwrap.json` pinning transitive dependencies, and upstream pins direct deps to exact versions. Source: repo README "Supply-chain hardening".

### 1.2 AgentSession creation

- Factory: `createAgentSession(options)` returns `{ session, extensionsResult, modelFallbackMessage? }`. Source: sdk.md "Core Concepts" / "Return Value".
- Minimal creation:
  ```ts
  import { createAgentSession, ModelRuntime, SessionManager } from "@earendil-works/pi-coding-agent";
  const modelRuntime = await ModelRuntime.create();
  const { session } = await createAgentSession({
    sessionManager: SessionManager.inMemory(),
    modelRuntime,
  });
  ```
  Source: sdk.md "Quick Start".
- Options relevant to this SPEC: `cwd`, `agentDir`, `model`, `thinkingLevel`, `modelRuntime`, `tools` (allowlist), `excludeTools`, `noTools: "all" | "builtin"`, `customTools`, `resourceLoader`, `sessionManager`, `settingsManager`. Source: sdk.md "Options Reference".
- `ModelRuntime.create()` restores cached model catalogs without network refresh by default; `create({ allowModelNetwork: true, modelRefreshTimeoutMs })` opts into a bounded refresh. `PI_OFFLINE` disables model network access. Source: sdk.md "Model".
- Auth resolution priority: runtime overrides (`setRuntimeApiKey`, not persisted) → stored `auth.json` (API keys or OAuth) → environment variables → custom-provider fallback. Source: sdk.md "API Keys and OAuth". This supports the SPEC's "provider authentication remains managed by the official CLI/runtime" requirement: the worker passes no credentials of its own for subscription providers.

### 1.3 Custom tool selection

- Built-in tool names: `read`, `bash`, `powershell` (Windows), `edit`, `write`, `grep`, `find`, `ls`. Defaults are `read`, `bash`, `edit`, `write`. Source: sdk.md "Tools"; package README CLI Reference.
- `tools: [...]` is an allowlist across built-in, extension, and custom tools; `excludeTools` removes names after the allowlist; `noTools: "builtin"` disables default built-ins while keeping custom/extension tools. Source: sdk.md "Tools".
- Custom tools are defined with `defineTool({ name, label, description, parameters: Type.Object({...}), execute })` (TypeBox schemas; `typebox` is a pinned dependency, version 1.3.7) and passed via `customTools: [...]`. When a `tools` allowlist is used, each custom tool name must be included in it. Source: sdk.md "Custom Tools"; package.json dependencies.
- Tool factories (`createReadTool`, `createEditTool`, `createCodingTools`, `createReadOnlyTools`, etc.) are exported for direct construction. Source: sdk.md "Exports".

### 1.4 Event subscription

- `session.subscribe(listener)` returns an unsubscribe function. Event types include: `message_start`, `message_update` (with `assistantMessageEvent` such as `text_delta`, `thinking_delta`), `message_end`, `tool_execution_start`, `tool_execution_update`, `tool_execution_end` (has `isError`), `agent_start`, `agent_end` (carries new `messages`), `turn_start`, `turn_end` (carries `message` and `toolResults`), `queue_update`, `compaction_start/end`, `auto_retry_start/end`, summarization retry events. Source: sdk.md "Events".
- Subscriptions attach to a specific `AgentSession`; after session replacement (see below) you must re-subscribe. Source: sdk.md "createAgentSessionRuntime() and AgentSessionRuntime".

### 1.5 Prompt / steer / follow-up / abort

- `session.prompt(text, options?)` resolves only after the full accepted run finishes (including retries). Source: sdk.md "Prompting and Message Queueing".
- `PromptOptions`: `expandPromptTemplates`, `images`, `streamingBehavior: "steer" | "followUp"`, `source`, `preflightResult(success)`. Calling `prompt()` while streaming **without** `streamingBehavior` throws. Source: sdk.md "Prompting and Message Queueing".
- `session.steer(text)` queues a steering message delivered after the current assistant turn finishes its tool calls; `session.followUp(text)` is delivered only after the agent stops entirely. Both expand file-based prompt templates but reject extension commands. Source: sdk.md "Prompting and Message Queueing"; package README "Message Queue".
- `session.abort(): Promise<void>` aborts the current operation. Source: sdk.md "AgentSession" interface.
- State access: `session.isStreaming`, `session.messages`, `session.agent.state`, `await session.agent.waitForIdle()`. Source: sdk.md "Agent and AgentState".

### 1.6 Persistence

- Sessions are JSONL files with a tree structure (`id`/`parentId` per entry) supporting in-place branching. Source: package README "Sessions"; docs/session-format.md (referenced).
- `SessionManager` factories: `SessionManager.inMemory(cwd?)` (no persistence), `SessionManager.create(cwd)` (new persistent session), `SessionManager.continueRecent(cwd)` (resume latest; result carries `modelFallbackMessage` if the model could not be restored), `SessionManager.open(path)` (specific file), and `SessionManager.inMemory(cwd, { id }, entries)` (restore from entries held outside the filesystem, e.g. a database). Source: sdk.md "Session Management".
- Listing: `SessionManager.list(cwd)`, `SessionManager.listAll(cwd)`. Tree API: `getEntries()`, `getTree()`, `getPath()`, `branch(entryId)`, `createBranchedSession(leafId)`, labels. Source: sdk.md "Session Management".
- Default storage: `~/.pi/agent/sessions/` organized by working directory; override with `agentDir`, `PI_CODING_AGENT_SESSION_DIR`, or `--session-dir`. Source: package README "Sessions" / "Environment Variables".
- `SettingsManager.inMemory(settings?)` avoids all settings file I/O — appropriate for a managed worker that must not read the user's global Pi settings. Source: sdk.md "Settings Management".

### 1.7 Session replacement (multi-session runtime)

- `createAgentSessionRuntime(factory, { cwd, agentDir, sessionManager })` returns an `AgentSessionRuntime` owning `newSession()`, `switchSession()`, `fork()`, clone, and `importFromJsonl()`. `runtime.session` changes after these; re-subscribe and re-call `bindExtensions(...)`. Source: sdk.md "createAgentSessionRuntime() and AgentSessionRuntime".
- For this SPEC (one worker process per session, sessions addressed by `sessionId` in NDJSON frames), a plain `createAgentSession` per Pi worker process is sufficient; `AgentSessionRuntime` is only needed if one process hosts multiple replaceable sessions. [INFERENCE from SPEC §24 process model + sdk.md.]

### 1.8 NDJSON / framing facts (cross-check with SPEC §24)

- Pi's own RPC mode (`pi --mode rpc`) uses strict LF-delimited JSONL; clients must split on `\n` only and must not use generic line readers (e.g. Node `readline`) that also split on Unicode separators. Source: package README "RPC Mode"; docs/rpc.md (referenced).
- This validates the SPEC's Pi-worker transport choice (NDJSON on stdout, logs on stderr, `protocolVersion: 1`), which is our own protocol and independent of Pi's RPC framing. [SPEC §24 is authoritative for our frames; Pi RPC docs only inform the framing pitfall.]

### 1.9 Permission / sandbox posture

- Pi has **no built-in permission system** restricting filesystem, process, network, or credential access; it runs with the launching process's permissions. Upstream recommends containerization/sandboxing for stronger boundaries. Source: repo README "Permissions & Containerization".
- Consequence for the SPEC: enforcement of reservations (SPEC §18) cannot rely on Pi permission hooks; it must be implemented by *not granting* the unrestricted built-in `write`/`edit`/`bash` tools and instead supplying `reserved_*` custom tools whose `execute` calls the node reservation authority. Tool allowlisting is a supported SDK surface (§1.3), so this is achievable without forking Pi.

---

## 2. Root vs child mode constraints (mapping to SPEC §5.2, §18, §24)

Verified SDK surfaces make both modes expressible:

| Mode | SPEC requirement | SDK mechanism (verified) |
|---|---|---|
| Root orchestrator | Read/search + orchestration tools only; direct file writes technically blocked; no shell | `tools: ["read", "grep", "find", "ls", <orchestration customs>]` with `customTools` for `create_plan`, `spawn_agent`, `get_agent_status`, `submit_completion`; omit `bash`, `edit`, `write`. Source: sdk.md "Tools"/"Custom Tools". |
| Pi child (write-capable) | No unrestricted `edit`/`write`/`bash`; mutations via `reserved_*` tools carrying lease ID + fencing token | `customTools: [reserved_read, reserved_write, reserved_edit, reserved_delete, reserved_move, reserve_files, expand_reservation, release_reservation, run_verification_command]` plus `tools` allowlist excluding built-in mutation tools; each tool's `execute` calls back to the node over the NDJSON channel before touching the filesystem. Source: sdk.md "Custom Tools"; SPEC §18.1. |
| Event normalization | Runtime events → one normalized event contract | `session.subscribe` event stream (§1.4) mapped onto `protocolVersion: 1` NDJSON frames on stdout; all logging to stderr. |
| Cancellation / human guidance | Abort and mid-run steering | `session.abort()`, `session.steer()`, `session.followUp()` (§1.5). |
| Durability | Session state survives restarts; control plane keeps authoritative history | `SessionManager.create/open/continueRecent` for Pi-native JSONL; the control plane remains authoritative for request/event history (SPEC §5.7) independent of Pi's files. |

---

## 3. Unresolved assumptions / open items

1. **Exact pinned version.** 0.85.0 was observed on `main` at research date. The Pi worker's `package.json` must pin an exact version (pre-1.0 API may break between minors); the concrete pin should be re-checked against the npm registry at scaffold time. (https://www.npmjs.com/package/@earendil-works/pi-coding-agent)
2. **Custom tool `execute` signature stability.** Documented as `execute(toolCallId, params)` returning `{ content, details }`; not yet contract-tested against the pinned version. Verify with a compile + smoke prompt before Milestone 0 sign-off.
3. **Per-session concurrent processes.** Whether multiple Pi worker processes may safely use the same `agentDir` (shared `auth.json`, `models-store.json`, session dir) concurrently is not documented. Assumption: yes for auth/models (read-mostly), but give each worker its own session file via `SessionManager.create(cwd)` and consider a dedicated `PI_CODING_AGENT_DIR` per node to avoid lock contention. Needs a smoke test.
4. **`tools` allowlist vs custom-tool name collision behavior** (e.g., a custom tool named like a built-in) is undocumented; avoid name reuse (`reserved_*` prefix already does this).
5. **Event volume/`tool_execution_update` streaming guarantees** for long-running `reserved_*` tools (which round-trip to the node) are undocumented; assume updates are best-effort and treat `tool_execution_end` as authoritative.
6. **Node 26 specifically**: engines declare `>=22.19.0`; Node 26 is assumed compatible but not explicitly verified upstream.
7. **Steering delivery ordering guarantees** (`steeringMode`/`followUpMode` "one-at-a-time" vs "all") are settings-driven in the CLI; the SDK's `SettingsManager.inMemory` defaults for these are not documented — verify behavior if the node sends concurrent steer messages.
