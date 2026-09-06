# Security

Single local administrator. Loopback by default. No anonymous web UI or API (except loopback `/health`). Provider OAuth stays in official CLIs. Plain HTTP is allowed only for a positively verified loopback endpoint; every non-loopback node connection uses HTTPS/WSS.

## Threat model (PoC)

| Threat | Control |
|---|---|
| Anonymous browser on the workstation | Cookie auth + antiforgery; no anonymous pages except `/health` on loopback |
| CSRF against login/logout and mutating APIs | `UseAntiforgery`; login is an antiforgery form; logout is POST |
| Node impersonation | Distinct manually provisioned credential per node; authentication creates a principal containing the stable `NodeId`; hub code derives identity from the connection and rejects metadata mismatches |
| Credential leak in logs/API/transport payloads | Node secrets are confined to the HTTP authentication layer and are never logged, returned, or placed in SignalR DTOs; provider credentials never leave the node or enter SQLite or agent payloads |
| Path traversal / symlink escape | Node-local `Projects:ApprovedRoots`; bounded node-owned directory browse (no control-plane filesystem reads, no symlink entries, no parent above a root); revisioned canonical-path and Git validation by the node that owns the path namespace; `.git/` cannot be reserved |
| Prompt injection widening permissions | Runtime executables, profiles, hooks, project roots, and completion gates are configuration/supervisor-owned |
| Reservation bypass via Claude tools | Host-owned `--settings`; `--setting-sources ""` disables repository/user hook discovery; PreToolUse/PostToolUse gate on loopback |
| Shell escape for writers | Pi children lack unrestricted `edit`/`write`/`bash`; mutations go through reserved tools + `AuthorizeMutation` |
| Stale or duplicate writer after disconnect | A nonterminal or recovery assignment retains ownership and consumes the project write slot; lease/heartbeat expiry never authorizes reassignment; fencing tokens must match |
| Binding to the LAN accidentally | Kestrel and node URLs default to `127.0.0.1`; plaintext remote node URLs, TLS downgrade redirects, and certificate-validation bypasses are rejected |

Out of scope: multi-user tenancy, public internet hosting, full VM sandboxing.

## Authentication options

Bound from configuration (example: `deploy/appsettings.ControlPlane.example.json`).

### Admin (cookie)

| Key | Meaning |
|---|---|
| `Admin:Username` | Single local administrator (setup writes `admin`) |
| `Admin:PasswordFile` | Path to a `0600` **password hash** file. Created only by Control Plane `--setup` via `scripts/setup-local.sh`, never by silent runtime defaults |

Web and `/api/*` require the cookie admin policy. Login: `GET`/`POST /account/login` with form fields `username`, `password`, `returnUrl` plus antiforgery token. Failed login redirects to `/login?error=invalid&returnUrl=...` without echoing the password. Logout: `POST /account/logout` with antiforgery and `returnUrl`.

Typed-options defaults (when unset) are under `~/.config/pi-command-center/`. `scripts/setup-local.sh` instead writes `~/.local/share/devfleet` unless overridden:

| File | Role |
|---|---|
| `$HOME/.local/share/devfleet/admin.password.hash` | `Admin:PasswordFile` (Identity hash, `0600`) |
| `$HOME/.local/share/devfleet/admin.password` | One-time plaintext for the operator (`0600`); not what the runtime loads |
| `$HOME/.local/share/devfleet/node.token` | One node's `NodeAuthentication:CredentialFile` for the explicit loopback local installation (256-bit hex, `0600`) |
| `$HOME/.local/share/devfleet/local.env` | Sourced by `scripts/demo.sh` |
| `$HOME/.local/share/devfleet/pi-command-center.env` | systemd `EnvironmentFile=` (owner-only, absolute host paths) |

### Node (per-node credential, `/nodeHub` only)

| Key | Meaning |
|---|---|
| `NodeAuthentication:CredentialFile` | Node-side `0600` file containing that node's unique application-generated 256-bit credential |
| `NodeAuthentication:Header` | HTTP authentication header (`Authorization`) |
| `NodeAuthentication:Scheme` | Authentication scheme (`Bearer`) |

The operator manually provisions a distinct credential for each node and the corresponding control-plane identity mapping. Automatic credential distribution is out of scope. Successful authentication creates a principal containing exactly one stable `NodeId`; `Register` metadata must match it, and the connection remains bound to it. Every later hub method derives the caller from that connection. A fleet-shared token followed by a body-supplied `NodeId` does not authenticate multiple node identities.

