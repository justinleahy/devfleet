# DevFleet

Project-centric command center for orchestrated local development on a Fedora workstation: Blazor UI, ASP.NET Core control plane, .NET node worker, and a TypeScript Pi worker. Official `claude` and `agy` binaries stay unmodified; their credentials stay in those products.

Details: [docs/architecture.md](docs/architecture.md), [docs/protocols.md](docs/protocols.md), [docs/security.md](docs/security.md). Product rules: [SPEC.md](SPEC.md).

## Prerequisites (Fedora)

- Fedora Linux workstation, systemd user session.
- .NET SDK **10.0** (`TargetFramework` `net10.0` in `Directory.Build.props`). Install with the current Fedora `dotnet` 10 packages or the Microsoft SDK.
- Node.js **≥ 26** (`runtime/package.json` `engines.node`) and npm.
- Git and Bubblewrap (`bwrap`), required for verification and Antigravity filesystem boundaries.
- Optional runtimes on `PATH`: `claude` (Claude Code), `agy` (Antigravity CLI), plus a Pi-capable model configuration for `@earendil-works/pi-coding-agent` 0.85.0.

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

## First-time setup

Secrets are created **only** by the setup script.

```bash
./scripts/setup-local.sh
```

Defaults:

| Variable | Default |
|---|---|
| `PI_CC_DATA` | `~/.local/share/pi-command-center` (`0700`) |
| Admin username | `admin` (`Admin:Username`) |
| `Admin:PasswordFile` | `$PI_CC_DATA/admin.password.hash` (`0600`, password hash) |
| `NodeAuthentication:CredentialFile` | `$PI_CC_DATA/node.token` (`0600`) |

The script also writes `$PI_CC_DATA/pi-command-center.env` with expanded absolute paths for the systemd user units. [deploy/pi-command-center.env.example](deploy/pi-command-center.env.example) documents its shape. Typed JSON examples: [deploy/appsettings.ControlPlane.example.json](deploy/appsettings.ControlPlane.example.json), [deploy/appsettings.Node.example.json](deploy/appsettings.Node.example.json).

Install JS deps once:

```bash
cd runtime && npm ci && cd ..
```

### Provider-native login (opaque to this app)

```bash
claude    # Claude Code interactive login; do not paste tokens into Command Center
agy       # Antigravity native login
```

If a session starts without provider auth, the UI shows blocked + input required naming that native login — not a Command Center password prompt.

### Pi model configuration

Configure models through Pi’s own agent data under `Pi:AgentDataDirectory` (default `~/.local/share/pi-command-center/pi-agent`). Do not put provider API keys in SQLite or `appsettings`. `Pi:WorkerPath` may be left empty; the node resolves `runtime/pi-worker/src/index.ts` from the content root.

## Migrations

The Control Plane applies EF Core migrations on startup (`MigrateAsync` in `src/PiCommandCenter.ControlPlane/Program.cs`). There is no separate migrate command for normal operation. Database file: `ConnectionStrings:ControlPlane` (SQLite).

## Loopback run

Port **5057** on `127.0.0.1` (node default `Node:ControlPlaneUrl` is `http://127.0.0.1:5057`).

```bash
export PI_CC_DATA="${PI_CC_DATA:-$HOME/.local/share/pi-command-center}"
export PI_CC_PORT="${PI_CC_PORT:-5057}"
./scripts/demo.sh
```

`--smoke` uses a temporary data directory, never launches providers, and does **not** count as a completed demonstration.

Open `http://127.0.0.1:5057/login`, sign in as `admin` with the password from `$PI_CC_DATA/admin.password`. Health: `curl -fsS http://127.0.0.1:5057/health`.

Without the demo script:

```bash
export ASPNETCORE_URLS="http://127.0.0.1:5057"
export ASPNETCORE_ENVIRONMENT=Production
dotnet run --project src/PiCommandCenter.ControlPlane/PiCommandCenter.ControlPlane.csproj --no-launch-profile
dotnet run --project src/PiCommandCenter.Node/PiCommandCenter.Node.csproj --no-launch-profile
```

Pass `Admin__PasswordFile`, `NodeAuthentication__CredentialFile`, and `Node__ControlPlaneUrl` via environment or `--environment` files. Missing auth material outside `Testing` stops the process with a message pointing at `scripts/setup-local.sh`.

## Private-LAN HTTPS (phone)

Do not bind `0.0.0.0` on HTTP. Use TLS and a certificate you trust on the phone:

```bash
export ASPNETCORE_URLS="https://0.0.0.0:7443"
export ASPNETCORE_Kestrel__Certificates__Default__Path="$PI_CC_DATA/https/devcert.pfx"
export ASPNETCORE_Kestrel__Certificates__Default__PasswordFile="$PI_CC_DATA/https/devcert.password"
```

