/**
 * Versioned NDJSON envelope codec for the Pi worker stdio protocol
 * (SPEC.md section 24).
 *
 * Contract:
 * - One JSON envelope per line on stdin/stdout (NDJSON).
 * - `protocolVersion` is always 1 for this milestone.
 * - `kind` is exactly one of: hello, event, request, response, heartbeat, goodbye.
 * - Logs never go to stdout; use {@link log} (stderr) instead.
 * - Frames larger than {@link MAX_FRAME_BYTES} are rejected on decode and
 *   never emitted on encode.
 */

export const PROTOCOL_VERSION = 1 as const;

/** Maximum size of a single NDJSON frame, in bytes (UTF-8, excluding newline). */
export const MAX_FRAME_BYTES = 1024 * 1024;

export const FRAME_KINDS = [
  "hello",
  "event",
  "request",
  "response",
  "heartbeat",
  "goodbye",
] as const;

export type FrameKind = (typeof FRAME_KINDS)[number];

export interface Envelope {
  protocolVersion: typeof PROTOCOL_VERSION;
  messageId: string;
  kind: FrameKind;
  sessionId: string;
  type: string;
  payload: unknown;
}

export type FrameErrorCode =
  | "FRAME_OVERSIZED"
  | "FRAME_EMPTY"
  | "FRAME_INVALID_JSON"
  | "FRAME_NOT_OBJECT"
  | "FRAME_UNSUPPORTED_PROTOCOL_VERSION"
  | "FRAME_UNKNOWN_KIND"
  | "FRAME_MISSING_FIELD";

export class FrameError extends Error {
  readonly code: FrameErrorCode;

  constructor(code: FrameErrorCode, message: string) {
    super(message);
    this.name = "FrameError";
    this.code = code;
  }
}

/**
 * Boundary parser: validate an unknown JSON value as a protocol envelope.
 * Every field is checked with typeof at this single parse site; consumers
 * receive the named {@link Envelope} type and never re-guard.
 *
 * @throws {FrameError} on any schema violation.
 */
export function parseEnvelope(value: unknown): Envelope {
  if (typeof value !== "object" || value === null || Array.isArray(value)) {
    throw new FrameError("FRAME_NOT_OBJECT", "frame must be a JSON object");
  }
  const record = value as Record<string, unknown>;

  if (record["protocolVersion"] !== PROTOCOL_VERSION) {
    throw new FrameError(
      "FRAME_UNSUPPORTED_PROTOCOL_VERSION",
      `unsupported protocolVersion ${String(record["protocolVersion"])}; expected ${PROTOCOL_VERSION}`,
    );
  }
  if (
    typeof record["kind"] !== "string" ||
    !(FRAME_KINDS as readonly string[]).includes(record["kind"])
  ) {
    throw new FrameError(
      "FRAME_UNKNOWN_KIND",
      `unknown frame kind ${String(record["kind"])}`,
    );
  }
  for (const field of ["messageId", "sessionId", "type"] as const) {
    const fieldValue = record[field];
    if (typeof fieldValue !== "string" || fieldValue.length === 0) {
      throw new FrameError(
        "FRAME_MISSING_FIELD",
        `frame field "${field}" must be a non-empty string`,
      );
    }
  }

  return {
    protocolVersion: PROTOCOL_VERSION,
    messageId: record["messageId"] as string,
    kind: record["kind"] as FrameKind,
    sessionId: record["sessionId"] as string,
    type: record["type"] as string,
    payload: record["payload"],
  };
}

/**
 * Serialize an envelope to a single NDJSON line (including trailing newline).
 *
 * @throws {FrameError} if the envelope fails validation or exceeds
 *   {@link MAX_FRAME_BYTES}.
 */
export function encodeFrame(envelope: Envelope): string {
  const body = JSON.stringify(parseEnvelope(envelope));
  const bytes = Buffer.byteLength(body, "utf8");
  if (bytes > MAX_FRAME_BYTES) {
    throw new FrameError(
      "FRAME_OVERSIZED",
      `frame of ${bytes} bytes exceeds maximum of ${MAX_FRAME_BYTES} bytes`,
    );
  }
  return body + "\n";
}

