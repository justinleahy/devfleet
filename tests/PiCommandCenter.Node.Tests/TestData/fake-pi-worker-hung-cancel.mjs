// Fake Pi worker used only by tests: starts normally but never answers session.cancel.
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
  if (message.kind !== 'request') return;
  if (message.type === 'session.start') {
    send({ protocolVersion: 1, messageId: message.messageId, kind: 'response', sessionId: message.sessionId, type: 'session.start', payload: { sdkSessionId: 'hung-cancel-provider' } });
    return;
  }
  if (message.type === 'session.input') {
    send({ protocolVersion: 1, messageId: message.messageId, kind: 'response', sessionId: message.sessionId, type: 'session.input', payload: { queued: true } });
    return;
  }
  if (message.type === 'session.cancel') return;
  if (message.type === 'goodbye') {
    send({ protocolVersion: 1, messageId: message.messageId, kind: 'response', sessionId: message.sessionId, type: 'goodbye' });
    setTimeout(() => process.exit(0), 10);
  }
});
