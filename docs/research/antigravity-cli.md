# Google Antigravity `agy` CLI — Runtime Contract Research

Date researched: 2026-09-04
Primary sources (all fetched and read in full on that date):

- Getting Started: https://antigravity.google/docs/cli/getting-started
- Headless mode: https://antigravity.google/docs/cli/headless
- Installation & Auth: https://antigravity.google/docs/cli/install
- CLI Reference: https://antigravity.google/docs/cli/reference

This note separates **verified contract facts** (stated on the official pages above) from **unavailable or ambiguous behavior**.

---

## Verified contract facts

### Executable name and install

- Executable is **`agy`**, installed by the official installer to `~/.local/bin/agy` (macOS/Linux) or `C:\Users\<username>\AppData\Local\agy\bin` (Windows).
- Bare `agy` with no arguments launches the interactive TUI.

### Headless invocation

- Headless mode ("print mode"): `agy -p "<prompt>"` (aliases `--print`, `--prompt`). Runs a single prompt non-interactively, writes the response to **stdout**, writes diagnostics (errors, auth prompts, progress, permission notices) to **stderr**, then exits.
- Default response timeout is **5 minutes**, adjustable with `--print-timeout` (e.g. `--print-timeout 15m`).
- Exit codes: `0` on success (including soft-denied tool runs). Non-zero on failure to produce a response; stdin-streaming sessions additionally use `1` (malformed input / invalid JSON / non-text block) and `2` (`control_request`/`control_response` or CLI-handled slash commands). The failure also appears in the `status`/`error` fields in `json`/`stream-json` output modes.

### Streaming JSON framing (output)

- `--output-format stream-json` emits **NDJSON on stdout**: one JSON object per line. Each line is an event object with an `event` discriminator field:
  - `init` — exactly once at stream start; payload key `init` with `cwd`, `tools` (string[]), `permission_mode`, and optional `model`, `agent`, `json_schema`.
  - `step_update` — per step transition or text delta; payload key `step_update` with `conversation_id`, `step_index` (zero-based), `state` (`ACTIVE`|`DONE`), `step_type` (observed: `user_input`, `agent_response`, `tool`, `checkpoint`), plus optional `tool_name`, `text_delta`, `duration_seconds`, `usage`, `tool_info` (`name`, `parameters`, `output`, optional `error` with `type`/`message`), `subagent_info` (`subagents` with `type_name`, `role`, `conversation_id`, `log_uri`, `workspace_uris`).
  - `result` — exactly once per turn at the end; same shape as the single-shot `json` envelope: `conversation_id`, `status`, `response`, optional `error`, `duration_seconds`, `num_turns`, optional `structured_output`/`json_schema`, `usage` (`input_tokens`, `output_tokens`, `thinking_tokens`, `cache_read_tokens`, `total_tokens`).
- `agent_response` steps stream partial `text_delta` fragments in one or more `ACTIVE` events before the final `DONE`; short responses may arrive in a single `DONE`.
- `--output-format json` emits a single one-line JSON envelope on completion. `--output-format text` (default) emits raw response text.
- `--json-schema` accepts a schema string, a path to a `.json` file, or a primitive type name; the parsed value appears in `structured_output`.

### Terminal `status` values

`SUCCESS`, `ERROR`, `CANCELED`, `INTERRUPTED` (e.g. `SIGINT`), `INVALID`, `WAITING`, `RUNNING`.

### Conversation/session identifiers

- Each run/session has a `conversation_id` (UUID string) present on the `init`, `step_update`, and `result` events.
- Cross-process resume: `--continue` (`-c`) resumes the most recent conversation; `--conversation <id>` resumes a specific one. Each such invocation is a **new process**.

### Multi-turn stdin behavior

- `--input-format stream-json` keeps **one process** alive for a continuous conversation, reading NDJSON prompts from stdin. It **requires `--output-format stream-json`**.
- Input message shape: `{"event":"user","message":{"content":"..."}}` — `content` is a string or a list of blocks; `text` is the only supported block type (`{"type":"text","text":"..."}`).
- Per turn: `init` is sent **once** at session start; each turn emits `step_update` events then exactly **one `result`** event. A single `conversation_id` tracks the whole session.
- Field scope: `response` is per-turn; `num_turns`, `usage`, `duration_seconds` are **cumulative over the session**.
- Read `result` for the current prompt before writing the next; the process stays open until stdin is closed, then exits (clean sessions exit `0`, and a final `result` is still delivered if the pipe is closed immediately after a prompt).
- Input validation: unrecognized `event` names are **skipped with a stderr warning** (forward-compatible); missing `event` field, invalid JSON line, or non-`text` block → `ERROR` result, session ends, exit `1`; `control_request`/`control_response` events and CLI-handled slash commands (e.g. `/model`) → `ERROR` result, session ends, exit `2`. A prompt passed via `-p` in streaming mode is silently dropped.
- Slash commands answered by the CLI itself (`/model`, `/usage`) must be run as standalone `agy -p /model` invocations; they produce a text report, not an event stream.

