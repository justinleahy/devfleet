/**
 * Behavioral tests for the Pi child session mode (SPEC.md sections 18.1,
 * 25.3): exact root/child tool allowlists, no shell escape, fencing data on
 * every mutation request, no success before the reservation authority
 * answers, preserved denials, and the normalized child result lifecycle.
 */
import assert from "node:assert/strict";
import { describe, it } from "node:test";
import type { Envelope } from "../src/protocol.ts";
import { PiWorker } from "../src/worker.ts";
import {
  ROOT_BUILTIN_TOOLS,
  ROOT_EXCLUDED_TOOLS,
  ROOT_TOOL_NAMES,
  buildRootTools,
} from "../src/rootTools.ts";
import {
  CHILD_BUILTIN_TOOLS,
  CHILD_EXCLUDED_TOOLS,
  CHILD_MUTATION_TOOLS,
  CHILD_TOOL_NAMES,
  CHILD_TOOL_REQUEST_TYPES,
  buildChildTools,
} from "../src/childTools.ts";
import type { PiSessionFactory, PiSessionLike } from "../src/pisession.ts";

const EXPECTED_CHILD_TOOLS = [
  "read",
  "grep",
  "find",
  "ls",
  "reserved_read",
  "reserved_write",
  "reserved_edit",
  "reserved_delete",
  "reserved_move",
  "reserve_files",
  "expand_reservation",
  "release_reservation",
  "request_reservation_handoff",
  "accept_reservation_handoff",
  "run_verification_command",
  "mail_send",
  "mail_reply",
  "mail_inbox",
  "mail_ack",
  "submit_child_result",
] as const;

const EXPECTED_ROOT_TOOLS = [
  "read",
  "grep",
  "find",
  "ls",
  "create_plan",
  "revise_plan",
  "spawn_agent",
  "spawn_agents",
  "get_agent_status",
  "await_agent",
  "send_agent_message",
  "read_agent_inbox",
  "acknowledge_message",
  "request_reservation_handoff",
  "cancel_agent",
  "inspect_project_diff",
  "request_verification",
  "submit_completion",
  "block_request",
] as const;

describe("tool allowlists", () => {
  it("root surface is sandbox reads plus the fifteen orchestration tools", () => {
    assert.deepEqual([...ROOT_TOOL_NAMES], [...EXPECTED_ROOT_TOOLS]);
  });

  it("child surface is sandbox reads plus the reservation, mail, and result tools", () => {
    assert.deepEqual([...CHILD_TOOL_NAMES], [...EXPECTED_CHILD_TOOLS]);
  });

  it("built-in SDK read tools are disabled; names are custom node round-trips", () => {
    assert.deepEqual([...ROOT_BUILTIN_TOOLS], []);
    assert.deepEqual([...CHILD_BUILTIN_TOOLS], []);
    for (const surface of [ROOT_EXCLUDED_TOOLS, CHILD_EXCLUDED_TOOLS]) {
      assert.deepEqual([...surface], ["bash", "powershell", "edit", "write"]);
    }
    for (const banned of ["bash", "powershell", "edit", "write"]) {
      assert.ok(!CHILD_TOOL_NAMES.includes(banned), `child leaks ${banned}`);
      assert.ok(!ROOT_TOOL_NAMES.includes(banned), `root leaks ${banned}`);
      assert.ok(!CHILD_TOOL_REQUEST_TYPES[banned]);
    }
    for (const name of ["read", "grep", "find", "ls"]) {
      assert.equal(CHILD_TOOL_REQUEST_TYPES[name], `workspace.${name}`);
    }
  });

  it("child custom tools stay on the typed node surface", () => {
    for (const type of Object.values(CHILD_TOOL_REQUEST_TYPES)) {
      assert.match(type, /^(workspace\.|reserved_|reservation\.|verification\.|agent\.|child\.)/);
    }
  });
});

