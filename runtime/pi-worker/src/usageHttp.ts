/**
 * Bounded HTTP for the usage sidecar. Every provider request goes through
 * `createUsageRequest`, which enforces the security envelope the collectors
 * cannot bypass: exact HTTPS origin allowlist, no redirects, capped response
 * size, and the caller's shared deadline. Failures surface as `Error`s whose
 * `message` is one of the stable `UsageRequestFailure` tokens so collectors
 * can forward them as diagnostics without leaking upstream text.
 */
import type { UsageRequest } from "./usageTypes.ts";

/** Maximum response body accepted from any provider. */
export const MAX_RESPONSE_BYTES = 64 * 1024;

/** Exact HTTPS origins the collectors may contact; anything else is refused. */
export type OriginAllowlist = Readonly<Record<string, true>>;

export const ALLOWED_ORIGINS: OriginAllowlist = {
  "https://chatgpt.com": true,
  "https://api.anthropic.com": true,
  "https://api.kimi.com": true,
  "https://cli-chat-proxy.grok.com": true,
  "https://api.z.ai": true,
  "https://opencode.ai": true,
};

/** Stable failure tokens; safe to emit as report diagnostics. */
export type UsageRequestFailure =
  | "origin_not_allowed"
  | "redirect_refused"
  | "response_too_large"
  | "request_timeout"
  | "request_failed";

const FAILURE_TOKENS: Readonly<Record<UsageRequestFailure, true>> = {
  origin_not_allowed: true,
  redirect_refused: true,
  response_too_large: true,
  request_timeout: true,
  request_failed: true,
};

/** The `UsageRequestFailure` an error carries, or undefined for any other error. */
export function usageRequestFailure(error: unknown): UsageRequestFailure | undefined {
  return error instanceof Error && Object.hasOwn(FAILURE_TOKENS, error.message)
    ? (error.message as UsageRequestFailure)
    : undefined;
}

function refuse(token: UsageRequestFailure): never {
  throw new Error(token);
}

function isAllowedOrigin(url: string, allowedOrigins: OriginAllowlist): boolean {
  let parsed: URL;
  try {
    parsed = new URL(url);
  } catch {
    return false;
  }
  return parsed.protocol === "https:" && Object.hasOwn(allowedOrigins, parsed.origin);
}

/** Read at most `MAX_RESPONSE_BYTES`; refuse (and cancel the stream) past that. */
async function readBounded(response: Response, signal: AbortSignal): Promise<string> {
  const declared = Number(response.headers.get("content-length"));
  if (declared > MAX_RESPONSE_BYTES) refuse("response_too_large");
  if (!response.body) return "";

  const reader = response.body.getReader();
  const chunks: Uint8Array[] = [];
  let total = 0;
  try {
    for (;;) {
      if (signal.aborted) refuse("request_timeout");
      const { done, value } = await reader.read();
      if (done) break;
      total += value.byteLength;
      if (total > MAX_RESPONSE_BYTES) refuse("response_too_large");
      chunks.push(value);
    }
  } finally {
    await reader.cancel().catch(() => undefined);
  }
  return new TextDecoder().decode(Buffer.concat(chunks, total));
}

/**
 * Build the `UsageRequest` handed to collectors. `fetchFn` and
 * `allowedOrigins` are injectable for tests; production uses the globals.
 */
export function createUsageRequest(
  fetchFn: typeof globalThis.fetch = globalThis.fetch,
  allowedOrigins: OriginAllowlist = ALLOWED_ORIGINS,
): UsageRequest {
  return async (url, init, signal) => {
    if (!isAllowedOrigin(url, allowedOrigins)) refuse("origin_not_allowed");
    if (signal.aborted) refuse("request_timeout");

    let response: Response;
    try {
      response = await fetchFn(url, { ...init, redirect: "manual", signal });
    } catch {
      refuse(signal.aborted ? "request_timeout" : "request_failed");
    }
    if (response.status >= 300 && response.status < 400) {
      await response.body?.cancel().catch(() => undefined);
      refuse("redirect_refused");
    }

    let text: string;
    try {
      text = await readBounded(response, signal);
    } catch (error) {
      if (usageRequestFailure(error)) throw error;
      refuse(signal.aborted ? "request_timeout" : "request_failed");
    }

    // JSON when it parses; otherwise the raw text so collectors can still
    // classify by status. Neither ever reaches the report.
    let body: unknown = undefined;
    if (text !== "") {
      try {
        body = JSON.parse(text);
      } catch {
        body = text;
      }
    }
    return { status: response.status, body };
  };
}
