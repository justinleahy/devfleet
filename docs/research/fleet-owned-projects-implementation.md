# Research: Fleet-owned projects — implementation constraints

**Date researched:** 2026-09-05
**Target framework:** ASP.NET Core 10 / EF Core SQLite (`PiCommandCenter.ControlPlane` → `net10.0`)
**Purpose:** Source-backed implementation constraints for the fleet-owned Project / WorkspaceBinding / ExecutionAssignment cutover. Primary sources only. Product contract is `docs/design/fleet-owned-projects.md`; this note does not amend that contract.

**Sourced facts** are labeled **Fact**. **Project decisions** (this cutover) are labeled **Decision**. Do not mix them.

## Primary sources

- [SignalR authentication and authorization](https://learn.microsoft.com/en-us/aspnet/core/signalr/authn-and-authz?view=aspnetcore-10.0) (ASP.NET Core 10)
- [ASP.NET Core SignalR configuration](https://learn.microsoft.com/en-us/aspnet/core/signalr/configuration?view=aspnetcore-10.0)
- [Security considerations in ASP.NET Core SignalR](https://learn.microsoft.com/en-us/aspnet/core/signalr/security?view=aspnetcore-10.0)
- [IUserIdProvider](https://learn.microsoft.com/en-us/dotnet/api/microsoft.aspnetcore.signalr.iuseridprovider?view=aspnetcore-10.0)
- [HttpConnectionOptions](https://learn.microsoft.com/en-us/dotnet/api/microsoft.aspnetcore.http.connections.client.httpconnectionoptions?view=aspnetcore-10.0)
- [HttpClientHandler.ServerCertificateCustomValidationCallback](https://learn.microsoft.com/en-us/dotnet/api/system.net.http.httpclienthandler.servercertificatecustomvalidationcallback?view=net-10.0)
- [Enforce HTTPS in ASP.NET Core](https://learn.microsoft.com/en-us/aspnet/core/security/enforcing-ssl?view=aspnetcore-10.0)
- [SQLite Database Provider — Limitations (EF Core)](https://learn.microsoft.com/en-us/ef/core/providers/sqlite/limitations)
- [Migrations Overview — EF Core](https://learn.microsoft.com/en-us/ef/core/managing-schemas/migrations/)
- [SQLite ALTER TABLE](https://sqlite.org/lang_altertable.html)
- [SQLite Transaction](https://sqlite.org/lang_transaction.html)
- [SQLite Foreign Key Support](https://sqlite.org/foreignkeys.html)

---

## 1. SignalR authenticated principal and connection identity

### Facts (ASP.NET Core 10)

- SignalR associates a user with each connection. In a hub, authentication data is `HubConnectionContext.User` / `HubCallerContext.User` (`Context.User`). Multiple connections can map to one user.
- SignalR **captures the authenticated principal when the connection is established and caches it for the lifetime of that connection**. It does **not** automatically revalidate identity, roles, or claims during the connection, including LongPolling and Server-Sent Events even when those transports make new HTTP requests. Later hub methods authorize against the cached principal.
- If a token expires during a live connection, the connection **continues by default**. `LongPolling` and `ServerSentEvents` fail on subsequent requests if they do not send new access tokens. To close when the token expires, set `HttpConnectionDispatcherOptions.CloseOnAuthenticationExpiration` (`false` by default).
- Bearer tokens: `AccessTokenProvider` on the .NET client is invoked before **every** HTTP request. For browser WebSockets/SSE, the token is sent as a query string parameter `access_token` because browsers cannot set Authorization on those transports. ASP.NET Core logs request URLs (including query string) at Info by default.
- `IUserIdProvider.GetUserId(HubConnectionContext)` configures the user id used by `IHubClients.User(string)`. Default uniqueness of `Name` is a documented hazard: non-unique Name claims cause mis-delivery.
- `MaximumParallelInvocationsPerClient` defaults to **1**. `EnableDetailedErrors` defaults to **false**; hub exception details must not ship to clients in production.

### In-repo hazard (observation, not Microsoft)

`NodeTokenAuthenticationHandler` succeeds with `ClaimTypes.Name = "node"` and `ClaimTypes.Role = "Node"` only. It does **not** emit a NodeId claim. `NodeHub.Register` / `Heartbeat` take `message.NodeId` from the payload and bind `nodeConnections` to that id. Shared fleet token + self-asserted NodeId is impersonation.

### Decision

- Production multi-node: principal **must** contain a stable NodeId from a per-node credential. Derive NodeId from the connection on every hub method. Payload NodeId mismatch fails closed.
- Bind one NodeId to the connection for its lifetime. Replacing a connection for the same NodeId does **not** replace an ExecutionAssignment or create a writer.
- Only the assigned node's authenticated connection (plus claim token) may renew, publish owned events, operate reservations, complete, or receive cancellation.
- Do not use heartbeat session ids to join foreign SignalR groups.
- Keep `EnableDetailedErrors` development-only (already gated on `IsDevelopment()`).
- Do not rely on token expiry to revoke a live node connection unless `CloseOnAuthenticationExpiration` is set; assignment authorization must re-check NodeId + assignment on each hub method, not only the cached "Node" role.

### Implementation checks

1. Hub methods never read execution NodeId from the message body as authority.
2. `IUserIdProvider` (if used for `Clients.User`) returns the authenticated NodeId, unique per node credential.
3. `ValidateWorkspaceBinding` results accepted only from the connection authenticated as the binding's NodeId and only for the current validation revision.
4. Tests: shared token + foreign NodeId in body is rejected after the principal change.

---

## 2. HTTPS / WSS client handling, redirects, certificates

### Facts

- Microsoft: **No API can prevent a client from sending sensitive data on the first request.**
- Web APIs should **not listen on HTTP** or should close HTTP with **400**. Do **not** use `RequireHttpsAttribute` on APIs that receive sensitive information: it redirects; API clients may ignore redirects and send secrets on HTTP.
- HSTS is a **browser** instruction. Phone/desktop clients do **not** obey it. A single authenticated HTTP call still leaks on an insecure network.
- `UseHttpsRedirection` default is **307 Temporary Redirect**. CORS preflight + HTTP→HTTPS redirect can fail (`ERR_INVALID_REDIRECT`).
- SignalR security: always use HTTPS for a secure end-to-end connection. Query-string access tokens are as secure as Authorization **only over HTTPS**.
- `HttpConnectionOptions`: `AccessTokenProvider`, `Headers`, `HttpMessageHandlerFactory` (wrap/replace the handler that makes HTTP requests), `WebSocketConfiguration` / `WebSocketFactory`, `SkipNegotiation`.
- `HttpClientHandler.ServerCertificateCustomValidationCallback` is `Func<HttpRequestMessage, X509Certificate2?, X509Chain?, SslPolicyErrors, bool>`. Official sample returns `sslErrors == SslPolicyErrors.None` (full chain + hostname policy). Returning `true` for any error is a validation bypass.

### In-repo hazard

`NodeTransportClient` builds `HubConnection` from `ControlPlaneUrl` + `/nodeHub` and applies the node credential via `AccessTokenProvider` or `Headers`. There is no check that the URL is loopback HTTP vs remote HTTPS, no redirect policy, and no custom certificate callback (default OS trust applies). Automatic reconnect retries indefinitely (cap 30s).

### Decision

- HTTP only for a **positively verified loopback** endpoint. Every non-loopback node connection uses HTTPS/WSS with normal certificate-chain and hostname validation.
- Reject plaintext remote URLs, TLS-to-HTTP downgrade redirects, and certificate-validation bypasses **before** sending credentials or assignment data.
- Manual server-certificate trust and per-node credentials are deployment prerequisites; without them, restrict the phase to loopback.
- Control plane: do not treat HTTPS redirection as protection for `/nodeHub`. Prefer not listening on HTTP remotely; 400 on non-loopback HTTP.

### Implementation checks

1. Node start: if `ControlPlaneUrl` is not loopback, scheme must be `https`. Fail before `StartAsync`.
2. Do not set `ServerCertificateCustomValidationCallback` to accept `SslPolicyErrors != None`. Pin extra trust via the OS/user store or an explicit extra root, not a bypass.
3. If wrapping `HttpMessageHandlerFactory`, keep `AllowAutoRedirect` from following HTTPS→HTTP. Fail closed on scheme downgrade.
4. Never log `AccessTokenProvider` output or Authorization headers (handler already avoids logging the presented secret).
5. Filter `access_token` from request URL logs if WebSockets put the token in the query string.

---

## 3. EF Core SQLite transactional migration / table rebuild

### Facts

- Dropping a column, adding/dropping FK, PK, unique, or check constraints, and `AlterColumn` are **rebuild** operations on SQLite in EF Core. Rebuilds are only attempted for artifacts **in the EF model**. Manual-only artifacts throw `NotSupportedException`.
- Workaround: write SQL in the migration (`MigrationBuilder.Sql`): create new table, copy data, drop old, rename new. SQLite documents this as the path for schema changes beyond rename/add/drop column.
- SQLite `ALTER TABLE` natively supports rename table, rename column, add column, drop column (with restrictions). Drop column **fails** if the column is PK/UNIQUE, indexed, in a CHECK, used in an FK, generated column, trigger, or view.
- Add column: cannot add PK/UNIQUE; NOT NULL requires a non-NULL default; if FKs are enabled, a new REFERENCES column must default to NULL.
- EF9+ SQLite migration lock: table `__EFMigrationsLock`. Unexpected process kill can leave the lock; subsequent `MigrateAsync` waits indefinitely until `DROP TABLE "__EFMigrationsLock"` or delete rows.
- SQLite has **no procedural language**; EF cannot generate idempotent if-then migration scripts. Prefer `database update` / `MigrateAsync` over idempotent SQL scripts.
- Migrations record applied ids in the history table; `MigrateAsync` applies only pending migrations (Control Plane already calls this at startup).

### Decision

One preconditioned, **transactional** cutover migration:

1. Create `WorkspaceBindings` and `ExecutionAssignments` while old Project/claim columns remain readable.
2. Backfill one designated binding per existing Project from current `NodeId` + canonical path; status **`PendingValidation`** (legacy control-plane Git check is not node attestation). Preserve `Project.Id`.
3. Convert each `RequestClaim` into an ExecutionAssignment with immutable snapshot. Nonterminal legacy claims → `RecoveryRequired`. Never requeue.
4. Assert 1:1 Project→binding and claim→assignment; **abort** rather than guess.
5. Rebuild `Projects` without `NodeId`/`RepositoryPath`; drop `IX_Projects_RepositoryPath`; remove old claim table. No compatibility columns.

Project has no NodeId/path after cutover. Zero-or-one WorkspaceBinding. ExecutionAssignment is durable history (not deleted on lease expiry).

### Implementation checks

1. Review generated migration: DropColumn on `Projects.NodeId`/`RepositoryPath` must be an explicit rebuild (or EF rebuild), not a naive `ALTER TABLE ... DROP COLUMN` that fails because of indexes/FKs.
2. Entire expand → backfill → verify → contract in **one** EF migration so a crash rolls back (see §4). Do not split across two deployed migrations without a dual-read window (this cutover forbids compatibility dual-read).
3. Integration test: start from **prior** migration, insert node-bound project + queue + claim, apply, assert stable ids, one pending binding, retained terminal or `RecoveryRequired` assignment.
4. Document operator recovery for abandoned `__EFMigrationsLock` if startup hangs on migrate.
5. Do not use `EnsureCreated`; only migrations.

---

## 4. SQLite atomicity and foreign keys for this cutover

### Facts

- All reads/writes occur in a transaction. Implicit transactions commit when the last statement finishes. Explicit `BEGIN` … `COMMIT`/`ROLLBACK`.
- Default `BEGIN` is **DEFERRED**: the write lock is taken on first write. `IMMEDIATE` takes a write transaction immediately. Only **one** write transaction at a time; others get `SQLITE_BUSY`.
- Control plane interceptor already sets `PRAGMA journal_mode=WAL`, `PRAGMA foreign_keys=ON`, `PRAGMA busy_timeout=5000`.
- Foreign keys are **off by default** per connection and **cannot be toggled mid-transaction** (no error; no effect). Each connection must enable them (interceptor does this post-open).
- FK: child NULL is allowed unless NOT NULL. Parent key must be PRIMARY KEY or UNIQUE.
- SQLite 12-step table rebuild (official ALTER TABLE §8, summarized by EF docs): if FKs are enabled, either disable them for the rebuild (remember: not mid-transaction) or follow the documented sequence (indexes, triggers, copy, drop, rename, restore FKs, `PRAGMA foreign_key_check`).
- Some errors (`SQLITE_FULL`, `IOERR`, `INTERRUPT`, `NOMEM`) may roll back the whole transaction or only the statement; apps should `ROLLBACK` to a known state.
- `RequestClaimService` already uses `BeginTransactionAsync(IsolationLevel.Serializable)` for claims.

### Decision

- Assignment insert + `Queued → Starting` + token + capacity checks commit **atomically** (keep serializable claim transaction).
- Unique `ExecutionAssignments.RequestId` is the duplicate-claim backstop.
- Lease expiry **never** deletes the assignment or frees the one-writer slot. Nonterminal states including `Finalizing`, `Cancelling`, `RecoveryRequired` occupy the write cap. No failover on expiry.
- Binding unique: `(NodeId, CanonicalRepositoryPath)` plus at most one row per Project.
- FK: `WorkspaceBindings.ProjectId → Projects`; `ExecutionAssignments.RequestId → WorkRequests`; assignment snapshots are **columns**, not live FK to mutable binding path.

### Implementation checks

1. Confirm `foreign_keys=ON` on the connection that runs the cutover (MigrateAsync uses the same interceptor).
2. After rebuild, `PRAGMA foreign_key_check` in the migration (or equivalent asserts) before COMMIT.
3. Do not `PRAGMA foreign_keys=OFF` after BEGIN; if a rebuild requires it, it must be the first action on a fresh connection before BEGIN, then re-enable and check before COMMIT — prefer rebuild SQL that keeps FKs on.
4. Claim path: unique RequestId + serializable transaction still required after rename to ExecutionAssignment.
5. Busy timeout: concurrent UI enqueue vs migrate — operators should stop the host around migrate; `MigrateAsync` at startup already serializes schema change before serving.

---

## 5. Mapping table (hazard → check)

| Hazard | Source fact | Cutover check |
|---|---|---|
| Shared token impersonates NodeId | Principal cached at connect; body NodeId unused by auth | NodeId claim on principal; hub derives id |
| Validation from wrong machine | Hub `Context.User` is the connection identity | Accept validate only from binding NodeId + current revision |
| HTTP leak of node token | First request can be plaintext; APIs must not rely on redirects | Reject remote non-HTTPS before connect |
| Cert bypass | Custom callback can ignore `SslPolicyErrors` | Require `SslPolicyErrors.None` (or equivalent chain+name) |
| Drop Project.NodeId fails / half-migrates | SQLite DROP COLUMN + FK/index limits; EF rebuild | One transactional rebuild migration + abort on ambiguous backfill |
| FK off during copy | FKs default off; cannot toggle mid-txn | Interceptor ON; `foreign_key_check` |
| Second writer on lease expiry | SQLite uniqueness only if modeled | Assignment row retained; capacity counts nonterminal including RecoveryRequired |
| Query-string token in logs | SignalR WSS token in query | HTTPS only; filter logs |

---

## 6. Out of scope (this phase)

- Repository mobility / interchangeable clones.
- Automatic credential distribution.
- Failover to another node on lease or heartbeat expiry.
- Compatibility shims leaving `Project.NodeId` or `RepositoryPath` on the live model.
