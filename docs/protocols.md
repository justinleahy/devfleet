# Protocols

Two versioned transports: Pi worker stdio (protocol v1) and Control Plane SignalR `/nodeHub`.

## Pi worker protocol v1

Process: `node` launches `runtime/pi-worker/src/index.ts` (`Pi:WorkerPath`, `Pi:NodeExecutable`).

- Stdin/stdout: strict NDJSON (LF; CRLF accepted on decode). Logs **only** on stderr.
- `protocolVersion`: `1` (`PROTOCOL_VERSION` / `PiProtocol.Version`).
- Maximum frame size: **1 048 576 bytes (1 MiB)** UTF-8, **excluding** the trailing newline (`MAX_FRAME_BYTES` / `PiProtocol.MaxFrameBytes`). Oversized frames fail with `FRAME_OVERSIZED` before JSON parse.
- Split on `\n` only (not Unicode line separators).
- Malformed frames: `FRAME_EMPTY`, `FRAME_INVALID_JSON`, `FRAME_NOT_OBJECT`, `FRAME_UNSUPPORTED_PROTOCOL_VERSION`, `FRAME_UNKNOWN_KIND`, `FRAME_MISSING_FIELD`. The worker logs and continues.

### Envelope

```json
{
  "protocolVersion": 1,
  "messageId": "01K...",
  "kind": "request",
  "sessionId": "session-01K...",
  "type": "session.start",
  "payload": {}
}
```

`messageId`, `kind`, `sessionId`, and `type` are required non-empty strings.

### Frame kinds

`hello`, `event`, `request`, `response`, `heartbeat`, `goodbye`.

Heartbeat while a session is active: 15 s from the worker. Node heartbeats to the hub are independent (`Node:HeartbeatSeconds`, default 10).

### Node → worker requests (correlated `response` by `messageId`)

Typical types: `session.start`, `session.input`, `session.cancel`, `session.snapshot`, `goodbye`. Input is acknowledged immediately; the SDK run continues in the background.

### Worker → node orchestration `request` types (SPEC §24.2)

`plan.submit`, `plan.revise`, `agent.spawn`, `agent.status`, `agent.await`, `agent.message.send`, `agent.inbox.read`, `agent.message.acknowledge`, `agent.cancel`, `reservation.acquire`, `reservation.expand`, `reservation.release`, `reservation.handoff.request`, `project.diff.inspect`, `verification.request`, `request.block`, `request.complete`.

### Events

Each `event` carries a strictly increasing per-session `seq` and a unique `messageId`. Text/thinking deltas may coalesce; lifecycle, blocker, reservation, error, and completion events are never dropped. Process crash synthesizes `session.failed` / `session.closed`.

Provider login missing: emit `session.snapshot` or `session.failed`-equivalent with blocked + input-required dimensions (see [security.md](security.md)).

## SignalR node hub (`/nodeHub`)

Outbound from the node (`Node:ControlPlaneUrl` + `/nodeHub`, default `http://127.0.0.1:5057/nodeHub`). Authenticated with the application-generated node credential (never logged or returned). Reconnect: exponential backoff capped at 30 seconds; re-`Register` on every connection.

Hub methods (one-argument DTOs in `PiCommandCenter.Contracts.NodeTransport`):

| Hub method | Request DTO | Result |
|---|---|---|
| `Register` | `NodeRegistrationMessage` (`NodeId`, `DisplayName`, `AgentVersion`, `CapabilitiesJson`) | `NodeDto` |
| `Heartbeat` | `NodeHeartbeatMessage` | `NodeDto` |
| `ClaimNext` | `ClaimRequestMessage` | `RequestClaimMessage?` |
| `RenewClaim` | `ClaimRenewalMessage` | `DateTimeOffset` |
| `PublishEvents` | `NodeEventBatchMessage` | `NodeEventAcknowledgementMessage` |
| `SendMail` / `ReplyMail` | `SendMailMessage` / `ReplyMailMessage` | `MailDeliveryMessage` |
| `FetchInbox` / `FetchThread` | `FetchMailInboxMessage` / `FetchMailThreadMessage` | `MailInboxMessage` |
| `MarkMailRead` / `AcknowledgeMail` | `MarkMailReadMessage` / `AcknowledgeMailMessage` | `MailReceiptMessage` |
| `AcquireReservation` | `AcquireReservationMessage` | `ReservationOperationResultMessage` |
| `RenewReservation` / `ReleaseReservation` | `ReservationMutationMessage` / `ReleaseReservationMessage` | `ReservationOperationResultMessage` |
| `ExpandReservation` | `ExpandReservationMessage` | `ReservationOperationResultMessage` |
| `TransferReservation` | `TransferReservationMessage` | `ReservationOperationResultMessage` |
| `AuthorizeMutation` | `MutationAuthorizationMessage` | `MutationAuthorizationResultMessage` |
| `MarkReservationRecovery` | `MarkRecoveryMessage` | `ReservationOperationResultMessage` |
| `ListReservations` | `ListReservationsMessage` | `ReservationLeaseMessage[]` |
| `RecordVerification` | `VerificationRunMessage` | `VerificationRunMessage` |
| `EvaluateCompletion` | `EvaluateCompletionMessage` | `CompletionGateDecisionMessage` |

### Server-enforced bounds (`NodeTransportLimits`)

| Limit | Value |
|---|---|
| Claim/reservation lease seconds | 10–300 |
| Event batch count | 500 |
| Event payload bytes | 256 KiB |
| Active session ids on heartbeat | 200 |
| Mail payload | 64 KiB |
| Inbox count | 200 |
| Session / verification id length | 128 |
| Verification output | 16 384 bytes |
| Artifact path | 1024 bytes |
| Completion summary | 64 KiB |
| Changed files / review findings | 500 / 200 |

Reservation errors are in-band (`ReservationErrorCodes`: `conflict`, `not_found`, `invalid_fencing_token`, `invalid_state`, `validation`, `unknown`), not raw hub exceptions. Stale fencing tokens fail mutation authorization.

### Idempotency

- `PublishEvents`: duplicate `EventId`s are acknowledged and not re-inserted (`NodeEventSink`).
- `MarkMailRead`: idempotent per recipient session.
- Reservation acquire of identical scopes for the same owner follows lease semantics (conflict vs existing lease), never silent double-grant of overlapping scopes to two owners.
- Completion evaluation is keyed by request; accepted results persist once.

### Event message

`NodeEventMessage`: `EventId`, `NodeId`, `ProjectId`, `RequestId?`, `SessionId?`, `Sequence`, `Type`, `OccurredAt`, `PayloadJson`.

Spool replay after reconnect: inventory + snapshots + unacked events in order; delete only after acknowledgement.
