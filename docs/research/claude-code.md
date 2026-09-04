# Claude Code — External Runtime Contract Research

**Researched:** 2026-09-04. Sources: Anthropic official Claude Code docs only (code.claude.com/docs, the canonical host for docs.anthropic.com/en/docs/claude-code). Behavior statements below carry the version gates the docs state; where no gate is stated, treat the behavior as current-as-of-research but unversioned.

## 1. Headless / programmatic execution

Source: <https://code.claude.com/docs/en/headless> (mirror: <https://docs.anthropic.com/en/docs/claude-code/headless>)

- Non-interactive mode is `claude -p "<prompt>"` (or `--print`). Exit code 0 on success, non-zero on failure; in-run failures (e.g. missing auth) are printed as the result on stdout, invalid flags go to stderr before the run starts.
- `-p` rejects `--bg`, and rejects `--cloud` with a task description (a session ID with `--cloud` + `-p` queues a message instead).
- Piped stdin is capped at 10 MB; over the cap → clear error + non-zero exit.
- **Bare mode** (`--bare`): skips auto-discovery of hooks, skills, commands, subagents, plugins, MCP servers, auto memory, CLAUDE.md. Critical credential caveat: *bare mode never reads OAuth credentials or the system keychain* — requires `ANTHROPIC_API_KEY` or an `apiKeyHelper` in `--settings`. Docs note `--bare` "will become the default for `-p` in a future release" (unresolved migration risk; see §8).
- **Structured output:** `--output-format text|json|stream-json`.
  - `json`: single payload with `result`, `session_id`, usage, `total_cost_usd`; with `--json-schema '<JSON Schema>'`, validated output lands in `structured_output`. Invalid schema → exit with `Error: --json-schema is not a valid JSON Schema` (before v2.1.205 invalid schemas were silently ignored).
  - `stream-json`: newline-delimited JSON events on stdout; for `-p` requires `--verbose`; add `--include-partial-messages` for token-level deltas. Final line is a `result` message with text, cost, session metadata. Slow consumers: exit waits for queued output to drain, capped at 30 s (was ~2 s before v2.1.214).
  - First stream event is normally `system/init` (model, tools, MCP servers, plugins, `plugin_errors`, `mcp_server_errors`; optional `capabilities` array for feature detection, e.g. `interrupt_receipt_v1`, `interrupt_cancel_queued_v1` — requires v2.1.205+).
  - API retries emit `system/api_retry` events (`attempt`, `max_retries`, `retry_delay_ms`, `error_status`, `error` category, `session_id`).
  - Subagent messages carry `parent_tool_use_id`; only `tool_use`/`tool_result` blocks forwarded unless `--forward-subagent-text` / `CLAUDE_CODE_FORWARD_SUBAGENT_TEXT` (v2.1.211+; nested subagents in stream v2.1.219+).
- **Unattended permission handling:** `--permission-prompts none` (requires v2.1.259+; earlier versions reject the flag) denies anything that would prompt unless a `PermissionRequest` hook allows it, removes interaction tools (`AskUserQuestion`), and cancels unanswered MCP elicitations. Denials surface as `permission_denied` system messages and in the result's `permission_denials`.
- Background Bash tasks are killed ~5 s after the result is delivered and stdin closes; background subagents are awaited (idle cap 10 min from v2.1.182, `CLAUDE_CODE_PRINT_BG_WAIT_CEILING_MS`).

## 2. Session IDs, resume, cancellation

Source: <https://code.claude.com/docs/en/sessions>

