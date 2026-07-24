import {
  createCipheriv,
  createDecipheriv,
  hkdfSync,
  timingSafeEqual,
} from "node:crypto";

export const VERSION = 2;
export const AUTH_PREFIX = "crc-v2-auth";
export const E2EE_INFO_PREFIX = "crc-v2-e2ee";
export const AAD_PREFIX = "crc-v2-aad";

export function base64url(value) {
  return Buffer.from(value).toString("base64url");
}

export function fromBase64url(value, expectedLength) {
  if (typeof value !== "string" || !/^[A-Za-z0-9_-]*$/.test(value)) {
    throw new TypeError("invalid base64url");
  }
  const decoded = Buffer.from(value, "base64url");
  if (base64url(decoded) !== value) throw new TypeError("non-canonical base64url");
  if (expectedLength !== undefined && decoded.length !== expectedLength) {
    throw new RangeError(`expected ${expectedLength} bytes`);
  }
  return decoded;
}

export function authCanonical({
  role,
  principalId,
  keyId,
  brokerNonce,
  clientNonce,
  ts,
}) {
  return [AUTH_PREFIX, role, principalId, keyId, brokerNonce, clientNonce, String(ts)].join("\n");
}

export function safeEqualBase64url(left, right) {
  try {
    const a = fromBase64url(left);
    const b = fromBase64url(right);
    return a.length === b.length && timingSafeEqual(a, b);
  } catch {
    return false;
  }
}

export function deriveSessionKey({ sharedSecret, sessionId, operatorId, deviceId }) {
  const ikm = Buffer.isBuffer(sharedSecret)
    ? sharedSecret
    : fromBase64url(sharedSecret, 32);
  return Buffer.from(
    hkdfSync(
      "sha256",
      ikm,
      Buffer.from(sessionId, "utf8"),
      Buffer.from(`${E2EE_INFO_PREFIX}|${operatorId}|${deviceId}`, "utf8"),
      32,
    ),
  );
}

export function relayAad({ sessionId, from, to, seq, messageId, ts }) {
  return Buffer.from(
    [AAD_PREFIX, sessionId, from, to, String(seq), messageId, String(ts)].join("\n"),
    "utf8",
  );
}

export function encryptRelay({
  key,
  nonce,
  plaintext,
  sessionId,
  from,
  to,
  seq,
  messageId,
  ts,
}) {
  const rawKey = Buffer.isBuffer(key) ? key : fromBase64url(key, 32);
  const rawNonce = Buffer.isBuffer(nonce) ? nonce : fromBase64url(nonce, 12);
  if (rawKey.length !== 32 || rawNonce.length !== 12) throw new RangeError("invalid key or nonce");
  const cipher = createCipheriv("aes-256-gcm", rawKey, rawNonce);
  cipher.setAAD(relayAad({ sessionId, from, to, seq, messageId, ts }));
  const ciphertext = Buffer.concat([cipher.update(plaintext), cipher.final()]);
  return {
    nonce: base64url(rawNonce),
    ciphertext: base64url(ciphertext),
    tag: base64url(cipher.getAuthTag()),
  };
}

export function decryptRelay({
  key,
  nonce,
  ciphertext,
  tag,
  sessionId,
  from,
  to,
  seq,
  messageId,
  ts,
}) {
  const rawKey = Buffer.isBuffer(key) ? key : fromBase64url(key, 32);
  const decipher = createDecipheriv("aes-256-gcm", rawKey, fromBase64url(nonce, 12));
  decipher.setAAD(relayAad({ sessionId, from, to, seq, messageId, ts }));
  decipher.setAuthTag(fromBase64url(tag, 16));
  return Buffer.concat([decipher.update(fromBase64url(ciphertext)), decipher.final()]);
}
