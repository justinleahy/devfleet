/**
 * Root orchestrator custom tools (SPEC.md sections 13.2, 13.3, 24.2).
 *
 * The root receives exactly the read-only built-ins `read`, `grep`, `find`,
 * `ls` plus these fifteen orchestration tools. Every tool is a pure
 * round-trip: its execute sends one correlated request to the node and
 * returns the node's response. No tool mutates the filesystem, spawns
 * processes, or touches Git directly.
 */
import { boundedError } from "./broker.ts";

/** Built-in Pi tools the root may use — none; reads round-trip through the node. */
export const ROOT_BUILTIN_TOOLS = [] as const;

/** Built-in Pi tools that are technically excluded from the root session. */
export const ROOT_EXCLUDED_TOOLS = [
  "bash",
  "powershell",
  "edit",
  "write",
] as const;

/** JSON-schema-ish property spec; `sdk.ts` converts these to TypeBox. */
export interface ToolPropertySpec {
  type: "string" | "number" | "boolean" | "array";
  description: string;
  optional?: boolean;
}

/** SDK-independent custom tool definition. */
export interface RootTool {
  name: string;
  label: string;
  description: string;
  properties: Record<string, ToolPropertySpec>;
  execute(params: Record<string, unknown>): Promise<string>;
}

/** Tool name → node request type (SPEC.md section 24.2). */
export const TOOL_REQUEST_TYPES: Record<string, string> = {
  read: "workspace.read",
  grep: "workspace.grep",
  find: "workspace.find",
  ls: "workspace.ls",
  create_plan: "plan.submit",
  revise_plan: "plan.revise",
  spawn_agent: "agent.spawn",
  spawn_agents: "agent.spawn",
  get_agent_status: "agent.status",
  await_agent: "agent.await",
  send_agent_message: "agent.message.send",
  read_agent_inbox: "agent.inbox.read",
  acknowledge_message: "agent.message.acknowledge",
  request_reservation_handoff: "reservation.handoff.request",
  cancel_agent: "agent.cancel",
  inspect_project_diff: "project.diff.inspect",
  request_verification: "verification.request",
  submit_completion: "request.complete",
  block_request: "request.block",
};

/** Exact root orchestration tool names, in SPEC order (section 13.3). */
export const ROOT_TOOL_NAMES = Object.keys(TOOL_REQUEST_TYPES);

/** Root system prompt: technical restriction is in the tool surface, this frames the role. */
export const ROOT_SYSTEM_PROMPT = [
  "You are the root orchestrator for a development request.",
  "You coordinate child agents, plans, reservations, review, and verification",
  "through your orchestration tools. Your tool surface is read-only plus",
  "orchestration round-trips: you cannot edit, write, or run shell commands.",
  "Never claim completion yourself; call submit_completion with evidence.",
].join(" ");

/** The node-side transport a tool round-trips through. */
export type ToolRequest = (
  type: string,
  payload: Record<string, unknown>,
) => Promise<unknown>;

export function roundTripTool(
  name: string,
  label: string,
  description: string,
  properties: Record<string, ToolPropertySpec>,
  request: ToolRequest,
  requestType = TOOL_REQUEST_TYPES[name]!,
  mapPayload: (params: Record<string, unknown>) => Record<string, unknown> = (params) => params,
): RootTool {
  return {
    name,
    label,
    description,
    properties,
    async execute(params) {
      const result = await request(requestType, mapPayload(params));
      // Tool results flow back through the SDK as text content; keep them
      // bounded so a huge node payload cannot blow up the model context.
      return boundedError(JSON.stringify(result ?? { ok: true }));
    },
  };
}

/**
 * Build the fifteen root orchestration tools over the given transport.
 * Factory-injected transport keeps the tools unit-testable without the SDK.
 */