Point `ControlPlane:BaseUrl` and `Node:ControlPlaneUrl` at `https://<workstation-lan-ip>:7443`. Keep `NodeAuthentication` and cookie auth enabled. Firewall: allow 7443 only on the intended interface.

## Docker Compose

The Compose stack publishes both .NET services, installs the Pi worker on Node.js 26,
persists state under `~/.local/share/devfleet`, and binds the UI to loopback only.

```bash
PI_CC_DATA="$HOME/.local/share/devfleet" ./scripts/setup-local.sh
DEVFLEET_BIND_ADDRESS=10.0.0.20 docker compose up --build --detach
docker compose ps
docker compose logs --follow
```

Open `http://<DEVFLEET_BIND_ADDRESS>:5057`. Omit `DEVFLEET_BIND_ADDRESS` to keep
the deployment loopback-only. The node mounts `~/Developer` at the same absolute
path and mounts the host's Claude and Antigravity executables plus Claude credentials.
The node process runs as uid 1000, but the container receives the full capability
bounding set and an unconfined seccomp profile so its setuid `bwrap` can create the
nested namespaces required by verification and read-only agent sandboxes.

### Change the administrator password

Passwords supplied on stdin must contain at least 12 characters. This avoids exposing
the password in shell history or the process list:

```bash
read -rsp "New DevFleet password: " password; echo
printf '%s\n' "$password" |
  docker compose run --rm -T control-plane --setup --force --password-stdin
printf '%s' "$password" > "$HOME/.local/share/devfleet/admin.password"
chmod 0600 "$HOME/.local/share/devfleet/admin.password"
unset password
docker compose restart control-plane node
```

Forced setup also rotates the node credential, so both services must restart.

## systemd user install

Units launch **published** binaries under the protected install root `~/.local/lib/pi-command-center` (not the source tree, so approved `~/Developer` repos cannot overwrite the runtime).

```bash
./scripts/install-systemd.sh
systemctl --user enable --now pi-command-center-control-plane.service pi-command-center-node.service
systemctl --user status pi-command-center-control-plane.service pi-command-center-node.service
journalctl --user -u pi-command-center-control-plane.service -u pi-command-center-node.service -f
```

`install-systemd.sh` runs `setup-local.sh`, publishes Control Plane and Node, copies `runtime/` with `npm ci --omit=dev`, and installs the user units. Override the install prefix only under `~/.local/lib/pi-command-center` via `PI_CC_INSTALL_ROOT`.

Linger if you need the stack without a graphical login: `loginctl enable-linger "$USER"`.

## Demo

The first SPEC demonstration is **web UI only**. `scripts/demo.sh` starts loopback hosts and registers `demo/health-details-fixture`. It does not complete a request.

```bash
PI_CC_PORT=5057 ./scripts/demo.sh
```

Sign in, open the printed `/projects/{id}` page, and **Queue request** with the canonical prompt in [demo/FIRST-DEMO.md](demo/FIRST-DEMO.md) (health/details API + tests + README, split writers, independent review).

`--smoke` is quota-free and exits after registration. Without `RUN_REAL_*`, the node stays stopped so UI-queued work cannot spend provider quota. Setting all three opt-ins starts the node; queue the request in the web UI and let the completion gate/UI report the outcome. `--smoke` ignores `RUN_REAL_*`.

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
| `Projects:ApprovedRoots` | Allowed registration prefixes |
| `Admin:Username` / `Admin:PasswordFile` | Cookie admin |
| `NodeAuthentication:CredentialFile` / `Header` / `Scheme` | Node hub |
| `Node:ControlPlaneUrl`, `Id`, `DisplayName`, `HeartbeatSeconds`, `ClaimLeaseSeconds`, `EventSpoolPath`, `RequireCleanStart`, `AllowUntrackedFiles` | Node worker |
| `Pi:*` | Worker path, node executable, agent data dir, timeouts, child caps, allowed roles/profiles, and ordered `RoleRoutes` |
| `Claude:*` | `Executable` (`claude`), `SettingsPath`, timeouts, line caps |
| `Antigravity:*` | `Executable` (`agy`), timeouts, line caps |
| `Verification:Profiles` | Trusted command lists (agents cannot supply executables) |

`Pi:RoleRoutes:<role>` is an ordered list of `{ RuntimeProfile, Model }` candidates.
The node—not the root agent—tries them in order until a runtime starts. `Model` may
be omitted or `null` to use that provider's default, and the same runtime profile may
appear repeatedly with different models. See `deploy/appsettings.Node.example.json`.

Authenticated operators can edit these routes at `/routing`. The page talks to the
selected online node over the existing SignalR connection; updates take effect for the
next child spawn and are persisted as `role-routes.json` under `Pi:AgentDataDirectory`.
**Refresh models** queries the node's authenticated Pi catalog and `agy models`.
Claude Code does not expose a model-list command, so Claude routes keep a free-text
model field and may use `null` for the provider default.
