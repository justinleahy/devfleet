# DevFleet

Fleet-oriented command center for orchestrated local development on Fedora workstations: a Blazor UI and ASP.NET Core control plane coordinate .NET nodes and a TypeScript Pi worker. Official `claude`, `agy`, and `muse` binaries stay unmodified; their credentials stay in those products.

Details: [docs/architecture.md](docs/architecture.md), [docs/protocols.md](docs/protocols.md), [docs/security.md](docs/security.md). Product rules: [SPEC.md](SPEC.md).

## Prerequisites (Fedora)

- Fedora Linux workstation, systemd user session.
- .NET SDK **10.0** (`TargetFramework` `net10.0` in `Directory.Build.props`). Install with the current Fedora `dotnet` 10 packages or the Microsoft SDK.
- Node.js **≥ 26** (`runtime/package.json` `engines.node`) and npm.
- Git and Bubblewrap (`bwrap`), required for verification and Antigravity filesystem boundaries.
- Optional runtimes on `PATH`: `claude` (Claude Code), `agy` (Antigravity CLI), `muse` (Muse Code, read-only), plus a Pi-capable model configuration for `@earendil-works/pi-coding-agent` 0.85.0.

```bash
sudo dnf install -y git bubblewrap nodejs npm
dotnet --version   # 10.x
node --version     # v26+
```

## Repository layout

- `PiCommandCenter.sln` — .NET solution
- `src/PiCommandCenter.ControlPlane` — web host, `/nodeHub`, `/api`, `/health`
- `src/PiCommandCenter.Node` — worker
- `runtime/` — Pi worker (`npm ci`, `npm test`, `npm run typecheck`)
- `scripts/setup-local.sh` — generate secrets and data dir (not silent defaults)
- `scripts/demo.sh` — loopback demo
- `deploy/systemd/` — user units
- `tests/` — unit, integration, end-to-end

## Project placement model

A **Project** is fleet-owned metadata and policy. It has no `NodeId` or repository path, so registration works with no node or checkout. In the initial phase each Project has zero or one **WorkspaceBinding**, which designates one node-local checkout. The selected authenticated node validates the binding's current revision on its own filesystem under its node-side `Projects:ApprovedRoots`; editing the node, path, or validation inputs makes the binding pending again.

Requests can be enqueued while a Project is unbound or its node is offline and remain `Queued` with a scheduling reason. When the designated binding becomes eligible, the control plane atomically creates a durable **ExecutionAssignment** containing the request's immutable node, path, branch, and binding-revision snapshot. Only the connection authenticated as that assigned node, with the assignment token, may act for the request, and every child stays on that node and workspace.

The initial phase has no repository mobility or transparent failover. A Project has an effective limit of one nonterminal development assignment, including finalizing, cancelling, and recovery-required work. Disconnect or lease expiry does not transfer ownership or free the writer slot; completion, failure, and cancellation release it only after assignment-bound quiescence is proved or audited recovery resolves the uncertainty.

## First-time setup

Secrets are created **only** by the setup script.

```bash
./scripts/setup-local.sh
```

Defaults:

| Variable | Default |
|---|---|
| Data root | `~/.local/share/devfleet` (`0700`) |
| Install root | `~/.local/lib/devfleet` |
| Admin username | `admin` (`Admin:Username`) |
| `Admin:PasswordFile` | `$HOME/.local/share/devfleet/admin.password.hash` (`0600`, password hash) |
| `NodeAuthentication:CredentialFile` | `$HOME/.local/share/devfleet/node.token` (`0600`) |
| Node `Projects:ApprovedRoots` | `~/Developer` (binding-validation allowlist; `~` expands in the node process) |

`scripts/setup-local.sh` writes owner-only `~/.local/share/devfleet/pi-command-center.env` with host paths for the persistent database, node assignment/event spool, auth material, Data Protection keys, node-local approved binding roots, installed Pi worker and usage sidecar, resolved provider executables, the Claude credential path, and a service `PATH` including `~/.local/bin`. [deploy/pi-command-center.env.example](deploy/pi-command-center.env.example) documents its shape. Typed JSON examples: [deploy/appsettings.ControlPlane.example.json](deploy/appsettings.ControlPlane.example.json), [deploy/appsettings.Node.example.json](deploy/appsettings.Node.example.json).

