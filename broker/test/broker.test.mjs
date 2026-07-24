import assert from "node:assert/strict";
import { createHmac, randomBytes } from "node:crypto";
import { mkdtemp, readFile } from "node:fs/promises";
import { tmpdir } from "node:os";
import { join } from "node:path";
import test from "node:test";
import { WebSocket } from "ws";
import {
  authCanonical,
  base64url,
  decryptRelay,
  deriveSessionKey,
  encryptRelay,
} from "../../protocol/v2/index.mjs";
import { auditStableString } from "../audit.mjs";
import { createBroker } from "../broker.mjs";

const operatorKey = randomBytes(32);
const deviceKey = randomBytes(32);
const deniedKey = randomBytes(32);
const auditKey = randomBytes(32);
const id = () => base64url(randomBytes(18));
const envelope = (type, body = {}, extra = {}) => ({
  v: 2,
  type,
  messageId: id(),
  ts: Date.now(),
  body,
  ...extra,
});

async function fixture() {
  const dir = await mkdtemp(join(tmpdir(), "crc-broker-test-"));
  const auditPath = join(dir, "audit.jsonl");
  const broker = createBroker({
    listen: { host: "127.0.0.1", port: 0, path: "/ws" },
    auth: {
      windowMs: 30_000,
      attemptsPerMinute: 50,
      peers: [
        { role: "operator", principalId: "op-allowed", keyId: "k1", authKey: base64url(operatorKey), allowedDevices: ["device-one"] },
        { role: "operator", principalId: "op-denied", keyId: "k1", authKey: base64url(deniedKey), allowedDevices: [] },
        { role: "device", principalId: "device-one", keyId: "k1", authKey: base64url(deviceKey) },
      ],
    },
    sessions: { ttlMs: 10_000 },
    limits: { maxMessageBytes: 50_000, maxCiphertextBytes: 10_000 },
    heartbeat: { intervalMs: 20_000, timeoutMs: 60_000 },
    audit: { path: auditPath, hmacKey: base64url(auditKey) },
  });
  const address = await broker.listen();
  return { broker, auditPath, url: `ws://127.0.0.1:${address.port}/ws` };
}

function inbox(ws) {
  const queued = [];
  const waiters = [];
  ws.on("message", (data) => {
    const value = JSON.parse(data);
    const waiter = waiters.shift();
    if (waiter) waiter(value);
    else queued.push(value);
  });
  return {
    async next() {
      if (queued.length) return queued.shift();
      return new Promise((resolve, reject) => {
        const timeout = setTimeout(() => reject(new Error("message timeout")), 2_000);
        waiters.push((value) => {
          clearTimeout(timeout);
          resolve(value);
        });
      });
    },
    async type(type) {
      for (;;) {
        const message = await this.next();
        if (message.type === type) return message;
      }
    },
  };
}

async function connect(url) {
  const ws = new WebSocket(url);
  const messages = inbox(ws);
  await new Promise((resolve, reject) => {
    ws.once("open", resolve);
    ws.once("error", reject);
  });
  return { ws, messages };
}

async function authenticate(connection, { role, principalId, keyId = "k1", key, clientNonce = base64url(randomBytes(32)), metadata = {}, responseType = "auth.ok" }) {
  const challenge = await connection.messages.type("auth.challenge");
  assert.deepEqual(Object.keys(challenge.body), ["nonce"]);
  const message = envelope("auth.response");
  const proof = base64url(createHmac("sha256", key).update(authCanonical({
    role,
    principalId,
    keyId,
    brokerNonce: challenge.body.nonce,
    clientNonce,
    ts: message.ts,
  })).digest());
  message.body = { role, principalId, keyId, clientNonce, proof, metadata };
  connection.ws.send(JSON.stringify(message));
  return connection.messages.type(responseType);
}

