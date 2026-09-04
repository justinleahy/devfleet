# Research: MCP Agent Mail — Coordination Semantics

**Date researched:** 2026-09-04
**Primary source:** [Dicklesworthstone/mcp_agent_mail](https://github.com/Dicklesworthstone/mcp_agent_mail) — [README.md](https://raw.githubusercontent.com/Dicklesworthstone/mcp_agent_mail/main/README.md) (main branch, fetched 2026-09-04)
**Purpose:** Behavioral reference for DevFleet's mail-like coordination and reservation semantics (SPEC §3.2, §3.3, §5.5). DevFleet does **not** adopt MCP Agent Mail as a component — SPEC §6.2 explicitly defers "MCP Agent Mail as the authoritative reservation store" — it is used only to inform contract design.

## Verified behaviors (from the official README)

### Project-scoped identities
- Agents register per project: `ensure_project`, then `register_agent` with the repository absolute path as `project_key`; sending from an unregistered agent fails (`"from_agent not registered"`).
- Agent names are human-memorable adjective+noun (e.g., `GreenCastle`) chosen to keep inboxes/commit logs readable with low collision risk vs. GUIDs.
- Schema: `agents(id, project_id, name, program, model, task_description, inception_ts, last_active_ts, ...)`.

### Inbox / thread semantics
- `send_message(project_key, sender_name, to[], subject, body_md, cc?, bcc?, ..., importance?, ack_required?, thread_id?)` writes a canonical message plus per-recipient inbox copies and a sender outbox copy.
- `reply_message(..., message_id, ...)` preserves or creates the thread: replies inherit `thread_id` from the original; if missing, the reply's `thread_id` is set to the original message id. Subjects are `Re:`-prefixed for readability.
- `fetch_inbox(project_key, agent_name, ..., unread_only?, urgent_only?, since_ts?)` is a **non-mutating** inbox read; `unread_only=true` filters on the recipient's `read_ts IS NULL`.
- Threads are addressable as resources (`resource://thread/{id}`) and summarizable (`summarize_thread`).

### Read and acknowledgement state
- Per-recipient state is tracked in `message_recipients(message_id, agent_id, kind, read_ts, ack_ts)` — read and ack are **distinct timestamps per recipient**, not per message.
- `mark_message_read` sets only the read receipt; `acknowledge_message` sets **both** ack and read (`{acknowledged, acknowledged_at, read_at}`).
- `ack_required=true` on send enables overdue-ack tracking (`resource://views/ack-overdue/...`, `ACK_TTL_*` config, optional escalation). Overdue-ack scanning/escalation is disabled by default (`ACK_TTL_ENABLED=false`).

### File reservation leases
- `file_reservation_paths(project_key, agent_name, paths[], ttl_seconds?, exclusive?, reason?)` returns `{granted, conflicts}` — **reservations are always granted even when conflicts exist; conflicts are reported alongside grants**.
- Leases are TTL-based with `exclusive` and `reason` fields; `renew_file_reservations` extends TTL; `release_file_reservations` releases (artifacts remain in Git for audit; DB tracks `released_ts`).
- Conflict detection is per exact path pattern; shared reservations can coexist; exclusive-vs-overlap conflicts are surfaced.
- Stale leases: the server evaluates staleness via agent-inactivity + mail/filesystem/git silence heuristics and can auto-release abandoned locks; `force_release_file_reservation` clears a stale lease and **notifies the previous holder by message**.

### Handoff-related messaging
- There is **no first-class reservation-transfer operation**. Ownership change is a convention: the blocked agent sends an in-thread message, the current holder calls `release_file_reservations`, and the requester re-reserves. `force_release_file_reservation` + holder notification is the closest built-in flow.
- Contact policy (`open`/`auto`/`contacts_only`/`block_all`) gates cross-agent messaging; `auto` allows messaging when there is shared context (same thread, overlapping active reservations, or recent prior contact within a TTL). Blocked sends are **not queued** — they fail loud with `CONTACT_REQUIRED`.

### Advisory vs. enforced guarantees
- Reservations are **advisory by design**: "Agents coordinate asynchronously; hard locks create head-of-line blocking and brittle failures."
- Optional enforcement layers only:
  - A client-side Git **pre-commit hook** blocks commits conflicting with other agents' active exclusive reservations (requires `AGENT_NAME` set locally; bypassable).
  - `FILE_RESERVATIONS_ENFORCEMENT_ENABLED=true` blocks conflicting writes only to the server's own mail-archive paths, not project source files.
- Auditability: canonical artifacts (messages, reservation JSON) are committed to a Git archive; SQLite + FTS5 is an index/query layer, not the source of truth.

## Mapping to the DevFleet SPEC

Reusable semantics DevFleet should adopt:

| Agent Mail behavior | DevFleet contract (SPEC) |
|---|---|
| Project-scoped `register_agent` with human-readable names | Agent Identity, unique per project (§5.5, §7) |
| `send_message` / `reply_message` thread inheritance (`thread_id` fallback to original message id) | Request Thread message/reply semantics (§3.3) |
| Per-recipient `read_ts` / `ack_ts`; ack implies read | Read receipts and acknowledgements (§5.5: "send, receive, reply to, and acknowledge") |
| `ack_required` + overdue-ack views | Attention inbox / blocked-agent surfacing (§1.10, §8) |
| `force_release_file_reservation` + notify previous holder | Reservation recovery inspection before reuse (§5.5) |
| TTL leases with `renew` / `release` | Time-limited reservations (§7) |
| Git-backed canonical artifacts + SQLite index | Event/message/reservation history persistence (§5.7) — DevFleet uses SQLite as authoritative store instead |

## Required deviations (SPEC is authoritative and strict)

1. **Reservations are server-enforced, not advisory.** SPEC §3.2: "Reservations are mandatory, server-enforced leases. They are not merely prompt instructions or advisory records." Unlike Agent Mail's "grant + report conflicts", a conflicting reservation request in DevFleet must be **denied before the second agent writes** (§5.5) and surfaced as blocked. The Control Plane Reservation Authority (§8) is the single decision point.
2. **Fencing tokens are mandatory.** DevFleet requires a monotonically increasing Fencing Token on every mutation and must reject stale tokens (§5.5). Agent Mail has no fencing-token concept.
3. **First-class atomic handoff.** DevFleet must support atomic multi-file reservation and **atomic ownership transfer** via a reservation handoff request (§5.5). Agent Mail's release-and-re-reserve convention is not atomic and is insufficient.
4. **No Git-archive source of truth.** DevFleet's authoritative state is EF Core/SQLite in the Control Plane (§6.1); Git state is supervisor-owned (§3.7). Agent Mail's Git-committed mailbox artifacts are not replicated.
5. **No client-side enforcement fallback.** Agent Mail's optional pre-commit hook conflicts with SPEC §3.7 (agents must not run repo-wide Git commands); enforcement lives exclusively in the Control Plane.
6. **No contact-policy layer.** DevFleet agents are supervisor-managed within a project; there is no cross-project contact handshake requirement in the PoC (multi-user/multi-node deferred, §6.2).
7. **Supervisor-owned recovery.** Stale/expired reservation handling passes through Control Plane recovery inspection before reuse (§5.5), not heuristic auto-release by peer agents.
