# Health Details fixture

Small ASP.NET Core app used by the Command Center first demonstration.

Canonical request — submit from the Command Center **web UI** (project page → New request → Queue request). See [../FIRST-DEMO.md](../FIRST-DEMO.md). HTTP registration of the project is not demonstration success.

> Add a `/health/details` endpoint, add tests, and update the README. Split the implementation so one agent changes the API and another changes the tests. Require independent review and run the configured test profile.

## Split scopes

| Agent | Runtime | Files |
|---|---|---|
| API implementer | Pi | `src/App/HealthEndpoint.cs` |
| Test implementer | Claude Code | `tests/App.Tests/HealthEndpointTests.cs` |
| Reviewer | Antigravity | read-only until both writers finish |
| README | either writer after review | `README.md` |

Do not share `src/App/DependencyInjection.cs` without a reservation handoff.

## Local run

```bash
dotnet test
dotnet run --project src/App
# GET /health
```
