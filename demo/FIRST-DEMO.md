# First demonstration (web UI)

The SPEC §43 demonstration succeeds only through the web interface. `scripts/demo.sh` always starts the loopback Control Plane and registers the fixture **project** as fleet metadata. Only `RUN_REAL_*` mode starts a node, designates a WorkspaceBinding, and requests validation. The script does **not** complete a request.

## Quota-free (default)

```bash
./scripts/setup-local.sh
./scripts/demo.sh          # or: ./scripts/demo.sh --smoke
```

1. Open the printed Control Plane URL, sign in as `admin` with `$PI_CC_DATA/admin.password`.
2. Open the printed **project** page (`/projects/{id}`).
3. Under **New request**, queue:

   > Add a `/health/details` endpoint, add tests, and update the README. Split the implementation so one agent changes the API and another changes the tests. Require independent review and run the configured test profile.

4. Open the request page. Default mode is Control-Plane-only: no node is running, the Project has no WorkspaceBinding, and the request remains queued without calling Pi, Claude, Antigravity, or Muse. End-to-end scenarios A–F are covered separately by `tests/PiCommandCenter.EndToEndTests` with fake runtimes.

## Full provider pipeline (opt-in quota)

Provider-native login first (`claude`, `agy`). Then:

```bash
RUN_REAL_PI_TESTS=1 RUN_REAL_CLAUDE_TESTS=1 RUN_REAL_ANTIGRAVITY_TESTS=1 ./scripts/demo.sh
```

After the node registers, the script designates its configured `Node__Id` and the prepared fixture path as the Project's WorkspaceBinding, then requests node-local validation. The script still does not mark the request complete. Once the binding and node are eligible, the node may claim queued work and launch official CLIs. Expected tree:

```text
Root Pi
├── API implementer / Pi             src/App/HealthEndpoint.cs
├── Test implementer / Claude Code   tests/App.Tests/HealthEndpointTests.cs
└── Reviewer / Antigravity           read-only until writers finish
```

Then force both implementers onto `src/App/DependencyInjection.cs`: one reservation is denied, the blocked agent requests a handoff, ownership transfers, the stale fencing token fails, verification runs, completion is accepted only by the gate.

## Smoke

`--smoke` uses a temporary `PI_CC_DATA`, starts only the Control Plane, registers one metadata-only fixture Project with no WorkspaceBinding, and exits. It never requests node validation or launches providers. Registration is not demonstration success.
