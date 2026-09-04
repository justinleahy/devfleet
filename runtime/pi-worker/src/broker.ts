/**
 * Correlated bidirectional request broker (SPEC.md section 24).
 *
 * The worker sends `request` frames to the node (custom orchestration tools)
 * and resolves their promises when the matching correlated `response` frame
 * arrives. Nothing here blocks stdin: requests are outstanding promises, the
 * stdin loop keeps draining while a prompt or a tool call streams.
 */
import { newMessageId } from "./ids.ts";
import { PROTOCOL_VERSION, type Envelope } from "./protocol.ts";

/** Default deadline for a node round-trip before the request rejects. */
export const DEFAULT_REQUEST_TIMEOUT_MS = 30_000;

/** Hard cap on error strings carried in responses (bounded errors). */
export const MAX_ERROR_CHARS = 2_000;

export interface PendingRequest {
  resolve: (result: unknown) => void;
  reject: (cause: Error) => void;
  timer: NodeJS.Timeout;
}

/** Truncate an error message so protocol frames stay bounded. */
export function boundedError(message: string): string {
  return message.length <= MAX_ERROR_CHARS
    ? message
    : message.slice(0, MAX_ERROR_CHARS) + "…[truncated]";
}

/**
 * Tracks in-flight worker→node requests by messageId and turns correlated
 * response frames back into promise resolutions.
 */
export class RequestBroker {
  private readonly pending = new Map<string, PendingRequest>();

  private readonly send: (envelope: Envelope) => void;
  private readonly defaultTimeoutMs: number;

  constructor(
    send: (envelope: Envelope) => void,
    defaultTimeoutMs: number = DEFAULT_REQUEST_TIMEOUT_MS,
  ) {
    this.send = send;
    this.defaultTimeoutMs = defaultTimeoutMs;
  }
  request(
    sessionId: string,
    type: string,
    payload: unknown,
    timeoutMs: number = this.defaultTimeoutMs,
  ): Promise<unknown> {
    const messageId = newMessageId();
    return new Promise<unknown>((resolve, reject) => {
      const timer = setTimeout(() => {
        this.pending.delete(messageId);
        reject(
          new Error(
            boundedError(
              `request ${type} (${messageId}) timed out after ${timeoutMs}ms`,
            ),
          ),
        );
      }, timeoutMs);
      timer.unref?.();
      this.pending.set(messageId, { resolve, reject, timer });
      this.send({
        protocolVersion: PROTOCOL_VERSION,
        messageId,
        kind: "request",
        sessionId,
        type,
        payload,
      });
    });
  }

  /**
   * Route an inbound `response` frame to its pending request.
   * Returns false when no matching request exists (stale or unknown id).
   */
  handleResponse(envelope: Envelope): boolean {
    const entry = this.pending.get(envelope.messageId);
    if (entry === undefined) {
      return false;
    }
    this.pending.delete(envelope.messageId);
    clearTimeout(entry.timer);
    const payload = (envelope.payload ?? {}) as Record<string, unknown>;
    if (payload["ok"] === true) {
      entry.resolve(payload["result"]);
      return true;
    }
    const error = payload["error"];
    const message =
      typeof error === "string"
        ? error
        : typeof (error as Record<string, unknown> | undefined)?.["message"] ===
            "string"
          ? String((error as Record<string, unknown>)["message"])
          : `request ${envelope.type} failed`;
    entry.reject(new Error(boundedError(message)));
    return true;
  }

  /** Reject every outstanding request (shutdown / channel loss). */
  rejectAll(cause: Error): void {
    for (const [messageId, entry] of this.pending) {
      clearTimeout(entry.timer);
      entry.reject(cause);
      this.pending.delete(messageId);
    }
  }

  /** Number of requests currently awaiting a correlated response. */
  get size(): number {
    return this.pending.size;
  }
}