Install JS deps once:

```bash
cd runtime && npm ci && cd ..
```

### Provider-native login (opaque to this app)

```bash
claude       # Claude Code interactive login; do not paste tokens into Command Center
agy          # Antigravity native login
muse login   # Muse Code native login on the host; DevFleet never reads ~/.config/muse/auth.json
```

If a session starts without provider auth, the UI shows blocked + input required naming that native login — not a Command Center password prompt.

### Pi model configuration

Configure models through Pi’s own agent data under `Pi:AgentDataDirectory` (default `~/.local/share/devfleet/pi-agent`). Do not put provider API keys in SQLite or `appsettings`. `Pi:WorkerPath` points at the installed worker under `~/.local/lib/devfleet` in production.
## Migrations

The Control Plane applies EF Core migrations on startup (`MigrateAsync` in `src/PiCommandCenter.ControlPlane/Program.cs`). There is no separate migrate command for normal operation. `ConnectionStrings:ControlPlane` selects the SQLite database that retains fleet Project metadata, WorkspaceBindings, request queues and history, and durable ExecutionAssignments. Assignment history survives terminalization, disconnect, and lease expiry. The node journals assignment identity and unacknowledged events at `Node:EventSpoolPath` for reconciliation before new claims after restart.

## Loopback run

Port **5057** on `127.0.0.1` (node default `Node:ControlPlaneUrl` is `http://127.0.0.1:5057`).

```bash
export PI_CC_DATA="${PI_CC_DATA:-$HOME/.local/share/devfleet}"
export PI_CC_PORT="${PI_CC_PORT:-5057}"
./scripts/demo.sh
```

`--smoke` uses a temporary data directory, starts only the Control Plane, registers one metadata-only Project, never launches providers, and does **not** count as a completed demonstration.

Open `http://127.0.0.1:5057/login`, sign in as `admin` with the password from `$PI_CC_DATA/admin.password`. Health: `curl -fsS http://127.0.0.1:5057/health`.

After login: `/` fleet, `/attention`, `/usage` (subscription windows), `/statistics` (persisted session token totals and runtime client cost estimates, not invoices), `/routing`.


Without the demo script:

```bash
export ASPNETCORE_URLS="http://127.0.0.1:5057"
export ASPNETCORE_ENVIRONMENT=Production
dotnet run --project src/PiCommandCenter.ControlPlane/PiCommandCenter.ControlPlane.csproj --no-launch-profile
dotnet run --project src/PiCommandCenter.Node/PiCommandCenter.Node.csproj --no-launch-profile
```

Pass `Admin__PasswordFile`, `NodeAuthentication__CredentialFile`, and `Node__ControlPlaneUrl` via environment or `--environment` files. Configure `Projects__ApprovedRoots__0` in the **node** environment; the Control Plane does not inspect those paths during Project registration. Missing auth material outside `Testing` stops the process with a message pointing at `scripts/setup-local.sh`.

## Private-LAN access

Production stays loopback-only unless installation receives a specific
`DEVFLEET_BIND_ADDRESS`. Wildcards such as `0.0.0.0` and `::` are rejected. The
native node still connects over `127.0.0.1`; only the authenticated browser
surface is added on the selected address.

The built-in LAN listener is HTTP. Use it only on a trusted private LAN, and
allow port 5057 only on the intended firewall interface. Put a trusted TLS
reverse proxy in front before exposing DevFleet beyond that boundary.

## Production: systemd user daemon

systemd user units are the **only** production deployment. There is no Docker or Compose stack. Binaries live under the protected install root `~/.local/lib/devfleet`; live state lives under `~/.local/share/devfleet`. Default bind is loopback. Set `DEVFLEET_BIND_ADDRESS` to a specific LAN address when installing to publish there. Provider CLIs and credential stores stay host-native (`claude`, `agy`, `muse`, `~/.pi/agent`, `~/.claude`, `~/.gemini`, `~/.config/muse`); node units allow their designated WorkspaceBinding and provider paths. No container mounts.

