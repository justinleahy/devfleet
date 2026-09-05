# First demonstration (web UI)

The SPEC §43 demonstration succeeds only through the web interface. `scripts/demo.sh` starts loopback hosts and registers the fixture **project**. It does **not** complete a request.

## Quota-free (default)

```bash
./scripts/setup-local.sh
./scripts/demo.sh          # or: ./scripts/demo.sh --smoke
```

1. Open the printed Control Plane URL, sign in as `admin` with `$PI_CC_DATA/admin.password`.
2. Open the printed **project** page (`/projects/{id}`).
3. Under **New request**, queue:

   > Add a `/health/details` endpoint, add tests, and update the README. Split the implementation so one agent changes the API and another changes the tests. Require independent review and run the configured test profile.

4. Open the request page. Fake/default mode does not call Pi, Claude, or Antigravity. End-to-end scenarios A–F are covered by `tests/PiCommandCenter.EndToEndTests` with fake runtimes.

## Full provider pipeline (opt-in quota)

Provider-native login first (`claude`, `agy`). Then:

```bash
RUN_REAL_PI_TESTS=1 RUN_REAL_CLAUDE_TESTS=1 RUN_REAL_ANTIGRAVITY_TESTS=1 ./scripts/demo.sh
```

The script still does not mark the request complete. The node may claim the queued work and launch official CLIs. Expected tree:

```text
Root Pi
├── API implementer / Pi             src/App/HealthEndpoint.cs
├── Test implementer / Claude Code   tests/App.Tests/HealthEndpointTests.cs
└── Reviewer / Antigravity           read-only until writers finish
```

Then force both implementers onto `src/App/DependencyInjection.cs`: one reservation is denied, the blocked agent requests a handoff, ownership transfers, the stale fencing token fails, verification runs, completion is accepted only by the gate.

## Smoke

`--smoke` uses a temporary `PI_CC_DATA`, never launches providers, registers the fixture project, and exits. Registration is not demonstration success.
