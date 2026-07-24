import crypto from 'node:crypto';

export const PROTOCOL_VERSION = 2;
export const MAX_CLOCK_SKEW_MS = 5 * 60 * 1000;

export function base64urlEncode(value) {
  return Buffer.from(value).toString('base64url');
}

export function decodeKey(value, name = 'key') {
  if (typeof value !== 'string' || value.length < 16) {
    throw new Error(`${name} must be a non-empty base64/base64url string`);
  }
  const normalized = value.replace(/-/g, '+').replace(/_/g, '/');
  const padding = '='.repeat((4 - (normalized.length % 4)) % 4);
  const decoded = Buffer.from(normalized + padding, 'base64');
  if (decoded.length !== 32) {
    throw new Error(`${name} must decode to exactly 32 bytes`);
  }
  return decoded;
}

export function authCanonical(role, principalId, keyId, brokerNonce, clientNonce, ts) {
  return `crc-v2-auth\n${role}\n${principalId}\n${keyId}\n${brokerNonce}\n${clientNonce}\n${ts}`;
}

export function authProof(key, role, principalId, keyId, brokerNonce, clientNonce, ts) {
  return crypto.createHmac('sha256', key)
    .update(authCanonical(role, principalId, keyId, brokerNonce, clientNonce, ts), 'utf8')
    .digest('base64url');
}

export function aadCanonical(sessionId, from, to, seq, messageId, ts) {
  return `crc-v2-aad\n${sessionId}\n${from}\n${to}\n${seq}\n${messageId}\n${ts}`;
}

export function deriveSessionKey(e2eeKey, sessionId, operatorId, deviceId) {
  return Buffer.from(crypto.hkdfSync(
    'sha256',
    e2eeKey,
    Buffer.from(sessionId, 'utf8'),
    Buffer.from(`crc-v2-e2ee|${operatorId}|${deviceId}`, 'utf8'),
    32,
  ));
}

export function encryptRelay(inner, {
  key,
  sessionId,
  from,
  to,
  seq,
  messageId = crypto.randomUUID(),
  ts = Date.now(),
  nonce = crypto.randomBytes(12),
} = {}) {
  const seqText = String(seq);
  const cipher = crypto.createCipheriv('aes-256-gcm', key, nonce);
  cipher.setAAD(Buffer.from(aadCanonical(sessionId, from, to, seqText, messageId, ts), 'utf8'));
  const plaintext = Buffer.from(JSON.stringify(inner), 'utf8');
  const ciphertext = Buffer.concat([cipher.update(plaintext), cipher.final()]);
  const tag = cipher.getAuthTag();
  return {
    v: PROTOCOL_VERSION,
    type: 'relay.data',
    messageId,
    ts,
    sessionId,
    seq: seqText,
    from,
    to,
    body: {
      nonce: nonce.toString('base64url'),
      ciphertext: ciphertext.toString('base64url'),
      tag: tag.toString('base64url'),
    },
  };
}

export function decryptRelay(message, key) {
  validateRelayEnvelope(message);
  const nonce = Buffer.from(message.body.nonce, 'base64url');
  const ciphertext = Buffer.from(message.body.ciphertext, 'base64url');
  const tag = Buffer.from(message.body.tag, 'base64url');
  if (nonce.length !== 12 || tag.length !== 16) {
    throw new Error('invalid AES-GCM nonce or tag length');
  }
  const decipher = crypto.createDecipheriv('aes-256-gcm', key, nonce);
  decipher.setAAD(Buffer.from(aadCanonical(
    message.sessionId,
    message.from,
    message.to,
    message.seq,
    message.messageId,
    message.ts,
  ), 'utf8'));
  decipher.setAuthTag(tag);
  const plaintext = Buffer.concat([decipher.update(ciphertext), decipher.final()]);
  return JSON.parse(plaintext.toString('utf8'));
}

export function makeEnvelope(type, body = {}, fields = {}) {
  return {
    v: PROTOCOL_VERSION,
    type,
    messageId: crypto.randomUUID(),
    ts: Date.now(),
    ...fields,
    body,
  };
}

export function validateBaseEnvelope(message) {
  if (!message || message.v !== PROTOCOL_VERSION || typeof message.type !== 'string') {
    throw new Error('invalid CRC v2 envelope');
  }
  if (typeof message.messageId !== 'string' || message.messageId.length < 8) {
    throw new Error('invalid messageId');
  }
  if (!Number.isSafeInteger(message.ts) || Math.abs(Date.now() - message.ts) > MAX_CLOCK_SKEW_MS) {
    throw new Error('message timestamp outside allowed clock skew');
  }
}

export function validateRelayEnvelope(message) {
  validateBaseEnvelope(message);
  if (message.type !== 'relay.data'
      || typeof message.sessionId !== 'string'
      || !/^[1-9][0-9]*$/.test(message.seq)
      || typeof message.from !== 'string'
      || typeof message.to !== 'string'
      || typeof message.body?.nonce !== 'string'
      || typeof message.body?.ciphertext !== 'string'
      || typeof message.body?.tag !== 'string') {
    throw new Error('invalid relay.data envelope');
  }
}

export class ReplayGuard {
  constructor(limit = 10_000) {
    this.limit = limit;
    this.messageIds = new Set();
    this.order = [];
    this.lastSeq = new Map();
  }

  acceptMessage(message) {
    validateBaseEnvelope(message);
    if (this.messageIds.has(message.messageId)) {
      throw new Error('replayed messageId');
    }
    this.messageIds.add(message.messageId);
    this.order.push(message.messageId);
    while (this.order.length > this.limit) {
      this.messageIds.delete(this.order.shift());
    }
  }

  acceptRelay(message) {
    this.acceptMessage(message);
    validateRelayEnvelope(message);
    const seq = BigInt(message.seq);
    const previous = this.lastSeq.get(message.sessionId) ?? 0n;
    if (seq !== previous + 1n) {
      throw new Error(`unexpected relay sequence ${seq}; expected ${previous + 1n}`);
    }
    this.lastSeq.set(message.sessionId, seq);
  }

  closeSession(sessionId) {
    this.lastSeq.delete(sessionId);
  }
}