### Permissions in headless mode

- No interactive approval exists headlessly. Tools needing approval are **soft-denied** by policy: the run continues, exits `0`, and prints a stderr notice. Workspace file read/write is auto-allowed; shell commands default to Ask and are soft-denied unless granted.
- Pre-grant via `permissions.allow` rules in `~/.gemini/antigravity-cli/settings.json` (e.g. `"command(git)"`, `"write_file(src/)"`), or pass `--dangerously-skip-permissions` (which makes `init.permission_mode` = `always-proceed`; default is `request-review`).
- `--sandbox` enables terminal sandbox restrictions for the run.

### Model/agent selection and discovery

- `agy models` lists available model slugs; `agy agents` lists agents. Pin per run with `--model <slug>`, `--effort low|medium|high`, `--agent <name>`.
- Headless mode does **not** fall back on unknown `--model`: exits non-zero with an `ERROR` status envelope.

### Authentication (provider-managed)

- Default: Google account sign-in. Local runs use the OS keyring (Keychain / Secret Service / Windows Credential Manager) for silent auth, else a browser flow; over SSH, a manual URL + authorization-code loop is printed.
- Headless runs use **cached credentials**; a non-interactive unauthenticated run exits with an `authentication required` error instead of hanging.
- API-key mode (suited to CI): set `"modelProvider": "gemini"` in `~/.gemini/antigravity-cli/settings.json` **and** export `GEMINI_API_KEY` (the env var alone has no effect; `.env` files are not loaded; `GOOGLE_API_KEY` is ignored). Custom endpoint: `GOOGLE_GEMINI_BASE_URL`.
- `/logout` purges keyring tokens (no-op under API-key mode).

### Cancellation

- Headless: `SIGINT` terminates the run; the terminal status is reported as `INTERRUPTED` (`CANCELED` also exists as a status value).
- Interactive TUI: `Esc` halts active streams; `Ctrl+C` exits (with confirmation if the agent is working).

---

## Unavailable or ambiguous behavior

- **Protocol version handshake: none documented.** The official docs describe no `protocolVersion`, handshake message, or version-negotiation frame in the NDJSON stream. The stream's forward-compatibility story is limited to "unrecognized `event` names are skipped with a warning." DevFleet's runtime `protocolVersion: 1` must therefore be **our own wrapper contract**, not an `agy`-native field.
- **Version discovery:** `agy --version` is referenced by install/usage material and third-party guides as printing the installed version, but the four official pages fetched above do not document a version flag, its output format, or a machine-readable version command. Treat `agy --version` as the conventional probe (text output, format unspecified); pin and validate the exact installed version in CI rather than parsing it.
- **`agy version` subcommand:** reportedly interactive and TTY-dependent per third-party sources; not documented on official pages. Do not use for automation.
- **Cancellation mid-turn via stdin:** no documented input event to cancel an in-flight turn in `--input-format stream-json` sessions (`control_request`/`control_response` are explicitly rejected, exit `2`). The only documented cancel surface is process-level signals (`SIGINT`).
- **Full `step_type` enumeration:** docs say "observed values include" `user_input`, `agent_response`, `tool`, `checkpoint` — explicitly non-exhaustive. Consumers must tolerate unknown `step_type` and `state` values.
- **Event ordering guarantees beyond init→step_update*→result:** not formally specified (e.g. whether `checkpoint` steps are stable, interleaving of subagent events).
- **Non-TTY stdout quirks:** third-party reports claim `agy -p` can emit empty stdout in some piped/CI contexts despite exiting `0`. Not covered by official docs; verify against the pinned version in our environment before relying on output capture.
- **Rate limits/quota errors in stream shape:** `/usage` and `/credits` exist in the TUI, but no documented NDJSON error taxonomy distinguishes quota exhaustion from other `ERROR` results beyond the free-text `error` field.
