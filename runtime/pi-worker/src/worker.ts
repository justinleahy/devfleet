/**
 * Worker request surface (SPEC.md section 24).
 *
 * Handles the Node→worker request types `session.start`, `session.input`,
 * `session.cancel`, `session.snapshot`, and `goodbye`, emits `heartbeat`
 * frames, and returns bounded error responses for every failure path.
 * Prompts never block the frame loop: `session.input` is acknowledged
 * immediately and the SDK run continues in the background, streaming
 * normalized events.
 */
import { RequestBroker, boundedError } from "./broker.ts";
import { EventNormalizer } from "./events.ts";
import { newMessageId } from "./ids.ts";
import type {
  PiSessionLike,
  PiSessionFactory,
  RootSessionConfig,
} from "./pisession.ts";
import {
  PROTOCOL_VERSION,
  type Envelope,
  type FrameKind,
} from "./protocol.ts";
import { ROOT_SYSTEM_PROMPT } from "./rootTools.ts";

/** Interval between heartbeat frames while a session is active. */
export const HEARTBEAT_INTERVAL_MS = 15_000;

export interface WorkerOptions {
  factory: PiSessionFactory;
  /** Protocol frame sink (writes to stdout in production). */
  send: (envelope: Envelope) => void;
  heartbeatIntervalMs?: number;
  requestTimeoutMs?: number;
  now?: () => number;
}

interface StartedSession {
  session: PiSessionLike;
  normalizer: EventNormalizer;
  /** Child worker mode for this session; root when false. */
  isChild: boolean;
  /** Parent session id reported in `session.registered` for children. */
  parentSessionId: string | null;
  /** Last `submit_child_result` payload, kept for the completion event. */
  childResult: unknown;
}

function errorEnvelope(
  received: Envelope,
  message: string,
  code: string,
): Envelope {
  return {
    protocolVersion: PROTOCOL_VERSION,
    messageId: received.messageId,
    kind: "response",
    sessionId: received.sessionId,
    type: received.type,
    payload: { ok: false, error: { code, message: boundedError(message) } },
  };
}

function okEnvelope(
  received: Envelope,
  payload: Record<string, unknown>,
): Envelope {
  return {
    protocolVersion: PROTOCOL_VERSION,
    messageId: received.messageId,
    kind: "response",
    sessionId: received.sessionId,
    type: received.type,
    payload: { ok: true, ...payload },
  };
}

/** Reads protocol frames, drives the SDK session, writes protocol frames. */
export class PiWorker {
  private readonly broker: RequestBroker;
  private readonly heartbeatIntervalMs: number;
  private readonly now: () => number;
  private started: StartedSession | undefined;
  private heartbeatTimer: NodeJS.Timeout | undefined;
  private stopped = false;

  private readonly options: WorkerOptions;
  private sessionId: string | undefined;

  constructor(options: WorkerOptions) {
    this.options = options;
    this.broker = new RequestBroker(options.send, options.requestTimeoutMs);
    this.heartbeatIntervalMs =
      options.heartbeatIntervalMs ?? HEARTBEAT_INTERVAL_MS;
    this.now = options.now ?? (() => Date.now());
  }

  /**
   * Send one correlated request to the node on behalf of a custom
   * orchestration tool. Resolves with the node's result payload.
   */
  requestFromNode(
    type: string,
    payload: Record<string, unknown>,
  ): Promise<unknown> {
    if (this.sessionId === undefined) {
      return Promise.reject(new Error("no active session"));
    }
    return this.broker.request(this.sessionId, type, payload);
  }

  /** Currently running SDK session, if `session.start` succeeded. */
  get session(): PiSessionLike | undefined {
    return this.started?.session;
  }

