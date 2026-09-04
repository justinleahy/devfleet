/**
 * Normalized event mapping (SPEC.md sections 24.3 and 25).
 *
 * Structured SDK session events are mapped onto versioned protocol `event`
 * frames. Every frame carries a strictly increasing per-session `seq` and a
 * globally unique `messageId`. Streaming text/thinking deltas are sampled and
 * coalesced on a short window; lifecycle, tool, error, and completion events
 * are never dropped or deferred.
 */
import { newMessageId } from "./ids.ts";
import { PROTOCOL_VERSION, type Envelope } from "./protocol.ts";

/** Window (ms) over which consecutive text/thinking deltas are coalesced. */
export const DELTA_FLUSH_MS = 120;

/** Hard cap on any single string field inside an event payload. */
export const MAX_EVENT_FIELD_CHARS = 4_000;

const DELTA_EVENT = "message.delta";

/** SDK `message_update` subtypes that accumulate into one coalesced frame. */
const COALESCED_UPDATES: Record<string, true> = {
  text_delta: true,
  thinking_delta: true,
};

export interface NormalizedFrame {
  envelope: Envelope;
  /** True when this frame is a coalesced delta and may be sampled further. */
  isDelta: boolean;
}

/** Truncate a value for bounded emission: never drops the event itself. */
function bounded(value: unknown): unknown {
  if (typeof value === "string") {
    return value.length <= MAX_EVENT_FIELD_CHARS
      ? value
      : value.slice(0, MAX_EVENT_FIELD_CHARS) + "…[truncated]";
  }
  if (Array.isArray(value)) {
    return value.slice(0, 100).map(bounded);
  }
  if (typeof value === "object" && value !== null) {
    const out: Record<string, unknown> = {};
    for (const [key, item] of Object.entries(value as Record<string, unknown>)) {
      out[key] = bounded(item);
    }
    return out;
  }
  return value;
}

/** SDK event type → normalized event type. Unlisted types pass through. */
export function normalizeEventType(sdkType: string): string {
  switch (sdkType) {
    case "agent_start":
      return "agent.started";
    case "agent_end":
      return "agent.completed";
    case "turn_start":
      return "turn.started";
    case "turn_end":
      return "turn.completed";
    case "message_start":
      return "message.started";
    case "message_end":
      return "message.completed";
    case "tool_execution_start":
      return "tool.started";
    case "tool_execution_update":
      return "tool.updated";
    case "tool_execution_end":
      return "tool.completed";
    case "queue_update":
      return "queue.updated";
    case "compaction_start":
      return "compaction.started";
    case "compaction_end":
      return "compaction.completed";
    case "auto_retry_start":
      return "retry.started";
    case "auto_retry_end":
      return "retry.completed";
    default:
      return `sdk.${sdkType}`;
  }
}

interface PendingDelta {
  text: string;
  thinking: string;
  firstSeq: number;
}

/**
 * Per-session event normalizer. One instance per started session; sequence
 * numbers start at 1 and increase by exactly one per emitted frame.
 */
export class EventNormalizer {
  private seq = 0;
  private pending: PendingDelta | null = null;
  private flushTimer: NodeJS.Timeout | undefined;

  private readonly sessionId: string;
  private readonly emit: (frame: NormalizedFrame) => void;
  private readonly now: () => number;

  constructor(
    sessionId: string,
    emit: (frame: NormalizedFrame) => void,
    now: () => number = () => Date.now(),
  ) {
    this.sessionId = sessionId;
    this.emit = emit;
    this.now = now;
  }

  /** Feed one raw SDK session event. Safe for unknown event shapes. */
  handle(sdkEvent: unknown): void {
    const record = (sdkEvent ?? {}) as Record<string, unknown>;
    const sdkType =
      typeof record["type"] === "string" ? record["type"] : "unknown";

    if (sdkType === "message_update" && this.tryAccumulateDelta(record)) {
      this.scheduleDeltaFlush();
      return;
    }
    // Any non-delta event flushes pending deltas first to preserve order.
    this.flushDelta();
    this.emitEvent(normalizeEventType(sdkType), bounded(sdkEvent), false);
  }

  /** Emit any coalesced delta still buffered (call before shutdown). */
  flushDelta(): void {
    if (this.flushTimer !== undefined) {
      clearTimeout(this.flushTimer);
      this.flushTimer = undefined;
    }
    const pending = this.pending;
    this.pending = null;
    if (pending === null) {
      return;
    }
    this.emitEvent(
      DELTA_EVENT,
      {
        textDelta: pending.text,
        thinkingDelta: pending.thinking,
      },
      true,
    );
  }

  /** Current sequence value; the next emitted frame uses this + 1. */
  currentSeq(): number {
    return this.seq;
  }

  private tryAccumulateDelta(record: Record<string, unknown>): boolean {
    const update = record["assistantMessageEvent"] as
      | Record<string, unknown>
      | undefined;
    const updateType =
      typeof update?.["type"] === "string" ? update["type"] : undefined;
    if (
      update === undefined ||
      updateType === undefined ||
      COALESCED_UPDATES[updateType] !== true
    ) {
      return false;
    }
    const delta = typeof update["delta"] === "string" ? update["delta"] : "";
    if (this.pending === null) {
      this.pending = { text: "", thinking: "", firstSeq: this.seq + 1 };
    }
    if (updateType === "text_delta") {
      this.pending.text += delta;
    } else {
      this.pending.thinking += delta;
    }
    return true;
  }

  private scheduleDeltaFlush(): void {
    if (this.flushTimer !== undefined) {
      return;
    }
    this.flushTimer = setTimeout(() => {
      this.flushTimer = undefined;
      this.flushDelta();
    }, DELTA_FLUSH_MS);
    this.flushTimer.unref?.();
  }

  private emitEvent(type: string, data: unknown, isDelta: boolean): void {
    this.seq += 1;
    this.emit({
      envelope: {
        protocolVersion: PROTOCOL_VERSION,
        messageId: newMessageId(),
        kind: "event",
        sessionId: this.sessionId,
        type,
        payload: { seq: this.seq, timestamp: this.now(), data },
      },
      isDelta,
    });
  }
}
