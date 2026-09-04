/**
 * Node built-in test-runner suite for the NDJSON envelope codec
 * (SPEC.md section 24.3: maximum frame size documented and tested).
 */
import assert from "node:assert/strict";
import { describe, it } from "node:test";
import {
  MAX_FRAME_BYTES,
  PROTOCOL_VERSION,
  type Envelope,
  FrameDecoder,
  FrameError,
  decodeFrame,
  encodeFrame,
  parseEnvelope,
} from "../src/protocol.ts";

function sampleEnvelope(): Envelope {
  return {
    protocolVersion: PROTOCOL_VERSION,
    messageId: "01K-TEST",
    kind: "request",
    sessionId: "session-01K",
    type: "agent.spawn",
    payload: { role: "child" },
  };
}

describe("encodeFrame / decodeFrame roundtrip", () => {
  it("roundtrips a valid envelope through encode and decode", () => {
    const envelope = sampleEnvelope();
    const decoded = decodeFrame(encodeFrame(envelope));
    assert.deepEqual(decoded, envelope);
  });

  it("emits exactly one trailing newline on encode", () => {
    const frame = encodeFrame(sampleEnvelope());
    assert.equal(frame.endsWith("\n"), true);
    assert.equal(frame.slice(0, -1).includes("\n"), false);
  });

  it("rejects encoding a frame above MAX_FRAME_BYTES", () => {
    const huge: Envelope = { ...sampleEnvelope(), payload: "x".repeat(MAX_FRAME_BYTES) };
    assert.throws(() => encodeFrame(huge), (error: unknown) => {
      assert.ok(error instanceof FrameError);
      assert.equal(error.code, "FRAME_OVERSIZED");
      return true;
    });
  });
});

describe("line endings", () => {
  it("decodes LF-terminated frames", () => {
    const line = JSON.stringify(sampleEnvelope()) + "\n";
    assert.deepEqual(decodeFrame(line), sampleEnvelope());
  });

  it("decodes CRLF-terminated frames", () => {
    const line = JSON.stringify(sampleEnvelope()) + "\r\n";
    assert.deepEqual(decodeFrame(line), sampleEnvelope());
  });

  it("decodes frames split across stream chunks with CRLF endings", () => {
    const decoder = new FrameDecoder();
    const first = JSON.stringify(sampleEnvelope()) + "\r\n";
    const second = JSON.stringify({ ...sampleEnvelope(), messageId: "02K" }) + "\r\n";
    const bytes = Buffer.from(first + second, "utf8");
    const split = Math.floor(bytes.length / 2);
    const frames = [...decoder.push(bytes.subarray(0, split)), ...decoder.push(bytes.subarray(split))];
    assert.equal(frames.length, 2);
    assert.equal(frames[0]?.messageId, "01K-TEST");
    assert.equal(frames[1]?.messageId, "02K");
  });
});

describe("malformed input", () => {
  it("rejects invalid JSON with FRAME_INVALID_JSON", () => {
    assert.throws(() => decodeFrame("{not json"), (error: unknown) => {
      assert.ok(error instanceof FrameError);
      assert.equal(error.code, "FRAME_INVALID_JSON");
      return true;
    });
  });

  it("rejects non-object JSON with FRAME_NOT_OBJECT", () => {
    assert.throws(
      () => decodeFrame("[1,2,3]"),
      (error: unknown) => error instanceof FrameError && error.code === "FRAME_NOT_OBJECT",
    );
  });

  it("rejects empty frames with FRAME_EMPTY", () => {
    assert.throws(
      () => decodeFrame("   \n"),
      (error: unknown) => error instanceof FrameError && error.code === "FRAME_EMPTY",
    );
  });

  it("rejects missing required fields with FRAME_MISSING_FIELD", () => {
    const missing = { ...sampleEnvelope() } as Record<string, unknown>;
    delete missing["messageId"];
    assert.throws(
      () => parseEnvelope(missing),
      (error: unknown) => error instanceof FrameError && error.code === "FRAME_MISSING_FIELD",
    );
  });

  it("rejects empty-string fields with FRAME_MISSING_FIELD", () => {
    assert.throws(
      () => parseEnvelope({ ...sampleEnvelope(), sessionId: "" }),
      (error: unknown) => error instanceof FrameError && error.code === "FRAME_MISSING_FIELD",
    );
  });

  it("rejects unknown kind with FRAME_UNKNOWN_KIND", () => {
    assert.throws(
      () => parseEnvelope({ ...sampleEnvelope(), kind: "teleport" }),
      (error: unknown) => error instanceof FrameError && error.code === "FRAME_UNKNOWN_KIND",
    );
  });
});

