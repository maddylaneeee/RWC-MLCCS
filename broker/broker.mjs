import { createHash, createHmac, randomBytes, randomUUID } from "node:crypto";
import { createServer } from "node:http";
import { isIP } from "node:net";
import { WebSocket, WebSocketServer } from "ws";
import {
  VERSION,
  authCanonical,
  base64url,
  fromBase64url,
  safeEqualBase64url,
} from "../protocol/v2/index.mjs";
import { AuditLog } from "./audit.mjs";

const ID_PATTERN = /^[A-Za-z0-9._:@-]{1,128}$/;
const MESSAGE_ID_PATTERN = /^[A-Za-z0-9_-]{16,128}$/;
const DECIMAL_PATTERN = /^(?:0|[1-9][0-9]*)$/;

function send(ws, message) {
  if (ws.readyState === WebSocket.OPEN) {
    try {
      ws.send(JSON.stringify({
        v: VERSION,
        messageId: base64url(randomBytes(18)),
        ts: Date.now(),
        ...message,
      }));
    } catch {
      close(ws, 1011, "message serialization failed");
    }
  }
}

function close(ws, code, reason) {
  if (ws.readyState === WebSocket.OPEN || ws.readyState === WebSocket.CONNECTING) {
    ws.close(code, reason.slice(0, 123));
  }
}

function assertObject(value, name) {
  if (!value || typeof value !== "object" || Array.isArray(value)) throw new Error(`${name} must be object`);
}

function validId(value) {
  return typeof value === "string" && ID_PATTERN.test(value);
}

function nowInWindow(ts, windowMs) {
  return Number.isSafeInteger(ts) && Math.abs(Date.now() - ts) <= windowMs;
}

function exactKeys(value, keys) {
  return Object.keys(value).sort().join("\0") === [...keys].sort().join("\0");
}

function sanitizeMetadata(metadata) {
  const allowed = new Set(["adapter", "host", "userName", "machineName", "platform", "appVersion"]);
  const sanitized = {};
  for (const [key, value] of Object.entries(metadata)) {
    if (!allowed.has(key) || typeof value !== "string" || value.length > 256) {
      throw new Error("invalid peer metadata");
    }
    sanitized[key] = value;
  }
  return sanitized;
}

function clientAddress(req) {
  const remote = String(req.socket.remoteAddress ?? "unknown");
  const isLoopback = remote === "127.0.0.1" || remote === "::1" || remote === "::ffff:127.0.0.1";
  if (!isLoopback) return remote;
  const forwarded = String(req.headers["x-forwarded-for"] ?? "")
    .split(",")
    .map((value) => value.trim())
    .filter(Boolean)
    .at(-1);
  return forwarded && isIP(forwarded) ? forwarded : remote;
}

