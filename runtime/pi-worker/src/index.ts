/**
 * Pi worker process entry point (SPEC.md section 24).
 *
 * Reads strict NDJSON envelopes from stdin, writes protocol responses to
 * stdout, and writes all diagnostics to stderr. Malformed frames are logged
 * to stderr and skipped; the worker keeps running so a single bad frame can
 * never take the protocol stream down.
 */
import { FrameDecoder, type Envelope, log, writeFrame } from "./protocol.ts";

function responseFor(received: Envelope): Envelope {
  return {
    protocolVersion: 1,
    messageId: received.messageId,
    kind: "response",
    sessionId: received.sessionId,
    type: "worker.ack",
    payload: { acknowledgedKind: received.kind, receivedType: received.type },
  };
}
/** process.stdout is asynchronous when redirected; wait for it to drain. */
function drainStdout(): Promise<void> {
  const { promise, resolve } = Promise.withResolvers<void>();
  if (process.stdout.pending !== true) {
    resolve();
  } else {
    process.stdout.once("drain", resolve);
  }
  return promise;
}

async function main(): Promise<void> {
  const decoder = new FrameDecoder();
  log(`pi-worker ready (protocolVersion 1, pid ${process.pid})`);

  for await (const chunk of process.stdin) {
    // Per-line errors are logged and skipped so valid frames sharing the
    // same stdin chunk are still processed. Oversize buffering throws.
    const frames = decoder.push(chunk as Buffer | string, (error) => {
      log(`FRAME_ERROR ${error.message}`);
    });
    for (const frame of frames) {
      writeFrame(responseFor(frame));
      if (frame.kind === "goodbye") {
        log("goodbye received; shutting down");
        await drainStdout();
        return;
      }
    }
  }

  try {
    for (const frame of decoder.flush()) {
      writeFrame(responseFor(frame));
    }
  } catch (cause) {
    log(`FRAME_ERROR ${(cause as Error).message}`);
  }
  await drainStdout();
  log("stdin closed; exiting");
}

main().catch((cause: unknown) => {
  log(`FATAL ${(cause as Error).stack ?? String(cause)}`);
  process.exitCode = 1;
});
