/**
 * Identifier helpers for the Pi worker protocol (SPEC.md section 24).
 *
 * Event identifiers must be globally unique; message identifiers correlate
 * request/response pairs across the NDJSON channel. Both use UUIDv4, which is
 * collision-safe for the volumes a single worker process produces.
 */
import { randomUUID } from "node:crypto";

/** Fresh correlation id for a request/response pair or event frame. */
export function newMessageId(): string {
  return randomUUID();
}