export function validateConfig(config) {
  assertObject(config, "config");
  assertObject(config.listen, "listen");
  assertObject(config.auth, "auth");
  assertObject(config.audit, "audit");
  if (config.listen.host !== "127.0.0.1") throw new Error("broker must bind to 127.0.0.1");
  if (!Number.isInteger(config.listen.port) || config.listen.port < 0) throw new Error("invalid listen.port");
  if (config.listen.path !== "/ws") throw new Error("internal path must be /ws");
  if (!Array.isArray(config.auth.peers) || config.auth.peers.length === 0) throw new Error("auth.peers required");
  const authWindow = config.auth.windowMs ?? 30_000;
  const attempts = config.auth.attemptsPerMinute ?? 20;
  const sessionTtl = config.sessions?.ttlMs ?? 28_800_000;
  const maxMessage = config.limits?.maxMessageBytes ?? 1_100_000;
  const maxCiphertext = config.limits?.maxCiphertextBytes ?? 1_048_576;
  const maxConnections = config.limits?.maxConnections ?? 200;
  const maxUnauthenticated = config.limits?.maxUnauthenticated ?? 50;
  const heartbeatInterval = config.heartbeat?.intervalMs ?? 20_000;
  const heartbeatTimeout = config.heartbeat?.timeoutMs ?? 60_000;
  if (!Number.isInteger(authWindow) || authWindow < 10_000 || authWindow > 120_000) throw new Error("invalid auth.windowMs");
  if (!Number.isInteger(attempts) || attempts < 1 || attempts > 1_000) throw new Error("invalid auth.attemptsPerMinute");
  if (!Number.isInteger(sessionTtl) || sessionTtl < 1_000 || sessionTtl > 86_400_000) throw new Error("invalid sessions.ttlMs");
  if (!Number.isInteger(maxMessage) || maxMessage < 1_024 || maxMessage > 2_000_000) throw new Error("invalid limits.maxMessageBytes");
  if (!Number.isInteger(maxCiphertext) || maxCiphertext < 1 || maxCiphertext > maxMessage) throw new Error("invalid limits.maxCiphertextBytes");
  if (!Number.isInteger(maxConnections) || maxConnections < 1 || maxConnections > 10_000) throw new Error("invalid limits.maxConnections");
  if (!Number.isInteger(maxUnauthenticated) || maxUnauthenticated < 1 || maxUnauthenticated > maxConnections) throw new Error("invalid limits.maxUnauthenticated");
  if (!Number.isInteger(heartbeatInterval) || heartbeatInterval < 5_000 || heartbeatInterval > 120_000) throw new Error("invalid heartbeat.intervalMs");
  if (!Number.isInteger(heartbeatTimeout) || heartbeatTimeout < heartbeatInterval * 2 || heartbeatTimeout > 600_000) throw new Error("invalid heartbeat.timeoutMs");
  const peerKeys = new Set();
  for (const peer of config.auth.peers) {
    if (!["operator", "device"].includes(peer.role) || !validId(peer.principalId) || !validId(peer.keyId)) {
      throw new Error("invalid peer identity");
    }
    if (fromBase64url(peer.authKey, 32).length !== 32) throw new Error("invalid peer authKey");
    const identity = `${peer.role}\0${peer.principalId}\0${peer.keyId}`;
    if (peerKeys.has(identity)) throw new Error("duplicate peer key");
    peerKeys.add(identity);
    if (peer.role === "operator" && (!Array.isArray(peer.allowedDevices) || !peer.allowedDevices.every(validId))) {
      throw new Error("operator allowedDevices required");
    }
  }
  return {
    authWindowMs: authWindow,
    authAttemptsPerMinute: attempts,
    sessionTtlMs: sessionTtl,
    maxMessageBytes: maxMessage,
    maxCiphertextBytes: maxCiphertext,
    maxConnections,
    maxUnauthenticated,
    heartbeatIntervalMs: heartbeatInterval,
    heartbeatTimeoutMs: heartbeatTimeout,
  };
}