test("challenge authentication succeeds and a reused client nonce is rejected", async () => {
  const fx = await fixture();
  const nonce = base64url(randomBytes(32));
  const first = await connect(fx.url);
  const ok = await authenticate(first, { role: "device", principalId: "device-one", key: deviceKey, clientNonce: nonce });
  assert.equal(ok.body.heartbeatSeconds, 20);

  const second = await connect(fx.url);
  const error = await authenticate(second, { role: "device", principalId: "device-one", key: deviceKey, clientNonce: nonce, responseType: "error" });
  assert.equal(error.body.code, "auth_replay");
  first.ws.terminate();
  second.ws.terminate();
  await fx.broker.close();
});

test("operator ACL denies unauthorized session", async () => {
  const fx = await fixture();
  const device = await connect(fx.url);
  await authenticate(device, { role: "device", principalId: "device-one", key: deviceKey });
  const operator = await connect(fx.url);
  await authenticate(operator, { role: "operator", principalId: "op-denied", key: deniedKey });
  operator.ws.send(JSON.stringify(envelope("session.open", { deviceId: "device-one" })));
  const denied = await operator.messages.type("session.denied");
  assert.equal(denied.body.reason, "acl");
  device.ws.terminate();
  operator.ws.terminate();
  await fx.broker.close();
});

test("presence is filtered by operator ACL", async () => {
  const fx = await fixture();
  const device = await connect(fx.url);
  await authenticate(device, {
    role: "device",
    principalId: "device-one",
    key: deviceKey,
    metadata: { platform: "windows", machineName: "private-host" },
  });
  const deniedOperator = await connect(fx.url);
  await authenticate(deniedOperator, {
    role: "operator",
    principalId: "op-denied",
    key: deniedKey,
  });
  const snapshot = await deniedOperator.messages.type("presence.snapshot");
  assert.deepEqual(snapshot.body.devices, []);
  device.ws.terminate();
  deniedOperator.ws.terminate();
  await fx.broker.close();
});

test("invalid nested peer metadata is rejected without crashing the broker", async () => {
  const fx = await fixture();
  const invalid = await connect(fx.url);
  const error = await authenticate(invalid, {
    role: "device",
    principalId: "device-one",
    key: deviceKey,
    metadata: { machineName: { deeply: { nested: true } } },
    responseType: "error",
  });
  assert.equal(error.body.code, "auth_invalid");

  const valid = await connect(fx.url);
  const ok = await authenticate(valid, {
    role: "device",
    principalId: "device-one",
    key: deviceKey,
    metadata: { machineName: "device-one" },
  });
  assert.equal(ok.type, "auth.ok");
  invalid.ws.terminate();
  valid.ws.terminate();
  await fx.broker.close();
});

test("relay is opaque, ordered, addressed, and replay protected", async () => {
  const fx = await fixture();
  const device = await connect(fx.url);
  await authenticate(device, { role: "device", principalId: "device-one", key: deviceKey, metadata: { platform: "macos" } });
  const operator = await connect(fx.url);
  await authenticate(operator, { role: "operator", principalId: "op-allowed", key: operatorKey });
  operator.ws.send(JSON.stringify(envelope("session.open", { deviceId: "device-one" })));
  const ready = await operator.messages.type("session.ready");
  await device.messages.type("session.ready");
  const relay = envelope("relay.data", {
    nonce: base64url(Buffer.alloc(12, 1)),
    ciphertext: base64url(Buffer.from("opaque-ciphertext")),
    tag: base64url(Buffer.alloc(16, 2)),
  }, {
    sessionId: ready.sessionId,
    seq: "1",
    from: "operator:op-allowed",
    to: "device:device-one",
  });
  operator.ws.send(JSON.stringify(relay));
  const routed = await device.messages.type("relay.data");
  assert.deepEqual(routed.body, relay.body);
  assert.equal(routed.messageId, relay.messageId);

  operator.ws.send(JSON.stringify(relay));
  const replay = await operator.messages.type("error");
  assert.equal(replay.body.code, "relay_replay");
  device.ws.terminate();
  operator.ws.terminate();
  await fx.broker.close();
});