export function buildRootTools(request: ToolRequest): RootTool[] {
  const s = (description: string): ToolPropertySpec => ({
    type: "string",
    description,
  });
  const optional = (description: string): ToolPropertySpec => ({
    type: "string",
    description,
    optional: true,
  });
  const strings = (description: string): ToolPropertySpec => ({
    type: "array",
    description,
  });

  return [
    roundTripTool(
      "read",
      "Read",
      "Read a repository-relative file through the node. Paths cannot leave the registered repository or follow a symlink out.",
      { path: s("Repository-relative POSIX path") },
      request,
    ),
    roundTripTool(
      "grep",
      "Grep",
      "Search file contents inside the registered repository through the node.",
      { pattern: s("Regular expression"), path: optional("Repository-relative start path") },
      request,
    ),
    roundTripTool(
      "find",
      "Find",
      "Find files inside the registered repository through the node.",
      { pattern: optional("Glob against file name or relative path"), path: optional("Repository-relative start path") },
      request,
    ),
    roundTripTool(
      "ls",
      "List",
      "List a directory inside the registered repository through the node.",
      { path: optional("Repository-relative directory") },
      request,
    ),
    roundTripTool(
      "create_plan",
      "Create Plan",
      "Submit the plan for the active development request.",
      { requestId: s("Request id"), title: s("Plan title"), steps: strings("Ordered step descriptions") },
      request,
    ),
    roundTripTool(
      "revise_plan",
      "Revise Plan",
      "Revise the active plan with a replacement step list and reason.",
      { requestId: s("Request id"), reason: s("Why the plan changes"), steps: strings("Replacement steps") },
      request,
    ),
    roundTripTool(
      "spawn_agent",
      "Spawn Agent",
      "Start one child agent session for a role in this request.",
      {
        agentName: s("Child agent name"),
        role: s("Child role routed by node policy"),
        prompt: s("Initial child prompt"),
      },
      request,
    ),
    roundTripTool(
      "spawn_agents",
      "Spawn Agents",
      "Start several child agent sessions in one round-trip.",
      { requests: strings("JSON-encoded spawn requests") },
      request,
    ),
    roundTripTool(
      "get_agent_status",
      "Get Agent Status",
      "Read the current lifecycle dimensions of one child session.",
      { agentSessionId: s("Child session id") },
      request,
    ),
    roundTripTool(
      "await_agent",
      "Await Agent",
      "Wait until a child session reaches a terminal or quiescent state.",
      { agentSessionId: s("Child session id"), timeoutMs: optional("Wait budget in milliseconds") },
      request,
    ),
    roundTripTool(
      "send_agent_message",
      "Send Agent Message",
      "Deliver a steering or instruction message to a child session.",
      { agentSessionId: s("Child session id"), text: s("Message text") },
      request,
    ),
    roundTripTool(
      "read_agent_inbox",
      "Read Agent Inbox",
      "Read pending messages addressed to the root from children.",
      { agentSessionId: optional("Filter by child session id") },
      request,
    ),
    roundTripTool(
      "acknowledge_message",
      "Acknowledge Message",
      "Acknowledge one inbox message so it stops being pending.",
      { messageId: s("Inbox message id") },
      request,
    ),
    roundTripTool(
      "request_reservation_handoff",
      "Request Reservation Handoff",
      "Ask the node to hand file reservations between sessions.",
      { agentSessionId: s("Target session id"), paths: strings("Repository-relative paths"), reason: s("Why the handoff is needed") },
      request,
    ),
    roundTripTool(
      "cancel_agent",
      "Cancel Agent",
      "Cancel one child session.",
      { agentSessionId: s("Child session id"), reason: optional("Cancellation reason") },
      request,
    ),
    roundTripTool(
      "inspect_project_diff",
      "Inspect Project Diff",
      "Read the repository diff the node maintains for this request.",
      { requestId: s("Request id") },
      request,
    ),
    roundTripTool(
      "request_verification",
      "Request Verification",
      "Run a configured verification profile for the bound request; optional commandId targets one command.",
      { profileId: s("Configured verification profile id (required)"), commandId: optional("Specific verification command id") },
      request,
    ),
    roundTripTool(
      "submit_completion",
      "Submit Completion",
      "Submit completion evidence for the node to accept or reject.",
      { requestId: s("Request id"), summary: s("Completion summary"), evidence: strings("Evidence references") },
      request,
    ),
    roundTripTool(
      "block_request",
      "Block Request",
      "Report that the request is blocked and on what.",
      { requestId: s("Request id"), reason: s("Blocking reason"), blockedOn: optional("Blocking dependency") },
      request,
    ),
  ];
}