export function createBroker(config) {
  const options = validateConfig(config);
  const audit = new AuditLog(config.audit);
  const peers = new Map(
    config.auth.peers.map((peer) => [
      `${peer.role}\0${peer.principalId}\0${peer.keyId}`,
      { ...peer, authKey: Buffer.from(peer.authKey, "base64url") },
    ]),
  );
  const authenticated = new Set();
  const devices = new Map();
  const sessions = new Map();
  const usedAuthNonces = new Map();
  const authRates = new Map();
  let unauthenticatedCount = 0;
  const httpServer = createServer((req, res) => {
    const health = { ok: audit.healthy, auditOk: audit.healthy, v: VERSION };
    res.writeHead(req.url === "/healthz" ? (health.ok ? 200 : 503) : 404, { "content-type": "application/json" });
    res.end(JSON.stringify(req.url === "/healthz" ? health : { error: "not_found" }));
  });
  const wss = new WebSocketServer({ noServer: true, maxPayload: options.maxMessageBytes });

  function auditPeer(event, state, extra = {}) {
    void audit.write(event, {
      connectionId: state.connectionId,
      role: state.role,
      principalId: state.principalId,
      keyId: state.keyId,
      ...extra,
    });
  }

  function reject(ws, state, code, reason, auditEvent = "protocol.reject") {
    auditPeer(auditEvent, state, { code, reason });
    send(ws, { type: "error", body: { code, reason } });
    close(ws, 1008, reason);
  }

  function removeSession(session, reason) {
    if (!sessions.delete(session.id)) return;
    clearTimeout(session.timer);
    send(session.operator.ws, { type: "session.closed", sessionId: session.id, body: { reason } });
    send(session.device.ws, { type: "session.closed", sessionId: session.id, body: { reason } });
    void audit.write("session.closed", {
      sessionId: session.id,
      operatorId: session.operator.principalId,
      deviceId: session.device.principalId,
      reason,
    });
  }

  function presenceSnapshot(state) {
    const allowed = new Set(state.peer.allowedDevices);
    send(state.ws, {
      type: "presence.snapshot",
      body: {
        devices: [...devices.values()]
          .filter((device) => allowed.has(device.principalId))
          .map((device) => ({
            deviceId: device.principalId,
            metadata: device.metadata,
            connectedAt: device.authenticatedAt,
          })),
      },
    });
  }

  function broadcastPresence() {
    for (const state of authenticated) if (state.role === "operator") presenceSnapshot(state);
  }

  function cleanup(ws, state) {
    if (state.countedUnauthenticated) {
      unauthenticatedCount -= 1;
      state.countedUnauthenticated = false;
    }
    authenticated.delete(state);
    if (state.role === "device" && devices.get(state.principalId)?.ws === ws) {
      devices.delete(state.principalId);
      broadcastPresence();
    }
    for (const session of [...sessions.values()]) {
      if (session.operator.ws === ws || session.device.ws === ws) removeSession(session, "peer_disconnected");
    }
    if (state.authenticatedAt) auditPeer("peer.disconnected", state);
  }

  function checkRate(ip) {
    const now = Date.now();
    const update = (key) => {
      const recent = (authRates.get(key) ?? []).filter((time) => now - time < 60_000);
      recent.push(now);
      authRates.set(key, recent);
      return recent.length;
    };
    const perAddress = update(`ip:${ip}`);
    const global = update("global");
    if (authRates.size > 10_000) authRates.delete(authRates.keys().next().value);
    return perAddress <= options.authAttemptsPerMinute
      && global <= options.authAttemptsPerMinute * 10;
  }

  function pruneNonces() {
    const cutoff = Date.now() - options.authWindowMs;
    for (const [nonce, usedAt] of usedAuthNonces) if (usedAt < cutoff) usedAuthNonces.delete(nonce);
  }

  function handleAuthResponse(ws, state, message) {
    if (state.phase !== "challenge") return reject(ws, state, "auth_state", "unexpected auth.response", "auth.failed");
    if (!checkRate(state.ip)) return reject(ws, state, "rate_limited", "too many authentication attempts", "auth.rate_limited");
    if (!message.body || typeof message.body !== "object" || !exactKeys(message.body, ["role", "principalId", "keyId", "clientNonce", "proof", "metadata"])) {
      return reject(ws, state, "auth_invalid", "invalid authentication body", "auth.failed");
    }
    const { role, principalId, keyId, clientNonce, proof, metadata = {} } = message.body;
    if (
      !["operator", "device"].includes(role) ||
      !validId(principalId) ||
      !validId(keyId) ||
      !metadata ||
      typeof metadata !== "object" ||
      Array.isArray(metadata) ||
      !nowInWindow(message.ts, options.authWindowMs)
    ) {
      return reject(ws, state, "auth_invalid", "invalid authentication response", "auth.failed");
    }
    try {
      fromBase64url(clientNonce, 32);
    } catch {
      return reject(ws, state, "auth_invalid", "invalid authentication nonce", "auth.failed");
    }
    pruneNonces();
    if (usedAuthNonces.has(clientNonce)) return reject(ws, state, "auth_replay", "authentication nonce reused", "auth.replay");
    const peer = peers.get(`${role}\0${principalId}\0${keyId}`);
    if (!peer) return reject(ws, state, "auth_denied", "unknown peer", "auth.failed");
    let safeMetadata;
    try {
      safeMetadata = sanitizeMetadata(metadata);
    } catch {
      return reject(ws, state, "auth_invalid", "invalid peer metadata", "auth.failed");
    }
    Object.assign(state, { role, principalId, keyId, clientNonce, peer, metadata: safeMetadata });
    usedAuthNonces.set(clientNonce, Date.now());
    const canonical = authCanonical({ ...state, ts: message.ts });
    const expected = base64url(createHmac("sha256", state.peer.authKey).update(canonical).digest());
    if (!safeEqualBase64url(proof, expected)) return reject(ws, state, "auth_denied", "invalid proof", "auth.failed");
    state.phase = "authenticated";
    if (state.countedUnauthenticated) {
      unauthenticatedCount -= 1;
      state.countedUnauthenticated = false;
    }
    clearTimeout(state.authTimer);
    state.authenticatedAt = Date.now();
    authenticated.add(state);
    if (state.role === "device") {
      const existing = devices.get(state.principalId);
      if (existing && existing.ws !== ws) close(existing.ws, 4001, "superseded");
      devices.set(state.principalId, state);
    }
    send(ws, {
      type: "auth.ok",
      body: {
        connectionId: state.connectionId,
        heartbeatSeconds: options.heartbeatIntervalMs / 1000,
        serverTime: Date.now(),
      },
    });
    if (state.role === "operator") presenceSnapshot(state);
    else broadcastPresence();
    auditPeer("auth.succeeded", state);
  }

  function openSession(ws, state, message) {
    const deviceId = message.body?.deviceId;
    if (state.role !== "operator" || !validId(deviceId)) return reject(ws, state, "session_invalid", "invalid session request");
    const allowed = state.peer.allowedDevices.includes(deviceId);
    if (!allowed) {
      auditPeer("session.denied", state, { deviceId, reason: "acl" });
      return send(ws, { type: "session.denied", body: { deviceId, reason: "acl" } });
    }
    const device = devices.get(deviceId);
    if (!device || device.ws.readyState !== WebSocket.OPEN) {
      auditPeer("session.denied", state, { deviceId, reason: "offline" });
      return send(ws, { type: "session.denied", body: { deviceId, reason: "offline" } });
    }
    const id = randomUUID();
    const expiresAt = Date.now() + options.sessionTtlMs;
    const session = {
      id,
      operator: state,
      device,
      expiresAt,
      seq: new Map([[state.connectionId, 0n], [device.connectionId, 0n]]),
      messageIds: new Set(),
    };
    session.timer = setTimeout(() => removeSession(session, "expired"), options.sessionTtlMs);
    session.timer.unref?.();
    sessions.set(id, session);
    const ready = { type: "session.ready", sessionId: id, body: { operatorId: state.principalId, deviceId: device.principalId, expiresAt } };
    send(ws, ready);
    send(device.ws, ready);
    void audit.write("session.opened", { sessionId: id, operatorId: state.principalId, deviceId: device.principalId, expiresAt });
  }

  function relay(ws, state, message) {
    const session = sessions.get(message.sessionId);
    if (!session || (session.operator.ws !== ws && session.device.ws !== ws)) return reject(ws, state, "session_invalid", "unknown session");
    if (Date.now() >= session.expiresAt) {
      removeSession(session, "expired");
      return;
    }
    const from = state.role === "operator" ? `operator:${state.principalId}` : `device:${state.principalId}`;
    const target = session.operator.ws === ws ? session.device : session.operator;
    const to = target.role === "operator" ? `operator:${target.principalId}` : `device:${target.principalId}`;
    if (
      !DECIMAL_PATTERN.test(message.seq) ||
      message.seq.length > 20 ||
      !MESSAGE_ID_PATTERN.test(message.messageId ?? "") ||
      !nowInWindow(message.ts, options.authWindowMs) ||
      message.from !== from ||
      message.to !== to
    ) return reject(ws, state, "relay_invalid", "invalid relay metadata");
    let seq;
    try {
      seq = BigInt(message.seq);
    } catch {
      return reject(ws, state, "relay_invalid", "invalid relay sequence");
    }
    const expected = session.seq.get(state.connectionId) + 1n;
    if (seq !== expected) return reject(ws, state, "relay_replay", "sequence is not next", "relay.replay");
    if (session.messageIds.has(message.messageId)) return reject(ws, state, "relay_replay", "messageId reused", "relay.replay");
    if (!message.body || !exactKeys(message.body, ["nonce", "ciphertext", "tag"])) return reject(ws, state, "relay_invalid", "invalid relay body");
    let ciphertextBytes;
    try {
      fromBase64url(message.body.nonce, 12);
      ciphertextBytes = fromBase64url(message.body.ciphertext).length;
      fromBase64url(message.body.tag, 16);
    } catch {
      return reject(ws, state, "relay_invalid", "invalid relay encoding");
    }
    if (ciphertextBytes > options.maxCiphertextBytes) return reject(ws, state, "relay_too_large", "ciphertext exceeds limit");
    session.seq.set(state.connectionId, seq);
    session.messageIds.add(message.messageId);
    send(target.ws, {
      type: "relay.data",
      sessionId: session.id,
      seq: message.seq,
      messageId: message.messageId,
      ts: message.ts,
      from,
      to,
      body: message.body,
    });
    void audit.write("relay.routed", {
      sessionId: session.id,
      from,
      to,
      seq: message.seq,
      messageId: message.messageId,
      ciphertextBytes,
      ciphertextHash: createHash("sha256")
        .update(message.body.nonce, "utf8")
        .update(message.body.ciphertext, "utf8")
        .update(message.body.tag, "utf8")
        .digest("hex"),
    });
  }

  wss.on("connection", (ws, req) => {
    unauthenticatedCount += 1;
    const state = {
      connectionId: randomUUID(),
      phase: "challenge",
      role: null,
      principalId: null,
      keyId: null,
      ip: clientAddress(req),
      lastPong: Date.now(),
      ws,
      countedUnauthenticated: true,
    };
    if (unauthenticatedCount > options.maxUnauthenticated) {
      unauthenticatedCount -= 1;
      state.countedUnauthenticated = false;
      close(ws, 1013, "too many unauthenticated connections");
      return;
    }
    state.authTimer = setTimeout(
      () => reject(ws, state, "auth_timeout", "authentication timed out", "auth.failed"),
      options.authWindowMs,
    );
    state.authTimer.unref?.();
    state.brokerNonce = base64url(randomBytes(32));
    send(ws, { type: "auth.challenge", body: { nonce: state.brokerNonce } });
    ws.on("error", () => {});
    ws.on("close", () => {
      clearTimeout(state.authTimer);
      cleanup(ws, state);
    });
    ws.on("message", (data, isBinary) => {
      if (isBinary) return reject(ws, state, "json_required", "binary frames are not allowed");
      let message;
      try {
        message = JSON.parse(data.toString("utf8"));
        assertObject(message, "message");
      } catch {
        return reject(ws, state, "json_invalid", "invalid JSON");
      }
      if (message.v !== VERSION || typeof message.type !== "string") return reject(ws, state, "version_invalid", "CRC v2 required");
      if (!Object.keys(message).every((key) => ["v", "type", "messageId", "ts", "sessionId", "seq", "from", "to", "body"].includes(key))) {
        return reject(ws, state, "envelope_invalid", "unexpected envelope field");
      }
      if (!MESSAGE_ID_PATTERN.test(message.messageId ?? "") || !Number.isSafeInteger(message.ts)) {
        return reject(ws, state, "envelope_invalid", "messageId and ts required");
      }
      if (state.phase !== "authenticated") {
        if (message.type === "auth.response") return handleAuthResponse(ws, state, message);
        return reject(ws, state, "auth_required", "authenticate first");
      }
      if (message.type === "heartbeat.pong") {
        if (message.body?.nonce === state.heartbeatNonce) {
          state.lastPong = Date.now();
          state.heartbeatNonce = null;
        }
        return;
      }
      if (message.type === "session.open") return openSession(ws, state, message);
      if (message.type === "session.close") {
        const session = sessions.get(message.sessionId);
        if (session && (session.operator.ws === ws || session.device.ws === ws)) removeSession(session, "peer_closed");
        return;
      }
      if (message.type === "relay.data") return relay(ws, state, message);
      reject(ws, state, "type_invalid", "unknown message type");
    });
  });

  httpServer.on("upgrade", (req, socket, head) => {
    const pathname = new URL(req.url, "http://127.0.0.1").pathname;
    if (pathname !== config.listen.path) return socket.destroy();
    if (wss.clients.size >= options.maxConnections) {
      socket.write("HTTP/1.1 503 Service Unavailable\r\nConnection: close\r\n\r\n");
      socket.destroy();
      return;
    }
    wss.handleUpgrade(req, socket, head, (ws) => wss.emit("connection", ws, req));
  });

  const heartbeat = setInterval(() => {
    const now = Date.now();
    for (const state of authenticated) {
      if (now - state.lastPong > options.heartbeatTimeoutMs) {
        auditPeer("peer.heartbeat_timeout", state);
        state.ws.terminate();
      }
      state.heartbeatNonce = base64url(randomBytes(18));
      send(state.ws, { type: "heartbeat.ping", body: { nonce: state.heartbeatNonce } });
    }
  }, options.heartbeatIntervalMs);
  heartbeat.unref?.();

  return {
    httpServer,
    wss,
    audit,
    async listen() {
      await new Promise((resolve, reject) => {
        httpServer.once("error", reject);
        httpServer.listen(config.listen.port, config.listen.host, resolve);
      });
      void audit.write("broker.started", { host: config.listen.host, port: config.listen.port, path: config.listen.path });
      return httpServer.address();
    },
    async close() {
      clearInterval(heartbeat);
      for (const ws of wss.clients) ws.terminate();
      for (const session of [...sessions.values()]) removeSession(session, "broker_stopped");
      await new Promise((resolve) => wss.close(resolve));
      await new Promise((resolve) => httpServer.close(() => resolve()));
      await audit.write("broker.stopped");
      await audit.flush();
    },
  };
}
