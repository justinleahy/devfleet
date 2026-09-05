/**
 * Pi child session tool surface (SPEC.md sections 18.1, 25.3).
 *
 * A child session gets the read-only built-ins plus an exact custom tool
 * allowlist: reservation-enforced mutations, mail, and child result
 * submission. The default unrestricted `edit`, `write`, `bash`, and
 * `powershell` tools are never granted. Every mutation tool round-trips to
 * the node — which consults the reservation authority before touching the
 * filesystem — and carries `leaseId`, `fencingToken`, `target`, and
 * `operation` fencing data. A tool call blocks until the correlated node
 * response arrives, so a mutation can never report success before the
 * authority accepted it.
 */

import {
  roundTripTool,
  type RootTool,
  type ToolPropertySpec,
} from "./rootTools.ts";

/** Built-in Pi tools a child may use — none; reads round-trip through the node. */
export const CHILD_BUILTIN_TOOLS = [] as const;

/**
 * Built-in Pi tools that are technically excluded from every child session.
 * Children never receive the default unrestricted edit/write/shell surface.
 */
export const CHILD_EXCLUDED_TOOLS = [
  "bash",
  "powershell",
  "edit",
  "write",
] as const;

/** Child tool name → node request type (SPEC.md sections 18.1, 25.3). */
export const CHILD_TOOL_REQUEST_TYPES: Record<string, string> = {
  read: "workspace.read",
  grep: "workspace.grep",
  find: "workspace.find",
  ls: "workspace.ls",
  reserved_read: "reserved_read",
  reserved_write: "reserved_write",
  reserved_edit: "reserved_edit",
  reserved_delete: "reserved_delete",
  reserved_move: "reserved_move",
  reserve_files: "reservation.acquire",
  expand_reservation: "reservation.expand",
  release_reservation: "reservation.release",
  request_reservation_handoff: "reservation.handoff.request",
  accept_reservation_handoff: "reservation.handoff.accept",
  run_verification_command: "verification.request",
  mail_send: "agent.message.send",
  mail_reply: "agent.message.send",
  mail_inbox: "agent.inbox.read",
  mail_ack: "agent.message.acknowledge",
  submit_child_result: "child.result.submit",
};

/** Exact child custom tool names, in the order declared above. */
export const CHILD_TOOL_NAMES = Object.keys(CHILD_TOOL_REQUEST_TYPES);

/**
 * Repository mutations and lease changes always round-trip through the node.
 * Existing-lease operations carry the lease ID and fencing token; acquisition
 * obtains those values from the authority.
 */
export const CHILD_MUTATION_TOOLS = [
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
] as const;

/** The node-side transport a tool round-trips through. */
export type ChildToolRequest = (
  type: string,
  payload: Record<string, unknown>,
) => Promise<unknown>;

function mapChildPayload(
  name: string,
  params: Record<string, unknown>,
): Record<string, unknown> {
  const payload = { ...params };
  if (typeof payload["target"] === "string") {
    payload[name === "reserved_move" ? "source" : "path"] = payload["target"];
  }
  if (name === "reserved_edit") {
    payload["oldText"] = payload["searchText"];
    payload["newText"] = payload["replacementText"];
  }
  if ((name === "reserve_files" || name === "expand_reservation")
      && Array.isArray(payload["paths"])) {
    payload["scopes"] = payload["paths"].map((path) => ({ kind: "file", path }));
  }
  if (name === "mail_reply") {
    payload["inReplyToMessageId"] = payload["messageId"];
  }
  return payload;
}

function childRoundTripTool(
  name: string,
  label: string,
  description: string,
  properties: Record<string, ToolPropertySpec>,
  request: ChildToolRequest,
): RootTool {
  return roundTripTool(
    name,
    label,
    description,
    properties,
    request,
    CHILD_TOOL_REQUEST_TYPES[name]!,
    (params) => mapChildPayload(name, params),
  );
}

/** Child system prompt: the technical restriction lives in the tool surface. */
export const CHILD_SYSTEM_PROMPT = [
  "You are a child worker agent for one delegated assignment.",
  "You work inside the file scopes reserved for you through your reservation",
  "tools. All mutations flow through reservation-enforced tools; you cannot",
  "edit, write, or run shell commands outside them. Coordinate with the root",
  "orchestrator through mail tools and finish by calling submit_child_result",
  "with a durable result payload.",
].join(" ");

/**
 * Build the child custom tools over the given transport.
 * Factory-injected transport keeps the tools unit-testable without the SDK.
 */