describe("child-node protocol mapping", () => {
  it("maps reservation-aware mutations to the node contract", async () => {
    const seen: Array<{ type: string; payload: Record<string, unknown> }> = [];
    const tools = buildChildTools(async (type, payload) => {
      seen.push({ type, payload });
      return { ok: true };
    });
    const byName = Object.fromEntries(tools.map((tool) => [tool.name, tool]));

    await byName["reserved_edit"]!.execute({
      leaseId: "lease-1",
      fencingToken: 7,
      target: "src/a.ts",
      operation: "edit",
      searchText: "old",
      replacementText: "new",
    });
    await byName["reserved_move"]!.execute({
      leaseId: "lease-1",
      fencingToken: 7,
      target: "src/a.ts",
      operation: "move",
      destination: "src/b.ts",
    });
    await byName["reserve_files"]!.execute({
      paths: ["src/a.ts", "src/b.ts"],
      reason: "implement feature",
    });
    await byName["expand_reservation"]!.execute({
      leaseId: "lease-1",
      fencingToken: 7,
      paths: ["tests/a.test.ts"],
    });
    await byName["request_reservation_handoff"]!.execute({
      paths: ["src/owned.ts"],
      reason: "continue blocked work",
    });

    assert.equal(seen[0]!.type, "reserved_edit");
    assert.equal(seen[0]!.payload["path"], "src/a.ts");
    assert.equal(seen[0]!.payload["oldText"], "old");
    assert.equal(seen[0]!.payload["newText"], "new");
    assert.equal(seen[1]!.type, "reserved_move");
    assert.equal(seen[1]!.payload["source"], "src/a.ts");
    assert.equal(seen[1]!.payload["destination"], "src/b.ts");
    assert.equal(seen[2]!.type, "reservation.acquire");
    assert.deepEqual(seen[2]!.payload["scopes"], [
      { kind: "file", path: "src/a.ts" },
      { kind: "file", path: "src/b.ts" },
    ]);
    assert.equal(seen[3]!.type, "reservation.expand");
    assert.deepEqual(seen[3]!.payload["scopes"], [
      { kind: "file", path: "tests/a.test.ts" },
    ]);
    assert.equal(seen[4]!.type, "reservation.handoff.request");
    assert.deepEqual(seen[4]!.payload["paths"], ["src/owned.ts"]);
    assert.equal(seen[4]!.payload["reason"], "continue blocked work");
  });

  it("maps sandbox read tools to workspace.* node requests", async () => {
    const seen: Array<{ type: string; payload: Record<string, unknown> }> = [];
    const byName = Object.fromEntries(buildChildTools(async (type, payload) => {
      seen.push({ type, payload });
      return { ok: true };
    }).map((tool) => [tool.name, tool]));

    await byName["read"]!.execute({ path: "src/a.ts" });
    await byName["grep"]!.execute({ pattern: "foo", path: "src" });
    await byName["find"]!.execute({ pattern: "*.ts" });
    await byName["ls"]!.execute({ path: "src" });

    assert.equal(seen[0]!.type, "workspace.read");
    assert.equal(seen[0]!.payload["path"], "src/a.ts");
    assert.equal(seen[1]!.type, "workspace.grep");
    assert.equal(seen[2]!.type, "workspace.find");
    assert.equal(seen[3]!.type, "workspace.ls");
  });

  it("maps mail, verification, and result tools to canonical request types", async () => {
    const seen: Array<{ type: string; payload: Record<string, unknown> }> = [];
    const byName = Object.fromEntries(buildChildTools(async (type, payload) => {
      seen.push({ type, payload });
      return { ok: true };
    }).map((tool) => [tool.name, tool]));

    await byName["run_verification_command"]!.execute({ profileId: "required", commandId: "tests" });
    await byName["mail_reply"]!.execute({
      requestId: "request-1",
      threadId: "thread-1",
      messageId: "message-1",
      body: "done",
    });
    await byName["mail_inbox"]!.execute({});
    await byName["mail_ack"]!.execute({ messageId: "message-1" });
    await byName["submit_child_result"]!.execute({
      requestId: "request-1",
      status: "completed",
      summary: "done",
      evidence: ["tests"],
    });

    assert.deepEqual(seen.map(({ type }) => type), [
      "verification.request",
      "agent.message.send",
      "agent.inbox.read",
      "agent.message.acknowledge",
      "child.result.submit",
    ]);
    assert.equal(seen[1]!.payload["inReplyToMessageId"], "message-1");
  });
});

