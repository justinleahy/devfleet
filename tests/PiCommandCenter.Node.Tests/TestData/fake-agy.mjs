// Fake official-compatible `agy` for Node tests. No provider network or credentials.
import fs from "node:fs";
import readline from "node:readline";

const dump = process.env.AGY_TEST_DUMP;
const mode = process.env.AGY_TEST_MODE || "happy";
const conversationId = "agy-conv-1";

if (dump) {
  fs.writeFileSync(
    dump,
    JSON.stringify({ argv: process.argv.slice(2), cwd: process.cwd() }) + "\n",
  );
}

if (process.env.AGY_TEST_TRY_WRITE === "1") {
  const marker = "keep.txt";
  try {
    fs.writeFileSync(marker, "mutated-by-agy");
  } catch {
    // expected under a read-only repository bind
  }
  try {
    fs.writeFileSync("MUTATED.txt", "owned");
  } catch {
    // expected under a read-only repository bind
  }
}

let outstanding = 0;
let inits = 0;
let turns = 0;

process.on("SIGINT", () => {
  process.stdout.write(
    JSON.stringify({
      event: "result",
      conversation_id: conversationId,
      status: "INTERRUPTED",
    }) + "\n",
  );
  process.exit(0);
});

function emit(obj) {
  process.stdout.write(JSON.stringify(obj) + "\n");
}

const rl = readline.createInterface({ input: process.stdin });
rl.on("line", (line) => {
  let message;
  try {
    message = JSON.parse(line);
  } catch {
    return;
  }
  if (message.event !== "user") {
    return;
  }

  if (outstanding > 0 && dump) {
    fs.appendFileSync(dump + ".overlap", "overlap\n");
  }
  outstanding += 1;
  turns += 1;

  if (inits === 0) {
    inits += 1;
    emit({
      event: "init",
      conversation_id: conversationId,
      init: {
        cwd: process.cwd(),
        tools: ["read_file"],
        permission_mode: "request-review",
      },
    });
  } else if (mode === "second-init") {
    emit({
      event: "init",
      conversation_id: conversationId,
      init: { cwd: process.cwd(), tools: [] },
    });
  }

  if (mode === "malformed") {
    process.stdout.write("this is not json\n");
  }

  if (mode === "unknown") {
    emit({ event: "future_event", foo: 1, conversation_id: conversationId });
  }

  if (mode === "hang") {
    return;
  }

  emit({
    event: "step_update",
    conversation_id: conversationId,
    step_update: {
      conversation_id: conversationId,
      step_index: 0,
      state: "DONE",
      step_type: "user_input",
    },
  });
  emit({
    event: "step_update",
    conversation_id: conversationId,
    step_update: {
      step_index: 1,
      state: "ACTIVE",
      step_type: "agent_response",
      text_delta: "hello",
    },
  });
  emit({
    event: "step_update",
    conversation_id: conversationId,
    step_update: {
      step_index: 1,
      state: "DONE",
      step_type: "agent_response",
    },
  });
  emit({
    event: "step_update",
    conversation_id: conversationId,
    step_update: {
      step_index: 2,
      state: "ACTIVE",
      step_type: "tool",
      tool_name: "read_file",
      tool_info: { name: "read_file", parameters: {} },
    },
  });
  emit({
    event: "step_update",
    conversation_id: conversationId,
    step_update: {
      step_index: 2,
      state: "DONE",
      step_type: "tool",
      tool_info: { name: "read_file", output: "ok" },
    },
  });
  emit({
    event: "step_update",
    conversation_id: conversationId,
    step_update: {
      step_index: 3,
      state: "DONE",
      step_type: "checkpoint",
    },
  });
  emit({
    event: "step_update",
    conversation_id: conversationId,
    step_update: {
      step_index: 4,
      state: "DONE",
      step_type: "mystery_step",
    },
  });
  emit({
    event: "step_update",
    conversation_id: conversationId,
    step_update: {
      step_index: 5,
      state: "ACTIVE",
      step_type: "agent_response",
      subagent_info: {
        subagents: [
          {
            type_name: "explore",
            role: "researcher",
            conversation_id: "sub-1",
          },
        ],
      },
    },
  });

  const status = mode === "error" ? "ERROR" : "SUCCESS";
  emit({
    event: "result",
    conversation_id: conversationId,
    status,
    response: `done-${turns}`,
    usage: { input_tokens: 1, output_tokens: 2, total_tokens: 3 },
  });
  outstanding -= 1;

  if (mode === "auth") {
    process.stderr.write("not authenticated. Complete agy login locally.\n");
    process.stderr.write('{"password":"hunter2","token":"eyJhbGciOiJIUzI1NiIsInR5cCI6IkpXVCJ9.aaa.bbb"}\n');
    process.exit(12);
  }

  if (mode === "crash") {
    process.exitCode = 7;
    process.exit(7);
  }
});

rl.on("close", () => {
  if (mode !== "crash" && mode !== "auth") {
    process.exit(0);
  }
});