The raw node credential is used only by the HTTP authentication layer. It is never written to a SignalR request, response, callback, event, assignment, or readiness DTO; never logged or returned by an API; and never given to agents. For a non-loopback control-plane URL, the node requires HTTPS/WSS with normal certificate-chain and hostname validation and rejects plaintext, downgrade redirects, and validation bypasses before sending the authentication header or assignment data. Positively verified loopback is the sole HTTP exception.

Provider/runtime authentication readiness crosses the hub only as typed status (`Ready`, `Unavailable`, or `Unknown`), stable evidence source, observation time, and routing revision. Provider credentials, credential contents, account identifiers, and raw provider output never cross the node transport. Credential-file presence alone is not authentication evidence.

### Startup failure

Outside the `Testing` environment, a missing `Admin:PasswordFile` on the control plane or missing node-side `NodeAuthentication:CredentialFile` (or an unreadable/empty file) **fails that process start** with an actionable provisioning message. No insecure built-in password or node credential is generated during ordinary startup. `--setup` is explicit; it is not invoked on ordinary `dotnet run`.

### Data Protection keys (cookie persistence)

| Key | Meaning |
|---|---|
| `DataProtection:KeysDirectory` | Directory for ASP.NET Core Data Protection keys. Stable application name is `PiCommandCenter.ControlPlane`. `~` is expanded via `PrivateFileAccess.ExpandPath`. |

Purpose: keep authenticated browser sessions valid across Control Plane process restarts. Keys must live under the canonical data root that survives unit restarts (production: `~/.local/share/devfleet` data-protection keys; local typed-options default: `~/.config/pi-command-center/data-protection-keys`).

This is **not** the admin password hash (`Admin:PasswordFile`) and **not** any node authentication credential. Those authenticate operators and individual nodes. Data Protection keys only protect cookies, antiforgery tokens, and other ASP.NET Core payloads so a second host sharing the same database, auth identity registry, and key directory can accept a cookie issued by the first.

`DataProtection:KeysDirectory` is created with owner-only directory mode (`0700`) on Unix. File persistence alone does **not** encrypt keys at rest.

## Filesystem modes (Linux)

| Path | Mode |
|---|---|
| Data root (`~/.local/share/devfleet`) | `0700` |
| `Admin:PasswordFile`, each node's `NodeAuthentication:CredentialFile`, node spool DB, Claude session settings | `0600` (directories `0700`) |
| `DataProtection:KeysDirectory` | `0700` |
| Claude reservation hook script | owner-only executable |

`scripts/setup-local.sh` creates these. systemd units load `EnvironmentFile=` from that private directory.

## Node-local workspace trust

`Projects:ApprovedRoots` belongs to each node, not to the control plane: only that machine can define and inspect its local path namespace. Workspace designation browses that tree through `BrowseWorkspaceDirectories` on the selected authenticated node. The control plane never reads node filesystems. Browse returns only existing directories inside ApprovedRoots, omits symlink entries, never allows traversal above an approved root, bounds entries (500) and error detail (512 characters), and fails if the node is offline. Selecting a folder records a path; it does not prove Git or default-branch validity. Because browse cannot inspect Git, the designation UI always warns, before submission, that designating consents to node-local Git metadata changes made only when needed.

A workspace designation starts a new validation revision. The control plane invokes `ValidateWorkspaceBinding` only on the connection authenticated as the binding's node, and the node canonicalizes the path and applies its ApprovedRoots, filesystem, and Git checks. The result is a read-only preparation classification — `valid`, `repository_initialization_required` (ordinary directory), `baseline_commit_required` (unborn repository), or an invalid code such as `path_not_writable`, `nested_in_parent_repository`, `not_git_repository`, or `default_branch_missing` — and it changes nothing on disk. It is bounded and structured; it includes the same binding, project, and revision plus the canonical path on success. The control plane accepts it only from that node and only while the revision remains current.

Local Git preparation is a node-local, assignment-scoped supervisor action, never a control-plane or agent action. It runs after the assignment journal is durable and before baseline capture, request-branch creation, and root start, and its authority is limited by construction:

