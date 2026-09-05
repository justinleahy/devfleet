# Research: subscription usage / remaining quota reporting

**Date researched:** 2026-09-05 (public-surface survey and production decision).

**Decision:** Pi remains DevFleet's production orchestrator, but the usage
snapshot is composite. The bundled Pi-SDK sidecar reports providers supported by
Pi `ModelRuntime`; dedicated provider-native readers restore Anthropic Claude and
Google Antigravity quota cards. Oh My Pi is not a command, package, library,
credential store, or deployment dependency.

None of `pi`, `claude`, or `agy` exposes a documented machine-readable quota API.
The operator accepted the bounded, fail-closed first-party surfaces below:
Claude Code's private OAuth usage endpoint and the official Antigravity CLI's
documented headless `/usage` command with its pinned text grammar. No model prompt
is sent and the UI refreshes manually only.

---

## Contract target (normalized UI, not bound to repo code)

A host UI should present, per provider (no runtime-profile field):

- `provider` (exact upstream id)
- `status`: `available` | `unavailable` | `error`
- `authenticated`
- optional `planLabel` (non-secret)
- optional `version` (informational, non-secret)
- one or more named **limit windows** with percent-used **or** percent-remaining and optional reset time
- `observedAt` (ISO-8601)
- `source` (stable provider-native label; never raw command output or a URL containing data)
- `diagnostic` (lowercase snake_case `^[a-z0-9_]{1,40}$` only; never raw HTTP, secrets, PII, or error bodies)

Unknown, truncated, or unparseable output closes only the affected source. Never
fabricate remaining quota or carry over a previous reading.

Wire DTOs stay unchanged (`Provider`, `Status`, `Authenticated`, `PlanLabel`, `Version`, `Windows`, `ObservedAt`, `Source`, `Diagnostic`). Proposed JSON shape (host-owned):

```json
{
  "provider": "anthropic",
  "status": "available",
  "authenticated": true,
  "planLabel": "Max",
  "version": null,
  "windows": [
    {
      "name": "session",
      "percentUsed": 42,
      "percentRemaining": null,
      "resetsAt": "2026-09-05T18:00:00Z"
    }
  ],
  "observedAt": "2026-09-05T12:00:00Z",
  "source": "GET https://api.anthropic.com/api/oauth/usage",
  "diagnostic": null
}
```

If `windows` cannot be parsed with both a name and at least one of `percentUsed`/`percentRemaining` from a **pinned** payload, omit windows and set `status` to `unavailable` or `error`.

---

## What DevFleet ships: Pi-orchestrated composite usage

`IRuntimeSubscriptionUsageProbe.GetAsync` takes one observation time and starts
the Pi sidecar plus every ordered `ISupplementalSubscriptionUsageSource`
concurrently. Sidecar rows retain report order. A non-null supplement replaces
the row with the same exact provider id or appends in registration order. A
supplement that is unconfigured returns null; an exception is isolated from the
sidecar and sibling supplements.

### Pi ModelRuntime sidecar

The node runs `SubscriptionUsage:NodeExecutable` (Compose: `node`) with
`SubscriptionUsage:ScriptPath` (`/app/runtime/pi-worker/src/usage.ts`) once,
without a shell and with closed stdin, a wall deadline, bounded combined output,
and process-tree containment.

The sidecar JSON allowlist is:

| Report id | Product |
|---|---|
| `openai-codex` | OpenAI Codex / ChatGPT (not Pi quota) |
| `anthropic` | Anthropic Claude |
| `kimi-code` | Kimi |
| `zai` | Z.AI |
| `xai-oauth` | xAI |
| `opencode-go` | OpenCode |

`google-antigravity` is intentionally **not** accepted in sidecar JSON. It
becomes a valid final DTO id only through the registered native source. An
Anthropic native card replaces a same-id sidecar row.

The sidecar uses public Pi `ModelRuntime.create()` / `checkAuth()` / `getAuth()`
against Pi-managed state. DevFleet does not parse or write Pi `auth.json`, and
there is no Pi or OMP quota command.

### Anthropic Claude supplemental source

Provider id: `anthropic`.