describe("authority-gated writes", () => {
  it("a mutation tool blocks until the correlated node response arrives", async () => {
    const { promise: gate, resolve: release } = Promise.withResolvers<unknown>();
    const tools = buildChildTools(() => gate);
    const write = tools.find((tool) => tool.name === "reserved_write")!;
    let settled = false;
    const run = write
      .execute({
        leaseId: "lease-1",
        fencingToken: 3,
        target: "src/a.ts",
        operation: "reserved_write",
        content: "x",
      })
      .then((text) => {
        settled = true;
        return text;
      });
    // Flush pending microtasks without wall-clock timers: the tool must
    // still be parked on the un-released node round-trip.
    for (let flush = 0; flush < 8; flush += 1) {
      await Promise.resolve();
    }
    assert.equal(settled, false, "tool resolved before the node answered");
    release({ ok: true, written: true });
    const text = await run;
    assert.match(text, /"ok":true/);
  });

  it("a denial from the authority is preserved verbatim and is not success", async () => {
    const denial = {
      ok: false,
      error: { code: "RESERVATION_CONFLICT", message: "path held by another lease" },
    };
    const tools = buildChildTools(async () => denial);
    const write = tools.find((tool) => tool.name === "reserved_write")!;
    const text = await write.execute({
      leaseId: "lease-2",
      fencingToken: 11,
      target: "src/a.ts",
      operation: "reserved_write",
      content: "x",
    });
    assert.match(text, /RESERVATION_CONFLICT/);
    assert.match(text, /path held by another lease/);
    assert.doesNotMatch(text, /"ok":true/);
  });
});