- Session ID comes from the `--output-format json` result (`session_id`) or the `stream-json` `system/init` event. Transcripts live at `~/.claude/projects/<project>/<session-id>.jsonl` (project = cwd path with non-alphanumerics replaced by `-`, truncated to 200 chars + hash). **The JSONL transcript format is internal and "changes between versions" — never parse it; use `-p --output-format json/stream-json`, `--resume`, or hook `transcript_path` consumers.**
- `claude -p` / SDK sessions are excluded from the picker and from plain `claude --continue`, but `claude -p --continue` includes them; resume any session by ID: `claude -p --resume <session-id> "follow-up"`. Resume searches the current project, its worktrees, then every other project (exactly-one-match rule; cross-project search requires v2.1.223+).
- Resume restores conversation, model, agent, and (terminal path only) permission mode. **`claude -p --resume` does NOT restore the stored permission mode** — it starts in the mode a fresh `-p` run would (default = `default`/Manual). Re-pass `--permission-mode` / `--dangerously-skip-permissions`. Flags not restored on resume: `--mcp-config`, `--settings`, `--plugin-dir`, `--fallback-model`, `--add-dir` — re-pass all of them.
- `--fork-session` / `/branch` copies the transcript under a new session ID. `--no-session-persistence` suppresses transcript writes for one `-p` run. Retention: `cleanupPeriodDays` (default 30 days).
- **Cancellation:** SIGTERM → exit code 143, in-progress turn left unfinished, running Bash process trees terminated, only `SessionEnd` hooks run; on `--resume` the unfinished turn continues. To end the turn cleanly first, send SIGINT (or SDK `interrupt()`) before stopping. Permission prompts pending at SIGTERM are left unanswered.

## 3. Permission modes

Source: <https://code.claude.com/docs/en/permission-modes>

Modes: `default` (labeled **Manual**; `manual` accepted as alias, v2.1.200+), `acceptEdits`, `plan`, `auto`, `dontAsk`, `bypassPermissions`.

- Start-mode resolution order: `--permission-mode`/`--dangerously-skip-permissions` flag → `permissions.defaultMode` in settings → built-in default. Built-in default for `claude -p` and the Agent SDK is always `default` (Manual), regardless of plan.
- `dontAsk`: denies everything not in `permissions.allow` or the built-in read-only command set — the locked-down CI mode.
- `acceptEdits`: auto-approves file edits plus `mkdir`, `touch`, `rm`, `rmdir`, `mv`, `cp`, `sed` on in-scope paths only.
- `bypassPermissions` (= `--dangerously-skip-permissions`): skips prompts; intended for containers/VMs only. **Deny rules still apply in every mode, including `bypassPermissions`** — this is the anchor for blocking unrestricted Bash.
- Never auto-approved in any mode: explicit ask rules, org-`ask` connector tools, `AskUserQuestion`/`requiresUserInteraction` MCP tools, `rm`/`rmdir` on critical paths.
- Project `.claude/settings.json` `defaultMode` values `auto` and `bypassPermissions` do not take effect (fallback rules apply).

## 4. Settings isolation and precedence

Source: <https://code.claude.com/docs/en/settings>

Precedence, highest first: **managed settings → `--settings` (command line) → `.claude/settings.local.json` → `.claude/settings.json` → `~/.claude/settings.json`**. A key set at a higher level overrides the same key lower down. Hook *entries* merge across levels rather than replacing (deny rules accumulate).

- `--settings <file-or-json>` lets a host inject a reserved settings document per launch that outranks project and user settings.
- `CLAUDE_CONFIG_DIR` relocates all `~/.claude` state; `CLAUDE_CODE_PROJECT_DIR_NAME` (v2.1.234+, requires `CLAUDE_CONFIG_DIR`) pins the per-project transcript/memory directory — the supported pattern for a host giving each managed session its own config root. Caveat: a separate config dir means separate OAuth state; to **reuse the user's existing provider-managed credentials, keep the default `~/.claude`** and isolate only via `--settings`.
- Credential facts: normal (non-bare) mode reads OAuth credentials / system keychain as usual; `--bare` never reads them (needs `ANTHROPIC_API_KEY` or `apiKeyHelper`). `claude auth status` exits 0/1 for scripting login checks.

## 5. PreToolUse / PostToolUse hook schemas and blocking semantics

Source: <https://code.claude.com/docs/en/hooks>