  /**
   * Handle one inbound frame. Always resolves; failures become bounded error
   * responses correlated with the incoming messageId.
   */
  async handleFrame(envelope: Envelope): Promise<void> {
    if (this.stopped) {
      return;
    }
    if (envelope.kind === "response") {
      // Correlated answer to one of our custom-tool requests.
      if (!this.broker.handleResponse(envelope)) {
        this.log(`no pending request for response ${envelope.messageId}`);
      }
      return;
    }
    if (envelope.kind !== "request") {
      return;
    }
    try {
      await this.dispatch(envelope);
    } catch (cause) {
      const message = cause instanceof Error ? cause.message : String(cause);
      this.log(`request ${envelope.type} failed: ${message}`);
      this.options.send(errorEnvelope(envelope, message, "WORKER_ERROR"));
    }
  }

  /** Flush pending deltas, reject outstanding tool requests, stop heartbeat. */
  stop(): void {
    this.stopped = true;
    this.stopHeartbeat();
    this.started?.normalizer.flushDelta();
    this.broker.rejectAll(new Error("worker stopped"));
  }

  private log(message: string): void {
    process.stderr.write(`pi-worker: ${message}\n`);
  }

  private async dispatch(envelope: Envelope): Promise<void> {
    switch (envelope.type) {
      case "session.start":
        await this.startSession(envelope);
        return;
      case "session.input":
        await this.input(envelope);
        return;
      case "session.cancel":
        await this.cancel(envelope);
        return;
      case "session.snapshot":
        this.snapshot(envelope);
        return;
      default:
        this.options.send(
          errorEnvelope(
            envelope,
            `unknown request type ${envelope.type}`,
            "UNKNOWN_REQUEST",
          ),
        );
    }
  }

  private async startSession(envelope: Envelope): Promise<void> {
    const payload = (envelope.payload ?? {}) as Record<string, unknown>;
    const cwd = typeof payload["cwd"] === "string" ? payload["cwd"] : process.cwd();
    const agentDir =
      typeof payload["agentDir"] === "string" ? payload["agentDir"] : undefined;
    if (agentDir === undefined) {
      this.options.send(
        errorEnvelope(envelope, "session.start requires agentDir", "MISSING_FIELD"),
      );
      return;
    }
    const isChild = payload["mode"] === "child";
    const parentSessionId =
      typeof payload["parentSessionId"] === "string"
        ? payload["parentSessionId"]
        : null;
    if (isChild && parentSessionId === null) {
      this.options.send(
        errorEnvelope(
          envelope,
          "child session.start requires parentSessionId",
          "MISSING_FIELD",
        ),
      );
      return;
    }
    const config: RootSessionConfig = {
      sessionId: envelope.sessionId,
      cwd,
      agentDir,
      ...(typeof payload["model"] === "string" ? { model: payload["model"] } : {}),
      ...(typeof payload["thinkingLevel"] === "string"
        ? { thinkingLevel: payload["thinkingLevel"] }
        : {}),
      ...(typeof payload["systemPrompt"] === "string"
        ? { systemPrompt: payload["systemPrompt"] }
        : {}),
      ...(isChild ? { mode: "child" as const } : {}),
      ...(isChild && parentSessionId !== null ? { parentSessionId } : {}),
    };
    const session = await this.options.factory.create(config);
    const normalizer = new EventNormalizer(
      envelope.sessionId,
      (frame) => this.options.send(frame.envelope),
      this.now,
    );
    const started: StartedSession = {
      session,
      normalizer,
      isChild,
      parentSessionId,
      childResult: undefined,
    };
    session.subscribe((event) => {
      normalizer.handle(event);
      this.observeChildEvent(started, event);
    });
    this.started = started;
    this.sessionId = envelope.sessionId;
    this.startHeartbeat(envelope.sessionId);
    if (isChild) {
      // Hierarchy event: children announce their parent immediately after
      // registration so the node can build the agent tree.
      normalizer.emitSessionEvent("session.registered", {
        mode: "child",
        parentSessionId,
      });
    }
    this.options.send(
      okEnvelope(envelope, {
        sdkSessionId: session.sessionId,
        sessionFile: session.sessionFile ?? null,
        agentDir,
        cwd,
        mode: isChild ? "child" : "root",
      }),
    );
  }

