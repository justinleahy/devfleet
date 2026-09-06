/**
 * Behavioral tests for the worker over a fake SDK session and in-memory
 * transport: start/input/cancel/snapshot, non-blocking prompts, delta
 * coalescing, strict sequence numbers, correlated custom-tool round-trips,
 * heartbeats, and bounded error responses (SPEC.md section 24).
 */
import assert from "node:assert/strict";
import { describe, it } from "node:test";
import type { Envelope } from "../src/protocol.ts";
import { PiWorker } from "../src/worker.ts";
import { buildRootTools, ROOT_TOOL_NAMES, TOOL_REQUEST_TYPES } from "../src/rootTools.ts";
import type { PiSessionFactory, PiSessionLike } from "../src/pisession.ts";


interface FakeSession {
  sessionId: string;
  sessionFile: string | undefined;
  isStreaming: boolean;
  messages: unknown[];
  prompts: string[];
  steers: string[];
  aborted: boolean;
  emit(event: unknown): void;
  subscribe(listener: (event: unknown) => void): () => void;
  prompt(text: string, options?: { streamingBehavior?: "steer" | "followUp" }): Promise<void>;
  steer(text: string): Promise<void>;
  followUp(text?: string): Promise<void>;
  abort(): Promise<void>;
}
function fakeSession(overrides: Partial<PiSessionLike> = {}) : FakeSession {
  const listeners: Array<(event: unknown) => void> = [];
  const session = {
    sessionId: "sdk-1",
    sessionFile: "/data/agent/sessions/s.jsonl",
    isStreaming: false,
    messages: [{ role: "user" }] as unknown[],
    prompts: [] as string[],
    steers: [] as string[],
    aborted: false,
    subscribe(listener: (event: unknown) => void) {
      listeners.push(listener);
      return () => {
        listeners.splice(listeners.indexOf(listener), 1);
      };
    },
    async prompt(text: string) {
      session.prompts.push(text);
      session.isStreaming = true;
      for (const listener of listeners) {
        listener({ type: "agent_start" });
      }
      session.isStreaming = false;
    },
    async steer(text: string) {
      session.steers.push(text);
    },
    async followUp() {},
    async abort() {
      session.aborted = true;
    },
    emit(event: unknown) {
      for (const listener of listeners) {
        listener(event);
      }
    },
    ...overrides,
  };
  return session;
}

interface Harness {
  worker: PiWorker;
  frames: Envelope[];
  session: ReturnType<typeof fakeSession>;
  send(payload: unknown): void;
  framesOfKind(kind: string): Envelope[];
  lastResponse(): Envelope;
}

async function harness(options?: {
  streamingSession?: boolean;
}): Promise<Harness> {
  const frames: Envelope[] = [];
  const session = fakeSession();
  if (options?.streamingSession === true) {
    session.isStreaming = true;
  }
  const factory: PiSessionFactory = {
    async create() {
      return session;
    },
  };
  const worker = new PiWorker({
    factory,
    send: (envelope) => frames.push(envelope),
    heartbeatIntervalMs: 5,
  });
  await worker.handleFrame({
    protocolVersion: 1,
    messageId: "m-start",
    kind: "request",
    sessionId: "s-1",
    type: "session.start",
    payload: { cwd: "/repo", agentDir: "/data/agent" },
  });
  return {
    worker,
    frames,
    session,
    send(payload: unknown) {
      session.emit({
        type: "tool_execution_end",
        toolName: "spawn_agent",
        response: payload,
      });
    },
    framesOfKind(kind: string) {
      return frames.filter((frame) => frame.kind === kind);
    },
    lastResponse() {
      return frames.filter((frame) => frame.kind === "response").at(-1)!;
    },
  };
}

function requestFrame(type: string, messageId: string): Envelope {
  return {
    protocolVersion: 1,
    messageId,
    kind: "request",
    sessionId: "s-1",
    type,
    payload: {},
  };
}

