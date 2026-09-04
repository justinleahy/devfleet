/**
 * Pi worker process entry point (SPEC.md section 24).
 *
 * Reads strict NDJSON envelopes from stdin, writes protocol responses to
 * stdout, and writes all diagnostics to stderr. Malformed frames are logged
 * to stderr and skipped; the worker keeps running so a single bad frame can
 * never take the protocol stream down.
 */
import { createSdkSessionFactory } from "./sdk.ts";
import { FrameDecoder, type Envelope, log, writeFrame } from "./protocol.ts";
import { PiWorker } from "./worker.ts";

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
  const worker: PiWorker = new PiWorker({
    // Custom orchestration tools round-trip through this worker's correlated
    // request broker; the closure runs only after `worker` is assigned.
    factory: createSdkSessionFactory((_sessionId, type, payload) =>
      worker.requestFromNode(type, payload),
    ),
    send: (envelope: Envelope) => writeFrame(envelope),
  });
  log(`pi-worker ready (protocolVersion 1, pid ${process.pid})`);

  for await (const chunk of process.stdin) {
    // Per-line errors are logged and skipped so valid frames sharing the
    // same stdin chunk are still processed. Oversize buffering throws.
    const frames = decoder.push(chunk as Buffer | string, (error) => {
      log(`FRAME_ERROR ${error.message}`);
    });
    for (const frame of frames) {
      await worker.handleFrame(frame);
      if (frame.kind === "goodbye") {
        worker.stop();
        log("goodbye received; shutting down");
        await drainStdout();
        return;
      }
    }
  }

  try {
    for (const frame of decoder.flush()) {
      await worker.handleFrame(frame);
    }
  } catch (cause) {
    log(`FRAME_ERROR ${(cause as Error).message}`);
  }
  worker.stop();
  await drainStdout();
  log("stdin closed; exiting");
}

main().catch((cause: unknown) => {
  log(`FATAL ${(cause as Error).stack ?? String(cause)}`);
  process.exitCode = 1;
});
