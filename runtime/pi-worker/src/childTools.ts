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

/** Built-in Pi tools a child may use — read-only, no shell, no mutation. */
export const CHILD_BUILTIN_TOOLS = ["read", "grep", "find", "ls"] as const;

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
  reserved_read: "file.reserved_read",
  reserved_write: "file.reserved_write",
  reserved_edit: "file.reserved_edit",
  reserved_delete: "file.reserved_delete",
  reserved_move: "file.reserved_move",
  reserve_files: "reservation.acquire",
  expand_reservation: "reservation.expand",
  release_reservation: "reservation.release",
  run_verification_command: "verification.run",
  mail_send: "mail.send",
  mail_reply: "mail.reply",
  mail_inbox: "mail.inbox",
  mail_ack: "mail.ack",
  submit_child_result: "child.result.submit",
};

/** Exact child custom tool names, in the order declared above. */
export const CHILD_TOOL_NAMES = Object.keys(CHILD_TOOL_REQUEST_TYPES);

/**
 * Child tools that mutate the repository or reservation state. Every request
 * they emit must carry lease/fencing data before the node consults the
 * reservation authority.
 */
export const CHILD_MUTATION_TOOLS = [
  "reserved_write",
  "reserved_edit",
  "reserved_delete",
  "reserved_move",
  "reserve_files",
  "expand_reservation",
  "release_reservation",
  "run_verification_command",
] as const;

/** The node-side transport a tool round-trips through. */
export type ChildToolRequest = (
  type: string,
  payload: Record<string, unknown>,
) => Promise<unknown>;

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
  const strings = (description: string): ToolPropertySpec => ({
    type: "array",
    description,
  });

  /** Fencing properties shared by every mutation tool. */
  const fencing = {
    leaseId: s("Active reservation lease id granted by the node"),
    fencingToken: n("Monotonic fencing token for the lease"),
    target: s("Repository-relative POSIX target path"),
    operation: s("Operation name asserted to the reservation authority"),
  };

  return [
    roundTripTool(
      "reserved_read",
      "Reserved Read",
      "Read a file inside your reserved scope through the node.",
      { ...fencing, operation: { ...fencing.operation } },
      request,
    ),
    roundTripTool(
      "reserved_write",
      "Reserved Write",
      "Create or overwrite a file inside your reserved scope; the node writes only after the reservation authority accepts the lease and fencing token.",
      { ...fencing, content: s("Full file content to write") },
      request,
    ),
    roundTripTool(
      "reserved_edit",
      "Reserved Edit",
      "Apply a text replacement inside a reserved file; the node edits only after the reservation authority accepts the lease and fencing token.",
      {
        ...fencing,
        searchText: s("Exact existing text to replace"),
        replacementText: s("Replacement text"),
      },
      request,
    ),
    roundTripTool(
      "reserved_delete",
      "Reserved Delete",
      "Delete a reserved file; the node deletes only after the reservation authority accepts the lease and fencing token.",
      fencing,
      request,
    ),
    roundTripTool(
      "reserved_move",
      "Reserved Move",
      "Rename or move within reserved scopes; the node moves only after the reservation authority accepts the lease and fencing token.",
      { ...fencing, destination: s("Repository-relative destination path") },
      request,
    ),
    roundTripTool(
      "reserve_files",
      "Reserve Files",
      "Acquire a reservation lease over repository-relative paths.",
      { ...fencing, paths: strings("Repository-relative paths to reserve") },
      request,
    ),
    roundTripTool(
      "expand_reservation",
      "Expand Reservation",
      "Extend an active lease with additional paths.",
      { ...fencing, paths: strings("Additional repository-relative paths") },
      request,
    ),
    roundTripTool(
      "release_reservation",
      "Release Reservation",
      "Release an active reservation lease when work on the scope is done.",
      fencing,
      request,
    ),
    roundTripTool(
      "run_verification_command",
      "Run Verification Command",
      "Ask the node to run one configured verification command; the node executes it only after the reservation authority accepts the lease and fencing token.",
      { ...fencing, command: s("Configured verification command to run") },
      request,
    ),
    roundTripTool(
      "mail_send",
      "Send Mail",
      "Send a message to the root or other sessions on this request.",
      {
        requestId: s("Request id"),
        threadId: s("Conversation thread id"),
        recipients: strings("Recipient session ids"),
        subject: s("Message subject"),
        body: s("Markdown message body"),
      },
      request,
    ),
    roundTripTool(
      "mail_reply",
      "Reply Mail",
      "Reply on an existing message thread.",
      {
        requestId: s("Request id"),
        threadId: s("Conversation thread id"),
        messageId: s("Message being replied to"),
        body: s("Markdown reply body"),
      },
      request,
    ),
    roundTripTool(
      "mail_inbox",
      "Read Mail Inbox",
      "Read pending messages addressed to this session.",
      {},
      request,
    ),
    roundTripTool(
      "mail_ack",
      "Acknowledge Mail",
      "Acknowledge one inbox message so it stops being pending.",
      { messageId: s("Inbox message id") },
      request,
    ),
    roundTripTool(
      "submit_child_result",
      "Submit Child Result",
      "Submit the durable result payload for this child assignment.",
      {
        requestId: s("Request id"),
        status: s("Terminal status (completed or blocked)"),
        summary: s("Result summary"),
        evidence: strings("Evidence references"),
      },
      request,
    ),
  ];
}