export function buildChildTools(request: ChildToolRequest): RootTool[] {
  const s = (description: string): ToolPropertySpec => ({
    type: "string",
    description,
  });
  const n = (description: string): ToolPropertySpec => ({
    type: "number",
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

  /** Fencing properties shared by operations against an active lease. */
  const lease = {
    leaseId: s("Active reservation lease id granted by the node"),
    fencingToken: n("Monotonic fencing token for the lease"),
  };
  const fencedPath = {
    ...lease,
    target: s("Repository-relative POSIX target path"),
    operation: s("Operation name asserted to the reservation authority"),
  };

  return [
    childRoundTripTool("read",
    "Read",
    "Read a repository-relative file through the node. Paths cannot leave the registered repository or follow a symlink out.",
    { path: s("Repository-relative POSIX path") },
    request,),
    childRoundTripTool("grep",
    "Grep",
    "Search file contents inside the registered repository through the node.",
    { pattern: s("Regular expression"), path: optional("Repository-relative start path") },
    request,),
    childRoundTripTool("find",
    "Find",
    "Find files inside the registered repository through the node.",
    { pattern: optional("Glob against file name or relative path"), path: optional("Repository-relative start path") },
    request,),
    childRoundTripTool("ls",
    "List",
    "List a directory inside the registered repository through the node.",
    { path: optional("Repository-relative directory") },
    request,),
    childRoundTripTool("reserved_read",
    "Reserved Read",
    "Read a file inside your reserved scope through the node.",
    { ...fencedPath },
    request,),
    childRoundTripTool("reserved_write",
    "Reserved Write",
    "Create or overwrite a file inside your reserved scope; the node writes only after the reservation authority accepts the lease and fencing token.",
    { ...fencedPath, content: s("Full file content to write") },
    request,),
    childRoundTripTool("reserved_edit",
    "Reserved Edit",
    "Apply a text replacement inside a reserved file; the node edits only after the reservation authority accepts the lease and fencing token.",
    {
      ...fencedPath,
      searchText: s("Exact existing text to replace"),
      replacementText: s("Replacement text"),
    },
    request,),
    childRoundTripTool("reserved_delete",
    "Reserved Delete",
    "Delete a reserved file; the node deletes only after the reservation authority accepts the lease and fencing token.",
    fencedPath,
    request,),
    childRoundTripTool("reserved_move",
    "Reserved Move",
    "Rename or move within reserved scopes; the node moves only after the reservation authority accepts the lease and fencing token.",
    { ...fencedPath, destination: s("Repository-relative destination path") },
    request,),
    childRoundTripTool("reserve_files",
    "Reserve Files",
    "Acquire a reservation lease over repository-relative paths.",
    { paths: strings("Repository-relative paths to reserve"), reason: s("Reason for the reservation") },
    request,),
    childRoundTripTool("expand_reservation",
    "Expand Reservation",
    "Extend an active lease with additional paths.",
    { ...lease, paths: strings("Additional repository-relative paths") },
    request,),
    childRoundTripTool("request_reservation_handoff",
    "Request Reservation Handoff",
    "Request ownership of paths currently reserved by another session; the current owner must accept.",
    { paths: strings("Repository-relative paths needed by this session"), reason: s("Why ownership is needed") },
    request,),
    childRoundTripTool("accept_reservation_handoff",
    "Accept Reservation Handoff",
    "As the current owner, atomically transfer your reservation to the requesting target.",
    { leaseId: s("Reservation lease id named in the handoff message") },
    request,),
    childRoundTripTool("release_reservation",
    "Release Reservation",
    "Release an active reservation lease when work on the scope is done.",
    lease,
    request,),
    childRoundTripTool("run_verification_command",
    "Run Verification Command",
    "Ask the node to run one command from a trusted verification profile.",
    { profileId: s("Configured verification profile id"), commandId: optional("Command id within the profile") },
    request,),
    childRoundTripTool("mail_send",
    "Send Mail",
    "Send a message to the root or other sessions on this request.",
    {
      requestId: s("Request id"),
      threadId: s("Conversation thread id"),
      recipients: strings("Recipient session ids"),
      subject: s("Message subject"),
      body: s("Markdown message body"),
    },
    request,),
    childRoundTripTool("mail_reply",
    "Reply Mail",
    "Reply on an existing message thread.",
    {
      requestId: s("Request id"),
      threadId: s("Conversation thread id"),
      messageId: s("Message being replied to"),
      body: s("Markdown reply body"),
    },
    request,),
    childRoundTripTool("mail_inbox",
    "Read Mail Inbox",
    "Read pending messages addressed to this session.",
    {},
    request,),
    childRoundTripTool("mail_ack",
    "Acknowledge Mail",
    "Acknowledge one inbox message so it stops being pending.",
    { messageId: s("Inbox message id") },
    request,),
    childRoundTripTool("submit_child_result",
    "Submit Child Result",
    "Submit the durable result payload for this child assignment.",
    {
      requestId: s("Request id"),
      status: s("Terminal status (completed or blocked)"),
      summary: s("Result summary"),
      evidence: strings("Evidence references"),
    },
    request,),
  ];
}

