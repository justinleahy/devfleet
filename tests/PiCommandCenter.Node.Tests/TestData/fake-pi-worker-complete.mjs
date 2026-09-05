// Fake Pi worker used only by tests: answers the session.start handshake and then emits a
// normalized session.completed event before exiting cleanly — the successful terminal.
import readline from 'node:readline';

const rl = readline.createInterface({ input: process.stdin });

function send(frame) {
  process.stdout.write(JSON.stringify(frame) + '\n');
}

rl.on('line', (line) => {
  let message;
  try {
    message = JSON.parse(line);
  } catch {
    return;
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
  send({
    protocolVersion: 1,
    messageId: 'evt-complete-1',
    kind: 'event',
    sessionId: message.sessionId,
    type: 'session.completed',
    payload: { result: 'done' },
  });
  setTimeout(() => process.exit(0), 20);
});