Configuration: `hooks.<Event>[ { matcher, hooks: [ { type: "command", command, args?, if?, timeout?, async? } ] } ]` in settings JSON. Matcher: `*`/empty = all; plain names or `|`/`,` lists = exact match; anything else = unanchored JS regex (e.g. `Bash|PowerShell`, `mcp__memory__.*`). Handlers receive JSON on stdin; hooks also fire inside subagents (input carries `agent_id`, `agent_type`).

**Common input fields:** `session_id`, `transcript_path`, `cwd`, `permission_mode` (`"default"`, `"plan"`, `"acceptEdits"`, `"auto"`, `"dontAsk"`, `"bypassPermissions"`), `hook_event_name`, `prompt_id` (v2.1.196+), `effort` (tool-context events).

**PreToolUse** — fires before tool execution; can block. Input adds `tool_name`, `tool_input`, `tool_use_id`. `tool_input.file_path` for Write/Edit/Read is always absolute with native separators (normalize `\\` → `/` before matching; `~`/relative already expanded). Never fires for `EndConversation` or for `@`-referenced files.

Decision output (`hookSpecificOutput`, requires `hookEventName: "PreToolUse"`):
- `permissionDecision`: `"allow"` | `"deny"` | `"ask"` | `"defer"`. Multiple hooks: precedence `deny` > `defer` > `ask` > `allow`. Settings deny/ask rules are still evaluated regardless of a hook `"allow"`.
- `permissionDecisionReason`: shown to user for allow/ask; shown to **Claude** for deny.
- `updatedInput`: replaces the entire tool input before execution (permission rules re-evaluated against the modified input).
- `additionalContext`: string added to Claude's context with the tool result.
- `AskUserQuestion`/`ExitPlanMode` need `"allow"` + `updatedInput` to run non-interactively. MCP tools marked `requiresUserInteraction` (v2.1.199+) cannot be hook-approved.
- `"defer"` (only honored with `-p`): process exits with `stop_reason: "tool_deferred"`, result carries `deferred_tool_use {id, name, input}`; resume with `claude -p --resume <session-id>`, hook fires again, return `"allow"` + `updatedInput`. Single tool call per turn only; no timeout; `stop_reason: "tool_deferred_unavailable"` if the tool vanishes before resume.
- Deprecated: top-level `decision`/`reason` for PreToolUse (`"approve"`→`"allow"`, `"block"`→`"deny"`).

