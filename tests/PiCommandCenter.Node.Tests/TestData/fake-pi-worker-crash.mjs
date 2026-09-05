// Fake Pi worker used only by tests: speaks the strict NDJSON stdio protocol without any
// provider network, authentication, or model access. It answers the `session.start`
// handshake, optionally emits one `event` frame, and optionally exits to simulate a crash.
import readline from 'node:readline';

const crash = true;
const rl = readline.createInterface({ input: process.stdin });

function send(frame) {
  process.stdout.write(JSON.stringify(frame) + '\n');
}

rl.on('line', (line) => {
  let message;
  try {
    message = JSON.parse(line);
  } catch {
    return; // strict framing: never reply to garbage
  }
  if (message.kind !== 'request') {
    return;
  }
  if (message.type === 'session.input') {
    send({ protocolVersion: 1, messageId: message.messageId, kind: 'response', sessionId: message.sessionId, type: 'session.input', payload: { queued: true } });
    return;
  }
  if (message.type === 'session.cancel') {
    send({ protocolVersion: 1, messageId: message.messageId, kind: 'response', sessionId: message.sessionId, type: 'session.cancel', payload: { cancelled: true } });
    return;
  }
  if (message.type === 'goodbye') {
    send({ protocolVersion: 1, messageId: message.messageId, kind: 'response', sessionId: message.sessionId, type: 'goodbye' });
    setTimeout(() => process.exit(0), 10);
    return;
  }
  if (message.type !== 'session.start') {
    return;
  }

  send({
    protocolVersion: 1,
    messageId: message.messageId,
    kind: 'response',
    sessionId: message.sessionId,
    type: 'session.start',
    payload: { sdkSessionId: 'fake-provider-session' },
  });

  if (!crash) {
    send({
      protocolVersion: 1,
      messageId: 'evt-1',
      kind: 'event',
      sessionId: message.sessionId,
      type: 'turn.started',
      payload: { seq: 4 },
    });
  } else {
    setTimeout(() => process.exit(3), 25);
  }
});
