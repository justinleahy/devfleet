#!/usr/bin/env node
// Fake official `claude` CLI for tests: no provider network or credentials.
import fs from "node:fs";
import path from "node:path";

const scenarioFile = path.join(process.cwd(), "fake-scenario");
const scenario = fs.existsSync(scenarioFile)
  ? fs.readFileSync(scenarioFile, "utf8").trim()
  : "happy";

const capture = {
  argv: process.argv.slice(1),
  cwd: process.cwd(),
  envKeys: Object.keys(process.env).sort(),
};

fs.writeFileSync(path.join(process.cwd(), "claude-capture.json"), JSON.stringify(capture, null, 2));

function emit(obj) {
  process.stdout.write(JSON.stringify(obj) + "\n");
}

function init() {
  emit({
    type: "system",
    subtype: "init",
    session_id: "claude-session-fake-1",
    model: "fake-model",
    tools: ["Read", "Edit", "Write"],
  });
}

if (scenario === "hang") {
  init();
  const exit = () => process.exit(143);
  process.on("SIGINT", exit);
  process.on("SIGTERM", exit);
  setInterval(() => {}, 60_000);
} else if (scenario === "crash") {
  init();
  setTimeout(() => {
    process.stderr.write("boom\n");
    process.exit(3);
  }, 40);
} else if (scenario === "auth") {
  init();
  process.stderr.write("Error: not logged in. Run `claude login`\n");
  process.stderr.write('{"api_key":"sk-ant-secretvalue1234567890abcdef"}\n');
  process.exit(1);
} else if (scenario === "malformed") {
  init();
  process.stdout.write("not-json\n");
  emit({ type: "mystery_event", extra: 1, session_id: "claude-session-fake-1" });
  emit({
    type: "assistant",
    message: {
      content: [{ type: "tool_use", name: "Read", input: { file_path: "/tmp/a" } }],
    },
  });
  emit({
    type: "stream_event",
    event: { type: "content_block_delta", delta: { text: "hi" } },
  });
  emit({
    type: "result",
    subtype: "success",
    session_id: "claude-session-fake-1",
    result: "done",
    usage: { input_tokens: 3, output_tokens: 5 },
  });
  process.exit(0);
} else {
  init();
  emit({
    type: "system",
    subtype: "api_retry",
    attempt: 1,
    max_retries: 3,
    session_id: "claude-session-fake-1",
  });
  emit({
    type: "assistant",
    message: {
      content: [{ type: "text", text: "working" }],
    },
  });
  emit({
    type: "assistant",
    message: {
      content: [{ type: "tool_use", name: "Read", input: { file_path: "/tmp/a" } }],
    },
  });
  emit({
    type: "user",
    message: {
      content: [{ type: "tool_result", content: "ok" }],
    },
  });
  emit({
    type: "stream_event",
    event: { type: "content_block_delta", delta: { text: "delta" } },
  });
  emit({
    type: "result",
    subtype: "success",
    session_id: "claude-session-fake-1",
    result: "all good",
    usage: { input_tokens: 11, output_tokens: 7, cache_read_tokens: 2 },
    total_cost_usd: 0,
  });
  process.exit(0);
}
