import assert from 'node:assert/strict';
import crypto from 'node:crypto';
import test from 'node:test';
import {
  ReplayGuard,
  aadCanonical,
  authCanonical,
  authProof,
  decryptRelay,
  deriveSessionKey,
  encryptRelay,
} from '../server/protocol-v2.mjs';

test('auth canonical and proof are deterministic', () => {
  const key = Buffer.from('000102030405060708090a0b0c0d0e0f101112131415161718191a1b1c1d1e1f', 'hex');
  assert.equal(
    authCanonical('operator', 'op-1', 'key-1', 'broker-nonce', 'client-nonce', 1700000000000),
    'crc-v2-auth\noperator\nop-1\nkey-1\nbroker-nonce\nclient-nonce\n1700000000000',
  );
  assert.equal(
    authProof(key, 'operator', 'op-1', 'key-1', 'broker-nonce', 'client-nonce', 1700000000000),
    'oNaIeb5ZCc8dsLXn8pxEaYU9zjft2Gd9k7FeXrnc6Js',
  );
});

test('HKDF and AES-256-GCM relay vector round trips', () => {
  const rootKey = Buffer.from('000102030405060708090a0b0c0d0e0f101112131415161718191a1b1c1d1e1f', 'hex');
  const key = deriveSessionKey(rootKey, 'session-1', 'op-1', 'mac-1');
  assert.equal(key.toString('hex'), 'b10df069b55836ac0f10ab17daf5a70afc62d98c3c1c74712d62afdaa51d09f4');
  const timestamp = Date.now();
  const inner = {
    type: 'command.execute',
    requestId: 'request-1',
    issuedAt: 1700000000000,
    expiresAt: 1700000060000,
    body: { kind: 'shell', command: 'id', runAsRoot: false },
  };
  const relay = encryptRelay(inner, {
    key,
    sessionId: 'session-1',
    from: 'operator:op-1',
    to: 'device:mac-1',
    seq: '1',
    messageId: '00000000-0000-4000-8000-000000000001',
    ts: timestamp,
    nonce: Buffer.from('000102030405060708090a0b', 'hex'),
  });
  assert.equal(
    aadCanonical(relay.sessionId, relay.from, relay.to, relay.seq, relay.messageId, relay.ts),
    `crc-v2-aad\nsession-1\noperator:op-1\ndevice:mac-1\n1\n00000000-0000-4000-8000-000000000001\n${timestamp}`,
  );
  assert.deepEqual(decryptRelay(relay, key), inner);
});

test('replay guard rejects duplicate IDs and non-contiguous session sequence', () => {
  const guard = new ReplayGuard();
  const base = {
    v: 2,
    type: 'relay.data',
    messageId: crypto.randomUUID(),
    ts: Date.now(),
    sessionId: 's',
    seq: '1',
    from: 'operator:o',
    to: 'device:d',
    body: { nonce: 'AAAAAAAAAAAAAAAA', ciphertext: '', tag: 'AAAAAAAAAAAAAAAAAAAAAA' },
  };
  guard.acceptRelay(base);
  assert.throws(() => guard.acceptRelay(base), /replayed/);
  assert.throws(() => guard.acceptRelay({ ...base, messageId: crypto.randomUUID(), seq: '3' }), /expected 2/);
});
