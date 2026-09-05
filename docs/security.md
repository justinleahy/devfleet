# Security

Single local administrator. Loopback by default. No anonymous web UI or API (except loopback `/health`). Provider OAuth stays in official CLIs.

## Threat model (PoC)

| Threat | Control |
|---|---|
| Anonymous browser on the workstation | Cookie auth + antiforgery; no anonymous pages except `/health` on loopback |
| CSRF against login/logout and mutating APIs | `UseAntiforgery`; login is an antiforgery form; logout is POST |
| Node impersonation | High-entropy credential in a `0600` file; SignalR `/nodeHub` uses the node token policy only |
| Credential leak in logs/API | Node secret is never logged or returned; provider tokens never enter SQLite or agent payloads |
| Path traversal / symlink escape | Only `Projects:ApprovedRoots`; resolved canonical paths; `.git/` cannot be reserved |
| Prompt injection widening permissions | Runtime executables, profiles, hooks, project roots, and completion gates are configuration/supervisor-owned |
| Reservation bypass via Claude tools | Host-owned `--settings`; `--setting-sources ""` disables repository/user hook discovery; PreToolUse/PostToolUse gate on loopback |
| Shell escape for writers | Pi children lack unrestricted `edit`/`write`/`bash`; mutations go through reserved tools + `AuthorizeMutation` |
| Stale writer after crash | Leases go `RecoveryRequired`; fencing token must match |
| Binding to the LAN accidentally | Kestrel URLs default to `127.0.0.1` |

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
| `$HOME/.local/share/devfleet/node.token` | `NodeAuthentication:CredentialFile` (256-bit hex, `0600`) |
| `$HOME/.local/share/devfleet/local.env` | Sourced by `scripts/demo.sh` |
| `$HOME/.local/share/devfleet/pi-command-center.env` | systemd `EnvironmentFile=` (owner-only, absolute host paths) |

### Node (token, `/nodeHub` only)

| Key | Meaning |
|---|---|
| `NodeAuthentication:CredentialFile` | Application-generated 256-bit hex token (`0600`). Same file on Control Plane and Node |
| `NodeAuthentication:Header` | HTTP header (`Authorization`) |
| `NodeAuthentication:Scheme` | Auth scheme (`Bearer`) |

The Node process reads the file and authenticates the SignalR connection. The secret is never written to logs, never returned on APIs, and never given to agents.

### Startup failure

Outside the `Testing` environment, missing `Admin:PasswordFile` or `NodeAuthentication:CredentialFile` (or unreadable/empty files) **fails process start** with an actionable message to run Control Plane `--setup` / `scripts/setup-local.sh`. No insecure built-in password. `--setup` is explicit; it is not invoked on ordinary `dotnet run`.

### Data Protection keys (cookie persistence)

| Key | Meaning |
|---|---|
| `DataProtection:KeysDirectory` | Directory for ASP.NET Core Data Protection keys. Stable application name is `PiCommandCenter.ControlPlane`. `~` is expanded via `PrivateFileAccess.ExpandPath`. |

Purpose: keep authenticated browser sessions valid across Control Plane process restarts. Keys must live under the canonical data root that survives unit restarts (production: `~/.local/share/devfleet` data-protection keys; local typed-options default: `~/.config/pi-command-center/data-protection-keys`).

This is **not** the admin password hash (`Admin:PasswordFile`) and **not** the node token (`NodeAuthentication:CredentialFile`). Those authenticate operators and nodes. Data Protection keys only protect cookies, antiforgery tokens, and other ASP.NET Core payloads so a second host sharing the same database, auth material, and key directory can accept a cookie issued by the first.

`DataProtection:KeysDirectory` is created with owner-only directory mode (`0700`) on Unix. File persistence alone does **not** encrypt keys at rest.

## Filesystem modes (Linux)

| Path | Mode |
|---|---|
| Data root (`~/.local/share/devfleet`) | `0700` |
| `Admin:PasswordFile`, `NodeAuthentication:CredentialFile`, node spool DB, Claude session settings | `0600` (directories `0700`) |
| `DataProtection:KeysDirectory` | `0700` |
| Claude reservation hook script | owner-only executable |

`scripts/setup-local.sh` creates these. systemd units load `EnvironmentFile=` from that private directory.

## Reservation and hook boundary

- Control Plane is the reservation authority. Nodes call hub methods; browsers only force-release.
- Fencing tokens are monotonic per project. Stale tokens → `invalid_fencing_token`.
- Claude: settings and hook live under `$XDG_DATA_HOME/devfleet/claude-runtime/<session>/`; `--setting-sources ""` prevents merged project or user hooks, while the explicit host `--settings` file installs the reservation gate.
- Pi reserved tools must present lease id + fencing token; the node calls `AuthorizeMutation` immediately before the write.
- Antigravity runs inside Bubblewrap with a private PID namespace plus private `/proc`, and with the host root and repository read-only. Its provider-owned `~/.gemini` credential/cache/log directory is the sole writable home-state bind required by the official CLI; private empty mounts hide Pi, Claude, and Muse credential stores. The node unit keeps an empty capability set and the inner read-only root, rather than systemd's namespace-based kernel, control-group, clock, and hostname protections, because those redundant directives prevent Bubblewrap from mounting the isolated `/proc`.
- Verification runs inside Bubblewrap with networking and the host process table isolated, user homes/runtime sockets hidden, host root read-only, and only the canonical repository plus a temporary HOME writable.

## Provider credentials

- Never stored, copied, or relayed. Claude Code and `agy` keep their own login caches.
- Missing provider auth: session dimensions `Attention=InputRequired`, `WorkState=Blocked`, reason naming **Claude Code native login** or **agy native login** — not a generic crash and not a Command Center credential form.
- Operators run `claude` / `agy` login locally before any `RUN_REAL_*` path. `scripts/demo.sh --smoke` does not start those CLIs and does not collect provider tokens.
- Do not paste provider tokens into Command Center config, SQLite, or the admin password files.
- Model selectors are `<provider>/<model>`: the reserved prefixes `claude-code`, `antigravity`, and `muse` select their official-harness adapters; every other valid prefix goes **only** to Pi as the runtime adapter (`codex` aliases Pi's `openai-codex`, all others pass through identically). There is no path from a selector to an arbitrary executable, and an unknown or unauthenticated Pi provider fails closed rather than falling back.


## Logging

Redact secrets; bound stderr tails (`Claude:MaxStderrLines`, `Antigravity:MaxStderrLines`, default 200). Do not dump environment wholesale. Protocol stdout is not a log stream. Audit force-release, cancel, completion override, and supervisor Git mutations.