**PostToolUse** — fires after successful execution; **cannot block** (tool already ran). Input adds `tool_response` and optional `duration_ms`. Output: top-level `decision: "block"` + `reason` (feedback shown to Claude next to the result), `additionalContext`, `updatedToolOutput` (replaces what Claude sees — must match the tool's output shape, e.g. Bash = `{stdout, stderr, interrupted, isImage}`; real-world effects and telemetry already happened). Failure counterpart: `PostToolUseFailure`.

**Exit-code semantics (all command hooks):**
- Exit 0: success. Stdout parsed as JSON decision only if it starts with `{` and ends with `}`; otherwise treated as plain text (plain text is added to Claude's context only for `UserPromptSubmit`, `UserPromptExpansion`, `SessionStart`, `PostModelSwitch`). Stderr on exit 0 goes to the debug log only.
- **Exit 2: the only exit code that blocks by itself.** On PreToolUse it blocks the tool call and routes like `"deny"` (stderr = reason shown to Claude). JSON can't override exit 2 (even `"allow"`). On PostToolUse exit 2 shows stderr to Claude but the tool already ran.
- Any other non-zero exit (including 1): **non-blocking** — action proceeds, transcript shows `<hook name> hook error`. A missing/non-executable hook script (exit 127) is also non-blocking — *a policy hook with a bad path silently stops gating*.
- Hook timeout: for PreToolUse command/http/mcp_tool hooks, a timeout does **not** block — the call proceeds through normal permissions (don't rely on a stalled hook as a gate). SDK callback hooks are the opposite: timeout blocks.
- Universal JSON fields: `continue: false` + `stopReason` stops Claude entirely (takes precedence over event decisions); `systemMessage` warns the user; `suppressOutput` is accepted but inert. Hook output strings capped at 10,000 chars.

## 6. Recommended safe reserved-write launch contract (derived)

To preserve provider-managed credentials and block unrestricted Bash for a managed `-p` session:

1. **Do not** pass `--bare` (it bypasses OAuth/keychain credentials) and **do not** relocate `CLAUDE_CONFIG_DIR` (credentials live under the default `~/.claude`). Isolate policy instead via `--settings <file>` with a host-owned, reserved-written settings file — `--settings` outranks project and user settings.
2. In that settings file set `permissions.deny` rules covering unrestricted shell use (deny rules apply in every mode including `bypassPermissions`) and, where a hard allowlist is wanted, launch with `--permission-mode dontAsk` plus explicit `--allowedTools`/`permissions.allow`.
3. Belt-and-braces enforcement: a `PreToolUse` hook (matcher `Bash|PowerShell`) that exits **2** (or returns `permissionDecision: "deny"`) for disallowed commands — exit 1 does not block; verify the hook path is executable because a failed spawn fails open.
4. Launch: `claude -p "<prompt>" --output-format stream-json --verbose --settings <reserved-file> [--permission-mode dontAsk] [--max-turns N]`; capture `session_id` from `system/init`; cancel with SIGINT then SIGTERM (exit 143 expected); resume with `claude -p --resume <session-id>` re-passing `--settings` and permission flags (resume does not restore them).

## 7. Observed version/compatibility assumptions

- Docs reference builds up to at least v2.1.259 (`--permission-prompts none`). Pin and record the installed `claude` version at runtime; feature-detect via `system/init.capabilities` (v2.1.205+) rather than version-string compares where possible.
- Version gates that matter to this contract: `--permission-prompts none` ≥ v2.1.259; plan-mode restore on `-p --resume` ≥ v2.1.246; `system/init.capabilities` ≥ v2.1.205; `--json-schema` strict validation ≥ v2.1.205; hook `if` conditions and current exit-code/JSON parsing rules assume a recent v2.1.x (several sub-behaviors changed at v2.1.214/v2.1.248); `manual` alias ≥ v2.1.200.

## 8. Unresolved version risks

- **`--bare` will become the default for `-p` in a future release** (documented intent, no version given). When that lands, credential behavior flips: hosts relying on the user's OAuth login must explicitly opt out of bare mode or supply `ANTHROPIC_API_KEY`/`apiKeyHelper`. Monitor release notes.
- Transcript JSONL format is explicitly internal and unstable across versions — never depend on it.
- The exact stream-json event vocabulary (e.g. `permission_denials`, `system/api_retry` quiet-retry behavior at v2.1.246+) evolves per release; consumers must ignore unknown event types/fields.
- Docs do not state a stability guarantee for `hookSpecificOutput` field names across major versions; PreToolUse already migrated once (`decision`→`permissionDecision`, deprecated).
- No documented guarantee on where OAuth credentials are stored on disk (keychain vs file) — treat credential storage as opaque; the contract only guarantees *non-bare mode reads provider-managed credentials*.

## Sources

- <https://code.claude.com/docs/en/headless> — programmatic/headless mode, output formats, bare mode, SIGTERM behavior
- <https://code.claude.com/docs/en/cli-reference> — CLI commands and flags
- <https://code.claude.com/docs/en/hooks> — hook events, matcher/config schema, PreToolUse/PostToolUse input & decision schemas, exit codes, JSON output
- <https://code.claude.com/docs/en/sessions> — session IDs, resume/fork, transcript storage, permission mode on resume
- <https://code.claude.com/docs/en/permission-modes> — permission modes, starting-mode resolution, never-auto-approved actions
- <https://code.claude.com/docs/en/settings> — settings files and precedence