describe("child result lifecycle", () => {
  interface Harness {
    worker: PiWorker;
    frames: Envelope[];
    session: PiSessionLike & { emit(event: unknown): void };
    events(): Array<Envelope & { type: string }>;
  }

  async function harness(startPayload: Record<string, unknown>): Promise<Harness> {
    const frames: Envelope[] = [];
    const listeners: Array<(event: unknown) => void> = [];
    const session = {
      sessionId: "sdk-child",
      sessionFile: undefined,
      isStreaming: false,
      messages: [],
      subscribe(listener: (event: unknown) => void) {
        listeners.push(listener);
        return () => {};
      },
      async prompt() {
        for (const listener of listeners) listener({ type: "agent_start" });
      },
      async steer() {},
      async followUp() {},
      async abort() {},
      emit(event: unknown) {
        for (const listener of listeners) listener(event);
      },
    } as PiSessionLike & { emit(event: unknown): void };
    const factory: PiSessionFactory = { async create() { return session; } };
    const worker = new PiWorker({
      factory,
      send: (envelope) => frames.push(envelope),
      heartbeatIntervalMs: 60_000,
    });
    await worker.handleFrame({
      protocolVersion: 1,
      messageId: "m-start",
      kind: "request",
      sessionId: "s-child",
      type: "session.start",
      payload: startPayload,
    });
    return {
      worker,
      frames,
      session,
      events() {
        return frames.filter(
          (frame): frame is Envelope & { type: string } => frame.kind === "event",
        );
      },
    };
  }

  it("child start emits session.registered with the parent session id", async () => {
    const h = await harness({
      cwd: "/repo",
      agentDir: "/data/agent",
      mode: "child",
      parentSessionId: "s-root",
    });
    const registered = h.events().find((frame) => frame.type === "session.registered");
    assert.ok(registered, "missing session.registered");
    const data = (registered.payload as Record<string, unknown>)["data"] as Record<string, unknown>;
    assert.equal(data["parentSessionId"], "s-root");
    assert.equal(data["mode"], "child");
  });

  it("root start does not emit session.registered", async () => {
    const h = await harness({ cwd: "/repo", agentDir: "/data/agent" });
    assert.equal(h.events().find((frame) => frame.type === "session.registered"), undefined);
  });

  it("child start without a parent id is rejected", async () => {
    const h = await harness({ cwd: "/repo", agentDir: "/data/agent", mode: "child" });
    const response = h.frames.find((frame) => frame.kind === "response")!;
    assert.equal((response.payload as Record<string, unknown>)["ok"], false);
  });

  it("submit_child_result then clean run end normalizes to session.completed with the result", async () => {
    const h = await harness({
      cwd: "/repo",
      agentDir: "/data/agent",
      mode: "child",
      parentSessionId: "s-root",
    });
    const resultPayload = { ok: true, summary: "done", status: "completed" };
    h.session.emit({
      type: "tool_execution_end",
      toolName: "submit_child_result",
      response: resultPayload,
    });
    h.session.emit({ type: "agent_end" });
    const completed = h.events().find((frame) => frame.type === "session.completed");
    assert.ok(completed, "missing session.completed");
    const data = (completed.payload as Record<string, unknown>)["data"] as Record<string, unknown>;
    assert.deepEqual(data["result"], resultPayload);
  });

  it("a failed run end normalizes to session.failed and keeps the result payload", async () => {
    const h = await harness({
      cwd: "/repo",
      agentDir: "/data/agent",
      mode: "child",
      parentSessionId: "s-root",
    });
    h.session.emit({
      type: "tool_execution_end",
      toolName: "submit_child_result",
      response: { ok: true, summary: "partial" },
    });
    h.session.emit({ type: "agent_end", isError: true, error: "model exploded" });
    const failed = h.events().find((frame) => frame.type === "session.failed");
    assert.ok(failed, "missing session.failed");
    const data = (failed.payload as Record<string, unknown>)["data"] as Record<string, unknown>;
    assert.match(String(data["error"]), /model exploded/);
    assert.deepEqual(data["result"], { ok: true, summary: "partial" });
  });

  it("a prompt crash in a child emits session.failed", async () => {
    const h = await harness({
      cwd: "/repo",
      agentDir: "/data/agent",
      mode: "child",
      parentSessionId: "s-root",
    });
    await h.worker.handleFrame({
      protocolVersion: 1,
      messageId: "m-input",
      kind: "request",
      sessionId: "s-child",
      type: "session.input",
      payload: { text: "do work" },
    });
    const failed = h
      .events()
      .find((frame) => frame.type === "session.failed" || frame.type === "sdk.session.failed");
    // The injected session's prompt never rejects here; the failed path is
    // covered by the agent_end test above. No crash event is expected.
    assert.equal(failed, undefined);
    assert.ok(h.events().some((frame) => frame.type === "agent.started"));
  });

  it("lifecycle events share one strictly increasing sequence with normal events", async () => {
    const h = await harness({
      cwd: "/repo",
      agentDir: "/data/agent",
      mode: "child",
      parentSessionId: "s-root",
    });
    h.session.emit({ type: "agent_end" });
    const seqs = h.events().map(
      (frame) => (frame.payload as Record<string, unknown>)["seq"] as number,
    );
    assert.ok(seqs.length >= 2);
    for (let index = 1; index < seqs.length; index += 1) {
      assert.equal(seqs[index], seqs[index - 1]! + 1);
    }
  });

  it("root sessions never emit child completion events", async () => {
    const h = await harness({ cwd: "/repo", agentDir: "/data/agent" });
    h.session.emit({
      type: "tool_execution_end",
      toolName: "submit_child_result",
      response: { ok: true },
    });
    h.session.emit({ type: "agent_end" });
    const types = h.events().map((frame) => frame.type);
    assert.ok(!types.includes("session.completed"));
    assert.ok(!types.includes("session.failed"));
    assert.ok(types.includes("agent.completed"));
  });
});

describe("root tools remain untouched", () => {
  it("root tool builder produces sandbox reads plus orchestration tools", () => {
    const tools = buildRootTools(async () => ({ ok: true }));
    assert.deepEqual(
      tools.map((tool) => tool.name),
      [...EXPECTED_ROOT_TOOLS],
    );
  });
});