```bash
# Omit DEVFLEET_BIND_ADDRESS for loopback-only deployment.
DEVFLEET_BIND_ADDRESS=10.0.0.20 ./scripts/install-systemd.sh
systemctl --user status pi-command-center-control-plane.service pi-command-center-node.service
journalctl --user -u pi-command-center-control-plane.service -u pi-command-center-node.service -f
systemctl --user restart pi-command-center-control-plane.service pi-command-center-node.service
```

`install-systemd.sh` runs idempotent local setup, publishes Control Plane, Node, and `runtime/` production npm dependencies under `~/.local/lib/devfleet`, installs hardened user units `pi-command-center-control-plane.service` and `pi-command-center-node.service`, reloads systemd, and enables/restarts both services. Units load `~/.local/share/devfleet/pi-command-center.env`. Default bind is `127.0.0.1`; `DEVFLEET_BIND_ADDRESS` adds one specific LAN listener while retaining loopback for the local node connection.

Linger if the stack must run without a graphical login: `loginctl enable-linger "$USER"`.

Open `http://127.0.0.1:5057` (or `http://<DEVFLEET_BIND_ADDRESS>:5057` when bound to a LAN address).

### Change the administrator password

Passwords on stdin must contain at least 12 characters. Rotate through the **installed** control-plane DLL so the live hash file is updated in place:

```bash
set -a
source "$HOME/.local/share/devfleet/pi-command-center.env"
set +a
read -rsp "New DevFleet password: " password; echo
printf '%s\n' "$password" |
  dotnet "$HOME/.local/lib/devfleet/control-plane/PiCommandCenter.ControlPlane.dll" --setup --force --password-stdin
printf '%s' "$password" > "$HOME/.local/share/devfleet/admin.password"
chmod 0600 "$HOME/.local/share/devfleet/admin.password"
unset password
systemctl --user restart pi-command-center-control-plane.service pi-command-center-node.service
```

Forced setup also rotates the node credential, so both services must restart.

## Demo

The first SPEC demonstration is **web UI only**. In its default and `--smoke` modes, `scripts/demo.sh` starts only the loopback Control Plane and registers fleet Project metadata for `demo/health-details-fixture`; the Project has no WorkspaceBinding, so requests remain queued. With `RUN_REAL_*` enabled, the script also starts the authenticated node, designates that node and fixture path as the sole WorkspaceBinding, and explicitly requests node-local validation. The script does not complete a request.

```bash
PI_CC_PORT=5057 ./scripts/demo.sh
```

Sign in, open the printed `/projects/{id}` page, and **Queue request** with the canonical prompt in [demo/FIRST-DEMO.md](demo/FIRST-DEMO.md) (health/details API + tests + README, split writers, independent review).

`--smoke` is quota-free and exits after metadata-only Project registration. Default mode leaves the Control Plane running with the node stopped and the Project unbound, so UI-queued work cannot spend provider quota. Setting a `RUN_REAL_*` opt-in starts the node; after it registers, the script designates the binding with that node's configured ID and the prepared fixture path, then requests validation. Once eligible, the next claim creates a durable ExecutionAssignment for that same node and workspace. Queue the request in the web UI and let the completion gate/UI report the outcome. `--smoke` ignores `RUN_REAL_*`.

## Tests

Default (no provider quota):

```bash
./scripts/verify.sh
```

That runs `dotnet restore`, `dotnet build --no-restore`, `dotnet test --no-build`, then `npm ci` / `npm run typecheck` / `npm test` in `runtime/`.

Opt-in runtime contract tests (subscription quota):

```bash
RUN_REAL_PI_TESTS=1 RUN_REAL_CLAUDE_TESTS=1 RUN_REAL_ANTIGRAVITY_TESTS=1 dotnet test
```

`scripts/demo.sh` honors the same `RUN_REAL_*` variables; they stay off unless set.

## Configuration keys