describe("protocol version", () => {
  it("rejects unsupported versions with FRAME_UNSUPPORTED_PROTOCOL_VERSION", () => {
    assert.throws(
      () => parseEnvelope({ ...sampleEnvelope(), protocolVersion: 2 }),
      (error: unknown) =>
        error instanceof FrameError && error.code === "FRAME_UNSUPPORTED_PROTOCOL_VERSION",
    );
  });

  it("rejects non-numeric versions with FRAME_UNSUPPORTED_PROTOCOL_VERSION", () => {
    assert.throws(
      () => parseEnvelope({ ...sampleEnvelope(), protocolVersion: "1" }),
      (error: unknown) =>
        error instanceof FrameError && error.code === "FRAME_UNSUPPORTED_PROTOCOL_VERSION",
    );
  });
});

describe("frame size limits", () => {
  it("rejects oversize lines in decodeFrame with FRAME_OVERSIZED", () => {
    const oversized = JSON.stringify({
      ...sampleEnvelope(),
      payload: "x".repeat(MAX_FRAME_BYTES + 1),
    });
    assert.throws(() => decodeFrame(oversized), (error: unknown) => {
      assert.ok(error instanceof FrameError);
      assert.equal(error.code, "FRAME_OVERSIZED");
      return true;
    });
  });

  it("rejects unbounded buffering above MAX_FRAME_BYTES with no newline", () => {
    const decoder = new FrameDecoder();
    assert.throws(
      () => decoder.push("x".repeat(MAX_FRAME_BYTES + 1)),
      (error: unknown) => error instanceof FrameError && error.code === "FRAME_OVERSIZED",
    );
  });

  it("accepts a frame at exactly MAX_FRAME_BYTES minus newline", () => {
    const payload = "x".repeat(
      MAX_FRAME_BYTES - Buffer.byteLength(JSON.stringify({ ...sampleEnvelope(), payload: "" }), "utf8"),
    );
    const envelope: Envelope = { ...sampleEnvelope(), payload };
    assert.equal(Buffer.byteLength(JSON.stringify(envelope), "utf8"), MAX_FRAME_BYTES);
    assert.deepEqual(decodeFrame(JSON.stringify(envelope)), envelope);
  });
});

describe("flush", () => {
  it("decodes a final unterminated frame at flush", () => {
    const decoder = new FrameDecoder();
    assert.equal(decoder.push(JSON.stringify(sampleEnvelope())).length, 0);
    const frames = decoder.flush();
    assert.deepEqual(frames, [sampleEnvelope()]);
  });

  it("returns nothing when flushing an empty decoder", () => {
    assert.deepEqual(new FrameDecoder().flush(), []);
  });
});

describe("mixed chunks", () => {
  it("delivers valid frames and skips malformed lines when onError is given", () => {
    const decoder = new FrameDecoder();
    const errors: FrameError[] = [];
    const good = JSON.stringify(sampleEnvelope());
    const good2 = JSON.stringify({ ...sampleEnvelope(), messageId: "02K" });
    const frames = decoder.push(`${good}\nnot json\n${good2}\n`, (error) => {
      errors.push(error);
    });
    assert.deepEqual(frames, [sampleEnvelope(), { ...sampleEnvelope(), messageId: "02K" }]);
    assert.equal(errors.length, 1);
    assert.equal(errors[0]?.code, "FRAME_INVALID_JSON");
  });
});