describe("session lifecycle", () => {
  it("start creates a session and reports persistence location", async () => {
    const h = await harness();
    const response = h.frames.find((frame) => frame.kind === "response")!;
    assert.equal(response.type, "session.start");
    assert.equal(response.payload && (response.payload as Record<string, unknown>)["ok"], true);
    assert.equal(
      (response.payload as Record<string, unknown>)["sessionFile"],
      "/data/agent/sessions/s.jsonl",
    );
  });

  it("input without an active session yields a bounded error response", async () => {
    const frames: Envelope[] = [];
    const worker = new PiWorker({
      factory: { async create() { return fakeSession(); } },
      send: (envelope) => frames.push(envelope),
    });
    await worker.handleFrame(requestFrame("session.input", "m1"));
    const response = frames.at(-1)!;
    assert.equal((response.payload as Record<string, unknown>)["ok"], false);
    assert.match(JSON.stringify(response.payload), /no active session/);
  });

  it("unknown request types get an error response, not a crash", async () => {
    const h = await harness();
    await h.worker.handleFrame(requestFrame("agent.spawn.local", "m2"));
    assert.equal((h.lastResponse().payload as Record<string, unknown>)["ok"], false);
  });
});

describe("non-blocking input", () => {
  it("acknowledges input immediately and keeps draining frames while the prompt runs", async () => {
    const h = await harness();
    await h.worker.handleFrame({
      protocolVersion: 1,
      messageId: "m3",
      kind: "request",
      sessionId: "s-1",
      type: "session.input",
      payload: { text: "plan the work" },
    });
    assert.equal(
      (h.lastResponse().payload as Record<string, unknown>)["queued"],
      "prompt",
    );
    // The frame loop is free: a snapshot is answered while the prompt is in flight.
    await h.worker.handleFrame(requestFrame("session.snapshot", "m4"));
    assert.equal(h.lastResponse().type, "session.snapshot");
  });

  it("steers instead of prompting while streaming", async () => {
    const h = await harness({ streamingSession: true });
    h.session.isStreaming = true;
    await h.worker.handleFrame({
      protocolVersion: 1,
      messageId: "m5",
      kind: "request",
      sessionId: "s-1",
      type: "session.input",
      payload: { text: "change course" },
    });
    assert.deepEqual(h.session.steers, ["change course"]);
    assert.equal(
      (h.lastResponse().payload as Record<string, unknown>)["queued"],
      "steer",
    );
  });

  it("cancel aborts the SDK session", async () => {
    const h = await harness();
    await h.worker.handleFrame(requestFrame("session.cancel", "m6"));
    assert.equal(h.session.aborted, true);
    assert.equal((h.lastResponse().payload as Record<string, unknown>)["cancelled"], true);
  });
});

describe("event normalization", () => {
  it("coalesces deltas, never drops lifecycle events, and numbers seq strictly", async () => {
    const h = await harness();
    h.session.emit({ type: "agent_start" });
    h.session.emit({ type: "message_update", assistantMessageEvent: { type: "text_delta", delta: "Hel" } });
    h.session.emit({ type: "message_update", assistantMessageEvent: { type: "text_delta", delta: "lo" } });
    h.session.emit({ type: "agent_end", messages: [] });
    // Flush the coalescing window.
    await new Promise((resolve) => setTimeout(resolve, 200));

    const events = h.framesOfKind("event");
    const types = events.map((frame) => frame.type);
    // The pending delta flushes ahead of the lifecycle event to keep order.
    assert.deepEqual(types, ["agent.started", "message.delta", "agent.completed"]);
    const seqs = events.map((frame) => (frame.payload as Record<string, unknown>)["seq"]);
    assert.deepEqual(seqs, [1, 2, 3]);
    const delta = events[1]!;
    assert.equal(
      ((delta.payload as Record<string, unknown>)["data"] as Record<string, unknown>)["textDelta"],
      "Hello",
    );
    const ids = new Set(events.map((frame) => frame.messageId));
    assert.equal(ids.size, events.length, "event ids must be unique");
  });

  it("maps tool start, update, and end to started, progress, and completed with tool identity", async () => {
    const h = await harness();
    h.session.emit({ type: "tool_execution_start", toolName: "read_file" });
    h.session.emit({ type: "tool_execution_update", toolName: "read_file" });
    h.session.emit({ type: "tool_execution_end", toolName: "read_file" });

    const events = h.framesOfKind("event");
    const types = events.map((frame) => frame.type);
    assert.deepEqual(types, ["tool.started", "tool.progress", "tool.completed"]);
    assert.equal(types.includes("tool.updated"), false);
    for (const frame of events) {
      const data = (frame.payload as Record<string, unknown>)["data"] as Record<string, unknown>;
      assert.equal(data["toolName"], "read_file");
    }
  });
});