The reader opens `SubscriptionUsage:ClaudeCredentialPath` (default
`~/.claude/.credentials.json`) as an owner-private regular file under a 256 KiB
cap and reads only the `claudeAiOauth` credential needed for the request. An
absent credential means unconfigured and returns null. A configured but unsafe,
unreadable, or malformed store returns a closed card with a stable diagnostic.

Usage is `GET https://api.anthropic.com/api/oauth/usage`. Near-expiry credentials,
or one 401, use the exact token origin
`https://platform.claude.com/v1/oauth/token`; the usage request is retried at
most once. Redirects are disabled, the operation is bounded to 10 seconds, and
response bodies are capped at 64 KiB. Token rotation re-reads the credential and
compare-and-swaps the refresh token before an atomic owner-only replacement, so
a concurrent Claude Code rotation wins safely. Only credential fields are
updated; unrelated JSON is preserved.

Known root windows and recognized weekly scoped rows map provider percentage
points directly to `PercentUsed` on the 0–100 scale; remaining is
`100 - used`. Any unknown required shape, unsafe label, invalid reset,
out-of-range/non-finite percent, incoherent pair, duplicate, or ninth window
closes the whole Anthropic card. Plan labels are non-secret metadata only.

This is a private first-party endpoint used by the official Claude Code client,
not a published Anthropic API. The stability risk is accepted and schema drift
must fail closed. DevFleet never runs or scrapes `claude -p /usage`.

### Google Antigravity supplemental source

Provider id: `google-antigravity`.

The reader uses `Antigravity:Executable` and runs the official binary with exact
argv: first `agy --version`, then
`agy -p /usage --print-timeout 8s`. Missing or blank executable means
unconfigured and returns null. The shared process runner supplies no shell,
closed stdin, bounded output, a wall deadline, and process-tree containment.

Only the pinned agy 1.1.27 four-column TSV grammar is accepted: safe model group;
`Weekly Limit Remaining` or `Five Hour Limit Remaining`; whole remaining percent
0–100; RFC3339 reset. Rows become `<group> weekly` or `<group> five-hour`,
`PercentRemaining` is read directly, and `PercentUsed = 100 - remaining`.
Empty, malformed, duplicated, ANSI/TUI, truncated, timed-out, non-zero, or ninth
rows close the card with a stable `process_*` diagnostic. Raw stdout and stderr
never cross the source boundary. Credits and internal Google quota RPCs are not
read.

`agy -p /usage` is an official documented headless command, but its text schema
is not documented. The pinned grammar and manual-refresh-only policy contain
that accepted stability and quota-cost risk.

### Merge and fail-closed rules

- `available` requires one to eight coherently named windows.
- Percentages must be finite and in 0–100; a present used/remaining pair must
  sum to 100 within the source's documented rounding tolerance.
- Reset times are parsed, never estimated.
- No last-known snapshots, interpolation, partial provider results, or session
  token/cost substitution.
- Source failures are isolated. Caller cancellation still cancels the composite
  operation.
- Diagnostics are fixed lowercase tokens. Credential contents, provider bodies,
  account/user ids, email/organization data, and raw process output are never
  logged, returned, or sent over SignalR.

### Deployment

Only the node sees provider state. Compose keeps these native mounts and
executables:

- `${HOME}/.pi/agent` → `/home/node/.pi/agent` for Pi ModelRuntime.
- `${HOME}/.claude` and `${HOME}/.claude.json` under `/home/node`; the explicit
  Claude usage path is `/home/node/.claude/.credentials.json`.
- `${HOME}/.gemini` → `/home/node/.gemini`, read by the mounted official
  `/usr/local/bin/agy`.

There is no OMP binary, package, `~/.omp` mount, or OMP credential path.

### Decision record

The operator accepted the Claude Code private first-party OAuth endpoint and
the official agy headless report to restore real Anthropic and Google
Antigravity cards while keeping Pi as the orchestrator. Consequences:

1. Manual refresh only; no background quota polling.
2. Exact HTTPS origins, no redirects, bounded files/bodies/processes.
3. Private or pinned schema drift produces a closed card, never stale or guessed
   numbers.
4. Provider-native credentials stay node-local and are never exposed through
   diagnostics or DTOs.
5. Cursor and Muse remain outside the subscription-usage contract.
6. OMP remains absent from code and deployment.

Session-level token or cost data may be shown only if labeled **session usage**,
never as remaining subscription quota.