test("operator and device exchange end-to-end encrypted command and result through the opaque broker", async () => {
  const fx = await fixture();
  const e2eeSecret = randomBytes(32);
  const device = await connect(fx.url);
  await authenticate(device, { role: "device", principalId: "device-one", key: deviceKey });
  const operator = await connect(fx.url);
  await authenticate(operator, { role: "operator", principalId: "op-allowed", key: operatorKey });
  operator.ws.send(JSON.stringify(envelope("session.open", { deviceId: "device-one" })));
  const operatorReady = await operator.messages.type("session.ready");
  const deviceReady = await device.messages.type("session.ready");
  assert.equal(operatorReady.sessionId, deviceReady.sessionId);

  const sessionId = operatorReady.sessionId;
  const key = deriveSessionKey({
    sharedSecret: e2eeSecret,
    sessionId,
    operatorId: "op-allowed",
    deviceId: "device-one",
  });
  const command = Buffer.from(JSON.stringify({
    type: "command.execute",
    requestId: "request-1",
    issuedAt: Date.now(),
    expiresAt: Date.now() + 60_000,
    body: { command: "plaintext-must-not-reach-broker" },
  }));
  const outbound = {
    v: 2,
    type: "relay.data",
    messageId: id(),
    ts: Date.now(),
    sessionId,
    seq: "1",
    from: "operator:op-allowed",
    to: "device:device-one",
  };
  outbound.body = encryptRelay({
    key,
    nonce: randomBytes(12),
    plaintext: command,
    sessionId,
    from: outbound.from,
    to: outbound.to,
    seq: outbound.seq,
    messageId: outbound.messageId,
    ts: outbound.ts,
  });
  operator.ws.send(JSON.stringify(outbound));
  const atDevice = await device.messages.type("relay.data");
  assert.equal(
    decryptRelay({ key, ...atDevice.body, ...atDevice }).toString("utf8"),
    command.toString("utf8"),
  );

  const result = Buffer.from(JSON.stringify({
    type: "command.complete",
    requestId: "request-1",
    issuedAt: Date.now(),
    expiresAt: Date.now() + 60_000,
    body: { exitCode: 0 },
  }));
  const inbound = {
    v: 2,
    type: "relay.data",
    messageId: id(),
    ts: Date.now(),
    sessionId,
    seq: "1",
    from: "device:device-one",
    to: "operator:op-allowed",
  };
  inbound.body = encryptRelay({
    key,
    nonce: randomBytes(12),
    plaintext: result,
    sessionId,
    from: inbound.from,
    to: inbound.to,
    seq: inbound.seq,
    messageId: inbound.messageId,
    ts: inbound.ts,
  });
  device.ws.send(JSON.stringify(inbound));
  const atOperator = await operator.messages.type("relay.data");
  assert.equal(
    decryptRelay({ key, ...atOperator.body, ...atOperator }).toString("utf8"),
    result.toString("utf8"),
  );

  device.ws.terminate();
  operator.ws.terminate();
  await fx.broker.close();
  const audit = await readFile(fx.auditPath, "utf8");
  assert.ok(!audit.includes("plaintext-must-not-reach-broker"));
});

test("audit JSONL is metadata-only and has a valid HMAC hash chain", async () => {
  const fx = await fixture();
  const device = await connect(fx.url);
  await authenticate(device, { role: "device", principalId: "device-one", key: deviceKey });
  device.ws.terminate();
  await fx.broker.close();
  const text = await readFile(fx.auditPath, "utf8");
  assert.ok(!text.includes(base64url(deviceKey)));
  assert.ok(!text.includes("proof"));
  assert.ok(!text.includes("ciphertext"));
  let previous = "0".repeat(64);
  for (const line of text.trim().split("\n")) {
    const record = JSON.parse(line);
    const { hash, ...unsigned } = record;
    assert.equal(unsigned.prevHash, previous);
    const expected = createHmac("sha256", auditKey).update(auditStableString(unsigned)).digest("hex");
    assert.equal(hash, expected);
    previous = hash;
  }
});