| Section | Keys |
|---|---|
| `ConnectionStrings:ControlPlane` | SQLite |
| `ControlPlane:BaseUrl` | Browser/API base (example `http://127.0.0.1:5057`) |
| `Projects:ApprovedRoots` (**Node configuration only**) | Node-local allowed WorkspaceBinding prefixes; used by the selected node during revisioned validation, never by Project registration |
| `Admin:Username` / `Admin:PasswordFile` | Cookie admin |
| `NodeAuthentication:CredentialFile` / `Header` / `Scheme` | Node hub |
| `Node:ControlPlaneUrl`, `Id`, `DisplayName`, `HeartbeatSeconds`, `ClaimLeaseSeconds`, `EventSpoolPath`, `RequireCleanStart`, `AllowUntrackedFiles` | Node worker |
| `Pi:*` | Worker path, node executable, agent data dir, timeouts, child caps, `Model` (canonical root selector), allowed roles, and ordered `RoleRoutes` |
| `Claude:*` | `Executable` (`claude`), `SettingsPath`, timeouts, line caps |
| `Antigravity:*` | `Executable` (`agy`), timeouts, line caps |
| `Muse:*` | `Executable` (`muse`), timeouts, line caps; read-only `muse serve` (MSP) with write and shell tools disabled |
| `Verification:Profiles` | Trusted command lists (agents cannot supply executables) |
| `SubscriptionUsage:*` | `NodeExecutable` + `ScriptPath` (installed worker usage sidecar under `~/.local/lib/devfleet`) |

`Pi:Model` is the canonical root selector (`codex/default`). `Pi:RoleRoutes:<role>` is an
ordered list of `{ Model }` candidates with canonical `<provider>/<model>` values (for example
`codex/gpt-5.6-sol` or `zai/glm-4.7`; `default` asks the provider for its default). The reserved
prefixes `claude-code`, `antigravity`, and `muse` select their official-harness adapters; every
other provider prefix runs through Pi (`codex` aliases Pi's `openai-codex`; the rest pass
through identically) and fails closed unless authenticated. The selector chooses the provider
and model; the node—not the root agent—tries candidates
in order until a runtime starts. `muse/*` runs the official Muse Code CLI over its
stable JSON-RPC MSP schema with `--disable-write --disable-shell`, so it belongs only
in read-oriented routes (architect, reviewer), never in implementer or verifier
routes. Auth failures surface as blocked + `muse login`; DevFleet never collects Muse
credentials or reads its auth file. See `deploy/appsettings.Node.example.json`.

Every provider prefix outside the reserved official harnesses (`claude-code`,
`antigravity`, `muse`) runs on Pi as the runtime adapter, which covers every
authenticated Pi provider, not only OpenAI. `codex` aliases Pi's `openai-codex`
provider (`codex/default`, `codex/gpt-5.6-sol`); every other Pi provider prefix
passes through identically (e.g. `zai/glm-4.7`), and an unauthenticated or
unavailable provider fails closed. Model discovery returns one catalog per
authenticated Pi provider, so every reported selector is runnable.

Authenticated operators can edit these routes at `/routing`. The page talks to the
selected online node over the existing SignalR connection; updates take effect for the
next child spawn and are persisted as `role-routes.json` under `Pi:AgentDataDirectory`.
**Refresh models** queries the node's authenticated Pi providers, `agy models`, and Muse's
`model/list`. Those three discovery snapshots are cached in memory on the node for five
minutes, so repeated refreshes reuse the last completed snapshot instead of relaunching
every discovery process. Claude aliases and configured route selectors are recomputed from
live routing on every request. Claude Code cannot export its authenticated model picker, so DevFleet
offers a maintained list of stable aliases (`default`, `fable`, `sonnet`, `opus`, and
`haiku`) plus any full Claude selectors already used in a route. Muse 1.0.3's bundled
MSP catalog can omit supported models, so DevFleet augments the live `model/list`
result with the four concrete IDs `muse-spark-1.3`, `muse-spark-1.3-contributor`,
`muse-spark-1.2`, and `muse-spark-1.2-contributor`, canonicalized as `muse/<model-id>`
and deduplicated while keeping any additional valid live Muse IDs. `muse/default`
remains the provider-selected default on routes, not a discovered catalog entry.
Operators may still enter a canonical selector by hand.