  /**
   * Track the child result lifecycle: capture the durable payload from
   * `submit_child_result` tool completions and normalize terminal state into
   * `session.completed` / `session.failed` events.
   */
  private observeChildEvent(started: StartedSession, event: unknown): void {
    if (!started.isChild) {
      return;
    }
    const record = (event ?? {}) as Record<string, unknown>;
    const type = typeof record["type"] === "string" ? record["type"] : "";
    if (type === "tool_execution_end" && record["toolName"] === "submit_child_result") {
      started.childResult = record["response"] ?? null;
      return;
    }
    if (type === "agent_end") {
      const failed = record["isError"] === true;
      started.normalizer.emitSessionEvent(
        failed ? "session.failed" : "session.completed",
        {
          ...(failed
            ? { error: boundedError(String(record["error"] ?? "child run failed")) }
            : {}),
          result: started.childResult ?? null,
        },
      );
      started.childResult = undefined;
    }
  }

  private async input(envelope: Envelope): Promise<void> {
    const started = this.requireStarted(envelope);
    if (started === undefined) {
      return;
    }
    const payload = (envelope.payload ?? {}) as Record<string, unknown>;
    const text = typeof payload["text"] === "string" ? payload["text"] : "";
    if (text.length === 0) {
      this.options.send(
        errorEnvelope(envelope, "session.input requires text", "MISSING_FIELD"),
      );
      return;
    }
    if (started.session.isStreaming) {
      // Mid-run guidance: queue as steering without touching the running turn.
      await started.session.steer(text);
      this.options.send(okEnvelope(envelope, { queued: "steer" }));
      return;
    }
    // Fire the prompt without awaiting: the response is written immediately
    // and the stdin loop keeps draining while the run streams events.
    void started.session.prompt(text).catch((cause: unknown) => {
      const message = cause instanceof Error ? cause.message : String(cause);
      this.log(`prompt failed: ${message}`);
      started.normalizer.handle({
        type: "worker_error",
        message: boundedError(message),
      });
      if (started.isChild) {
        started.normalizer.emitSessionEvent("session.failed", {
          error: boundedError(message),
          result: null,
        });
      }
    });
    this.options.send(okEnvelope(envelope, { queued: "prompt" }));
  }

  private async cancel(envelope: Envelope): Promise<void> {
    const started = this.requireStarted(envelope);
    if (started === undefined) {
      return;
    }
    await started.session.abort();
    this.options.send(okEnvelope(envelope, { cancelled: true }));
  }

  private snapshot(envelope: Envelope): void {
    const started = this.requireStarted(envelope);
    if (started === undefined) {
      return;
    }
    const messages = started.session.messages;
    const last = messages.at(-1) as Record<string, unknown> | undefined;
    this.options.send(
      okEnvelope(envelope, {
        sdkSessionId: started.session.sessionId,
        sessionFile: started.session.sessionFile ?? null,
        isStreaming: started.session.isStreaming,
        messageCount: messages.length,
        lastMessage: last === undefined ? null : last["role"] ?? null,
        seq: started.normalizer.currentSeq(),
      }),
    );
  }

  private requireStarted(envelope: Envelope): StartedSession | undefined {
    if (this.started === undefined) {
      this.options.send(
        errorEnvelope(envelope, "no active session", "NO_SESSION"),
      );
      return undefined;
    }
    return this.started;
  }

  private startHeartbeat(sessionId: string): void {
    this.stopHeartbeat();
    this.heartbeatTimer = setInterval(() => {
      this.options.send({
        protocolVersion: PROTOCOL_VERSION,
        messageId: newMessageId(),
        kind: "heartbeat" satisfies FrameKind,
        sessionId,
        type: "heartbeat",
        payload: { timestamp: this.now() },
      });
    }, this.heartbeatIntervalMs);
    this.heartbeatTimer.unref?.();
  }

  private stopHeartbeat(): void {
    if (this.heartbeatTimer !== undefined) {
      clearInterval(this.heartbeatTimer);
      this.heartbeatTimer = undefined;
    }
  }
}