describe("heartbeat and goodbye", () => {
  it("sends heartbeat frames while a session is active", async () => {
    const h = await harness();
    await new Promise((resolve) => setTimeout(resolve, 30));
    assert.ok(h.framesOfKind("heartbeat").length >= 1);
    h.worker.stop();
  });
});

describe("custom tool round-trip", () => {
  it("resolves a tool request when the correlated response arrives", async () => {
    const h = await harness();
    const toolPromise = h.worker.requestFromNode("agent.spawn", {
      role: "implementer",
    });
    // The request frame went out on the transport.
    const outgoing = h.frames.at(-1)!;
    assert.equal(outgoing.kind, "request");
    assert.equal(outgoing.type, "agent.spawn");
    // Node answers with a correlated response frame.
    await h.worker.handleFrame({
      protocolVersion: 1,
      messageId: outgoing.messageId,
      kind: "response",
      sessionId: "s-1",
      type: "agent.spawn",
      payload: { ok: true, result: { agentSessionId: "child-1" } },
    });
    assert.deepEqual(await toolPromise, { agentSessionId: "child-1" });
  });

  it("builds exactly the fifteen SPEC tools with matching request types", async () => {
    let captured: Array<{ type: string; payload: Record<string, unknown> }> = [];
    const tools = buildRootTools(async (type, payload) => {
      captured.push({ type, payload });
      return { ok: true };
    });
    const ORCHESTRATION_TOOLS = [
      "create_plan", "revise_plan", "spawn_agent", "spawn_agents",
      "get_agent_status", "await_agent", "send_agent_message", "read_agent_inbox",
      "acknowledge_message", "request_reservation_handoff", "cancel_agent",
      "inspect_project_diff", "request_verification", "submit_completion", "block_request",
    ];
    const names = tools.map((tool) => tool.name);
    for (const name of ORCHESTRATION_TOOLS) {
      assert.ok(names.includes(name), `missing orchestration tool ${name}`);
    }
    // Read-only built-ins round-trip as workspace.* requests.
    assert.deepEqual(
      ORCHESTRATION_TOOLS.map((name) => TOOL_REQUEST_TYPES[name]),
      ["plan.submit", "plan.revise", "agent.spawn", "agent.spawn", "agent.status",
        "agent.await", "agent.message.send", "agent.inbox.read", "agent.message.acknowledge",
        "reservation.handoff.request", "agent.cancel", "project.diff.inspect",
        "verification.request", "request.complete", "request.block"],
    );
    assert.equal(names.length, ROOT_TOOL_NAMES.length);
    for (const tool of tools) {
      const result = await tool.execute({ probe: 1 });
      assert.match(result as string, /"ok":true/);
    }
    assert.equal(captured.length, tools.length);
    for (const entry of captured) {
      assert.equal(TOOL_REQUEST_TYPES[Object.entries(TOOL_REQUEST_TYPES).find(([, t]) => t === entry.type)![0]], entry.type);
    }
  });

  it("maps request_verification to parameterless verification.request", async () => {
    const captured: Array<{ type: string; payload: Record<string, unknown> }> = [];
    const tools = buildRootTools(async (type, payload) => {
      captured.push({ type, payload });
      return { type };
    });
    const tool = tools.find((entry) => entry.name === "request_verification");
    assert.ok(tool, "request_verification tool must exist");
    assert.equal(TOOL_REQUEST_TYPES["request_verification"], "verification.request");
    assert.deepEqual(Object.keys(tool.properties), []);
    const roundTrip = await tool.execute({});
    assert.match(roundTrip, /verification\.request/);
    assert.equal(captured[0]!.type, "verification.request");
    assert.equal("profileId" in captured[0]!.payload, false);
    assert.equal("commandId" in captured[0]!.payload, false);
  });
});
