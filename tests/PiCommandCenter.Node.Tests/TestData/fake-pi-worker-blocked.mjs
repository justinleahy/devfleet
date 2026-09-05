// Fake Pi worker used only by tests: reports provider authentication as blocked, then closes.
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
  if (message.type !== 'session.start') return;

  send({
    protocolVersion: 1,
    messageId: message.messageId,
    kind: 'response',
    sessionId: message.sessionId,
    type: 'session.start',
    payload: { sdkSessionId: 'blocked-provider-session' },
  });
  send({
    protocolVersion: 1,
    messageId: 'evt-blocked-1',
    kind: 'event',
    sessionId: message.sessionId,
    type: 'session.snapshot',
    payload: { workState: 'Blocked', attention: 'InputRequired', statusReason: 'provider login required' },
  });
  send({
    protocolVersion: 1,
    messageId: 'evt-blocked-2',
    kind: 'event',
    sessionId: message.sessionId,
    type: 'session.closed',
    payload: { reason: 'provider_auth_required' },
  });
});