/**
 * Parse one NDJSON line into an envelope.
 *
 * Accepts the line with or without a trailing newline. Input may be a string
 * or a Buffer; Buffers are decoded as UTF-8.
 *
 * @throws {FrameError} on empty, oversized, non-JSON, non-object, or
 *   schema-violating input. Malformed frames are always rejected; they are
 *   never partially returned.
 */
export function decodeFrame(line: string | Buffer): Envelope {
  const text = typeof line === "string" ? line : line.toString("utf8");
  const trimmed = text.endsWith("\n")
    ? text.slice(0, -1)
    : text.endsWith("\r")
      ? text.slice(0, -1)
      : text;
  const bytes = Buffer.byteLength(trimmed, "utf8");
  if (bytes > MAX_FRAME_BYTES) {
    throw new FrameError(
      "FRAME_OVERSIZED",
      `frame of ${bytes} bytes exceeds maximum of ${MAX_FRAME_BYTES} bytes`,
    );
  }
  if (trimmed.trim().length === 0) {
    throw new FrameError("FRAME_EMPTY", "frame is empty");
  }

  let parsed: unknown;
  try {
    parsed = JSON.parse(trimmed);
  } catch (cause) {
    throw new FrameError(
      "FRAME_INVALID_JSON",
      `frame is not valid JSON: ${(cause as Error).message}`,
    );
  }
  return parseEnvelope(parsed);
}

/**
 * Incremental NDJSON decoder for a byte stream. Buffers partial lines across
 * chunks so frames split anywhere in the stream are reassembled correctly.
 */
export class FrameDecoder {
  #buffer = "";

  /**
   * Feed a chunk; returns every complete frame terminated within it.
   *
   * When `onError` is supplied, malformed lines are reported through it and
   * skipped so valid frames sharing the chunk are still delivered. Without
   * `onError`, the first malformed line throws.
   */
  push(
    chunk: string | Buffer,
    onError?: (error: FrameError) => void,
  ): Envelope[] {
    this.#buffer += typeof chunk === "string" ? chunk : chunk.toString("utf8");
    const frames: Envelope[] = [];
    for (;;) {
      if (this.#buffer.length > MAX_FRAME_BYTES) {
        throw new FrameError(
          "FRAME_OVERSIZED",
          `buffered frame exceeds maximum of ${MAX_FRAME_BYTES} bytes before a newline`,
        );
      }
      const newline = this.#buffer.indexOf("\n");
      if (newline < 0) {
        break;
      }
      const line = this.#buffer.slice(0, newline);
      this.#buffer = this.#buffer.slice(newline + 1);
      try {
        frames.push(decodeFrame(line));
      } catch (cause) {
        if (onError === undefined) {
          throw cause;
        }
        onError(cause as FrameError);
      }
    }
    return frames;
  }

  /**
   * Flush any remaining buffered input at end of stream.
   *
   * @throws {FrameError} if unterminated bytes remain.
   */
  flush(): Envelope[] {
    if (this.#buffer.length === 0) {
      return [];
    }
    const rest = this.#buffer;
    this.#buffer = "";
    return [decodeFrame(rest)];
  }
}

/**
 * Asynchronously decode every frame from a readable byte stream (e.g.
 * `process.stdin`). Malformed frames reject the iteration.
 */
export async function* decodeStream(
  stream: AsyncIterable<Buffer | string>,
): AsyncGenerator<Envelope> {
  const decoder = new FrameDecoder();
  for await (const chunk of stream) {
    yield* decoder.push(chunk);
  }
  yield* decoder.flush();
}

/**
 * Write one frame to protocol stdout. This is the ONLY write path allowed to
 * touch stdout, keeping protocol output free of log noise (SPEC.md section 24).
 */
export function writeFrame(
  envelope: Envelope,
  out: NodeJS.WritableStream = process.stdout,
): void {
  out.write(encodeFrame(envelope));
}

/** Write a log line to stderr. Logs MUST never go to stdout. */
export function log(
  message: string,
  err: NodeJS.WritableStream = process.stderr,
): void {
  err.write(message + "\n");
}