- Git is invoked argv-only through the trusted supervisor service; no shell string is composed, and agents never gain Git authority from an assignment.
- Initialization and the baseline commit use the fixed command-local identity `DevFleet Supervisor <devfleet@localhost>` passed per command, and the exact message `Initialize workspace for DevFleet`. No global or repository Git configuration is read or written.
- No remote, hook, credential helper, submodule, or additional worktree is ever configured, so preparation cannot fetch, push, or execute repository-supplied code.
- Preparation only adds history: it commits existing non-ignored contents and never deletes, resets, stashes, or cleans operator content.
- Preparation and request-branch creation are idempotent, so a retry on the same assignment cannot duplicate or diverge history; a preexisting divergent request branch fails closed.
- A startup failure, preparation included, journals `StartBlocked` and publishes one assignment-scoped `request.blocked` event without fabricating a session identity. The assignment stays retained and retryable on the same node; it is not reassigned, and it reconciles as retained rather than `RecoveryRequired`. Cancelling it still requires proven quiescence before terminalization.

## Reservation and hook boundary

- Control Plane is the reservation authority. Nodes call hub methods; browsers only force-release.
- Fencing tokens are monotonic per project. Stale tokens → `invalid_fencing_token`.
- Claude: settings and hook live under `$XDG_DATA_HOME/devfleet/claude-runtime/<session>/`; `--setting-sources ""` prevents merged project or user hooks, while the explicit host `--settings` file installs the reservation gate.
- Pi reserved tools must present lease id + fencing token; the node calls `AuthorizeMutation` immediately before the write.
- Node event and control authorization is assignment-scoped. The authenticated connection `NodeId`, durable `ExecutionAssignment`, claim token, binding revision, session, request, and project must correlate before renewal, event publication, heartbeat session membership, reservation, mutation, verification, completion, mail, cancellation, repository/Git work, or child-session creation.
- Disconnect, heartbeat expiry, and claim-lease expiry change liveness or require reconciliation; they do not release writer ownership. Completion, failure, or cancellation becomes releasable only after assignment-bound quiescence closes admission and accounts for supervised processes, mutations, verification, Git work, reservations, and spooled events. Uncertainty remains `RecoveryRequired`; there is no automatic failover.
- Antigravity runs inside Bubblewrap with a private PID namespace plus private `/proc`, and with the host root and repository read-only. Its provider-owned `~/.gemini` credential/cache/log directory is the sole writable home-state bind required by the official CLI; private empty mounts hide Pi, Claude, and Muse credential stores. The node unit keeps an empty capability set and the inner read-only root, rather than systemd's namespace-based kernel, control-group, clock, and hostname protections, because those redundant directives prevent Bubblewrap from mounting the isolated `/proc`.
- Verification runs inside Bubblewrap with networking and the host process table isolated, user homes/runtime sockets hidden, host root read-only, and only the canonical repository plus a temporary HOME writable.

## Provider credentials

- Never stored, copied, or relayed. Claude Code, `agy`, Muse, and Pi providers keep their own node-local login caches. Credential contents never appear in execution-readiness observations or cross the node transport.
- Missing provider auth: typed readiness is `Unavailable` or `Unknown`; an already assigned session uses `Attention=InputRequired`, `WorkState=Blocked`, with a reason naming the provider's native login — not a generic crash and not a Command Center credential form. It remains assigned to that node.
- Operators run `claude` / `agy` login locally before any `RUN_REAL_*` path. `scripts/demo.sh --smoke` does not start those CLIs and does not collect provider tokens.
- Do not paste provider tokens into Command Center config, SQLite, or the admin password files.
- Model selectors are `<provider>/<model>`: the reserved prefixes `claude-code`, `antigravity`, and `muse` select their official-harness adapters; every other valid prefix goes **only** to Pi as the runtime adapter (`codex` aliases Pi's `openai-codex`, all others pass through identically). There is no path from a selector to an arbitrary executable, and an unknown or unauthenticated Pi provider fails closed rather than falling back.


## Logging

Redact secrets; bound stderr tails (`Claude:MaxStderrLines`, `Antigravity:MaxStderrLines`, default 200). Do not dump environment wholesale. Protocol stdout is not a log stream. Never log node credentials, authentication headers, provider credentials, or raw readiness evidence. Audit force-release, cancellation, completion override, assignment reconciliation/recovery, workspace validation changes, and supervisor Git mutations.
