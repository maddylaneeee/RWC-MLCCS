#!/usr/bin/env node
import crypto from 'node:crypto';
import fs from 'node:fs';
import http from 'node:http';
import os from 'node:os';
import path from 'node:path';
import process from 'node:process';
import readline from 'node:readline/promises';
import { WebSocket } from 'ws';
import {
  ReplayGuard,
  authProof,
  decodeKey,
  decryptRelay,
  deriveSessionKey,
  encryptRelay,
  makeEnvelope,
  validateBaseEnvelope,
} from './protocol-v2.mjs';

const DEFAULT_CONFIG = {
  brokerUrl: 'wss://lixinchen.ca/crc/v2/ws',
  operatorId: '',
  keyId: '',
  brokerAuthKey: '',
  devices: {},
  commandTimeoutSeconds: 600,
  reconnect: { initialSeconds: 1, maxSeconds: 60 },
  localApi: {
    host: '127.0.0.1',
    port: 5002,
    tokenFile: 'operator.token',
  },
  logsDirectory: 'logs',
};

const argv = parseArgs(process.argv.slice(2));
const configPath = path.resolve(argv.config ?? path.join(process.cwd(), 'server.private.json'));
if (argv.help) {
  printUsage();
  process.exit(0);
}
if (argv.init) {
  initConfig(configPath);
  process.exit(0);
}

const config = loadConfig(configPath);
const logger = createLogger(resolveConfigPath(configPath, config.logsDirectory));
const bearerToken = loadOrCreateToken(resolveConfigPath(configPath, config.localApi.tokenFile));
const broker = new BrokerOperator(config, logger);
let selectedDeviceId = null;
let shuttingDown = false;

const apiServer = http.createServer(requestHandler);
apiServer.listen(config.localApi.port, config.localApi.host, () => {
  console.log(`RMC operator API: http://${config.localApi.host}:${config.localApi.port}`);
  console.log(`Bearer token file (0600): ${resolveConfigPath(configPath, config.localApi.tokenFile)}`);
  console.log(`Broker: ${config.brokerUrl}`);
  if (!argv.headless) {
    void interactiveShell();
  }
});
void broker.run();

process.on('SIGINT', () => shutdown('SIGINT'));
process.on('SIGTERM', () => shutdown('SIGTERM'));

function parseArgs(args) {
  const parsed = {};
  for (let index = 0; index < args.length; index += 1) {
    const arg = args[index];
    if (arg === '--config') parsed.config = args[++index];
    else if (arg === '--headless') parsed.headless = true;
    else if (arg === '--init') parsed.init = true;
    else if (arg === '--help' || arg === '-h') parsed.help = true;
    else throw new Error(`Unknown argument: ${arg}`);
  }
  return parsed;
}

function printUsage() {
  console.log(`Usage: rmc-server [--config path] [--headless] [--init]

This process is an outbound-only CRC v2 operator. It never accepts public connections.
Commands:
  clients | use <deviceId> | run <cmd> | root <cmd> | tool <name> [args...] | exit
`);
}

function initConfig(targetPath) {
  if (fs.existsSync(targetPath)) {
    console.log(`Config already exists: ${targetPath}`);
    return;
  }
  const operatorId = `operator-${os.hostname().toLowerCase().replace(/[^a-z0-9.-]/g, '-')}`;
  const created = {
    ...DEFAULT_CONFIG,
    operatorId,
    keyId: `${operatorId}-2026-01`,
    brokerAuthKey: crypto.randomBytes(32).toString('base64url'),
    devices: {
      'replace-with-device-id': { e2eeKey: crypto.randomBytes(32).toString('base64url') },
    },
  };
  fs.mkdirSync(path.dirname(targetPath), { recursive: true });
  fs.writeFileSync(targetPath, `${JSON.stringify(created, null, 2)}\n`, { mode: 0o600 });
  fs.chmodSync(targetPath, 0o600);
  console.log(`Created operator config: ${targetPath}`);
}

function loadConfig(targetPath) {
  if (!fs.existsSync(targetPath)) {
    throw new Error(`Config not found: ${targetPath}. Run with --init.`);
  }
  const loaded = JSON.parse(fs.readFileSync(targetPath, 'utf8'));
  const merged = {
    ...DEFAULT_CONFIG,
    ...loaded,
    reconnect: { ...DEFAULT_CONFIG.reconnect, ...(loaded.reconnect ?? {}) },
    localApi: { ...DEFAULT_CONFIG.localApi, ...(loaded.localApi ?? {}) },
  };
  if (!merged.brokerUrl.startsWith('wss://')) throw new Error('brokerUrl must use wss://');
  if (!merged.operatorId || !merged.keyId) throw new Error('operatorId and keyId are required');
  decodeKey(merged.brokerAuthKey, 'brokerAuthKey');
  for (const [deviceId, entry] of Object.entries(merged.devices ?? {})) {
    if (!deviceId || !entry?.e2eeKey) throw new Error(`invalid device entry: ${deviceId}`);
    decodeKey(entry.e2eeKey, `devices.${deviceId}.e2eeKey`);
  }
  if (!isLoopbackHost(merged.localApi.host)) {
    throw new Error('localApi.host must be a loopback address');
  }
  return merged;
}

function resolveConfigPath(base, configured) {
  return path.isAbsolute(configured) ? configured : path.join(path.dirname(base), configured);
}

function loadOrCreateToken(tokenPath) {
  fs.mkdirSync(path.dirname(tokenPath), { recursive: true });
  if (!fs.existsSync(tokenPath)) {
    fs.writeFileSync(tokenPath, `${crypto.randomBytes(32).toString('base64url')}\n`, {
      mode: 0o600,
      flag: 'wx',
    });
  }
  fs.chmodSync(tokenPath, 0o600);
  const token = fs.readFileSync(tokenPath, 'utf8').trim();
  if (token.length < 32) throw new Error('operator bearer token is too short');
  return token;
}

function createLogger(logsDirectory) {
  fs.mkdirSync(logsDirectory, { recursive: true });
  const logPath = path.join(logsDirectory, 'operator.log');
  const write = (level, event, fields = {}) => {
    const safe = {};
    for (const key of ['deviceId', 'sessionId', 'requestId', 'messageId', 'type', 'result', 'error']) {
      if (fields[key] !== undefined) safe[key] = String(fields[key]);
    }
    fs.appendFile(logPath, `${JSON.stringify({ ts: new Date().toISOString(), level, event, ...safe })}\n`, () => {});
  };
  return {
    info: (event, fields) => write('INFO', event, fields),
    error: (event, fields) => write('ERROR', event, fields),
  };
}

class BrokerOperator {
  constructor(operatorConfig, auditLogger) {
    this.config = operatorConfig;
    this.logger = auditLogger;
    this.ws = null;
    this.authenticated = false;
    this.stopped = false;
    this.replay = new ReplayGuard();
    this.presence = new Map();
    this.sessions = new Map();
    this.pendingSession = new Map();
    this.pendingCommands = new Map();
    this.authWaiter = null;
  }

  async run() {
    let attempt = 0;
    while (!this.stopped) {
      try {
        await this.connectOnce();
        attempt = 0;
      } catch (error) {
        if (!this.stopped) {
          this.logger.error('broker_connection_failed', { error: error.message });
          console.error(`[broker] ${error.message}`);
        }
      }
      this.resetConnection(new Error('broker disconnected'));
      if (this.stopped) break;
      const base = Math.min(
        this.config.reconnect.maxSeconds,
        this.config.reconnect.initialSeconds * (2 ** Math.min(attempt, 10)),
      );
      attempt += 1;
      const jittered = base * (0.75 + Math.random() * 0.5);
      await delay(jittered * 1000);
    }
  }

  connectOnce() {
    return new Promise((resolve, reject) => {
      const ws = new WebSocket(this.config.brokerUrl, {
        rejectUnauthorized: true,
        handshakeTimeout: 15_000,
        maxPayload: 16 * 1024 * 1024,
      });
      this.ws = ws;
      let settled = false;
      ws.on('open', () => this.logger.info('broker_socket_open'));
      ws.on('message', data => {
        try {
          this.handleMessage(JSON.parse(data.toString('utf8')));
        } catch (error) {
          this.logger.error('broker_message_rejected', { error: error.message });
          ws.close(1008, 'invalid message');
        }
      });
      ws.on('close', () => {
        if (!settled) {
          settled = true;
          resolve();
        }
      });
      ws.on('error', error => {
        if (!settled && ws.readyState !== WebSocket.OPEN) {
          settled = true;
          reject(error);
        }
      });
    });
  }

  handleMessage(message) {
    this.replay.acceptMessage(message);
    if (message.type === 'auth.challenge') {
      if (this.authenticated) throw new Error('unexpected repeated auth challenge');
      const brokerNonce = String(message.body?.nonce ?? '');
      if (!brokerNonce) throw new Error('auth challenge nonce missing');
      const clientNonce = crypto.randomBytes(32).toString('base64url');
      const ts = Date.now();
      this.send(makeEnvelope('auth.response', {
        role: 'operator',
        principalId: this.config.operatorId,
        keyId: this.config.keyId,
        clientNonce,
        proof: authProof(
          decodeKey(this.config.brokerAuthKey, 'brokerAuthKey'),
          'operator',
          this.config.operatorId,
          this.config.keyId,
          brokerNonce,
          clientNonce,
          ts,
        ),
        metadata: { adapter: 'macos-rmc', host: os.hostname() },
      }, { ts }));
      return;
    }
    if (message.type === 'auth.ok') {
      this.authenticated = true;
      this.logger.info('broker_authenticated');
      return;
    }
    if (!this.authenticated) throw new Error('message received before auth.ok');
    if (message.type === 'heartbeat.ping') {
      this.send(makeEnvelope('heartbeat.pong', { nonce: message.body?.nonce }));
      return;
    }
    if (message.type === 'heartbeat.pong') return;
    if (message.type === 'presence.snapshot') {
      this.presence.clear();
      for (const device of message.body?.devices ?? []) {
        if (this.config.devices[device.deviceId]) this.presence.set(device.deviceId, device);
      }
      return;
    }
    if (message.type === 'session.ready') {
      const sessionId = String(message.sessionId ?? '');
      const deviceId = String(message.body?.deviceId ?? '');
      const operatorId = String(message.body?.operatorId ?? '');
      const expiresAt = Number(message.body?.expiresAt);
      if (!sessionId || operatorId !== this.config.operatorId || !this.config.devices[deviceId]) {
        throw new Error('invalid session.ready');
      }
      if (!Number.isSafeInteger(expiresAt) || expiresAt <= Date.now()) {
        throw new Error('expired session.ready');
      }
      const session = {
        sessionId,
        deviceId,
        operatorId,
        expiresAt,
        key: deriveSessionKey(
          decodeKey(this.config.devices[deviceId].e2eeKey, `devices.${deviceId}.e2eeKey`),
          sessionId,
          operatorId,
          deviceId,
        ),
        sendSeq: 0n,
        receiveSeq: 0n,
      };
      this.sessions.set(sessionId, session);
      const waiter = this.pendingSession.get(deviceId);
      if (waiter) {
        clearTimeout(waiter.timer);
        this.pendingSession.delete(deviceId);
        waiter.resolve(session);
      }
      this.logger.info('session_ready', { deviceId, sessionId });
      return;
    }
    if (message.type === 'relay.data') {
      this.handleRelay(message);
      return;
    }
    if (message.type === 'session.closed') {
      this.closeSession(String(message.sessionId ?? ''), new Error('session closed by broker'));
    }
  }

  send(message) {
    if (this.ws?.readyState !== WebSocket.OPEN) throw new Error('broker is not connected');
    this.ws.send(JSON.stringify(message));
  }

  async openSession(deviceId) {
    if (!this.authenticated) throw new Error('operator is not authenticated to broker');
    if (!this.config.devices[deviceId]) throw new Error(`device is not configured: ${deviceId}`);
    const existing = [...this.sessions.values()].find(value => value.deviceId === deviceId);
    if (existing) return existing;
    if (this.pendingSession.has(deviceId)) return this.pendingSession.get(deviceId).promise;
    let resolvePromise;
    let rejectPromise;
    const promise = new Promise((resolve, reject) => {
      resolvePromise = resolve;
      rejectPromise = reject;
    });
    const timer = setTimeout(() => {
      this.pendingSession.delete(deviceId);
      rejectPromise(new Error('timed out opening session'));
    }, 20_000);
    this.pendingSession.set(deviceId, { promise, resolve: resolvePromise, reject: rejectPromise, timer });
    this.send(makeEnvelope('session.open', { deviceId }));
    return promise;
  }

  async execute(deviceId, commandBody, { printOutput = false } = {}) {
    const session = await this.openSession(deviceId);
    if (session.expiresAt <= Date.now()) {
      this.closeSession(session.sessionId, new Error('session authorization expired'));
      throw new Error('session authorization expired');
    }
    const requestId = crypto.randomUUID();
    const timeoutSeconds = positiveInt(commandBody.timeoutSeconds) ?? this.config.commandTimeoutSeconds;
    const inner = {
      type: 'command.execute',
      requestId,
      issuedAt: Date.now(),
      expiresAt: Date.now() + timeoutSeconds * 1000,
      body: commandBody,
    };
    const pending = { stdout: '', stderr: '', printOutput, sessionId: session.sessionId };
    const promise = new Promise((resolve, reject) => {
      pending.resolve = resolve;
      pending.reject = reject;
      pending.timer = setTimeout(() => {
        this.pendingCommands.delete(requestId);
        reject(new Error(`command timed out after ${timeoutSeconds}s`));
      }, (timeoutSeconds + 20) * 1000);
    });
    this.pendingCommands.set(requestId, pending);
    this.sendEncrypted(session, inner);
    this.logger.info('command_sent', { deviceId, sessionId: session.sessionId, requestId });
    return promise;
  }

  sendEncrypted(session, inner) {
    session.sendSeq += 1n;
    this.send(encryptRelay(inner, {
      key: session.key,
      sessionId: session.sessionId,
      from: `operator:${this.config.operatorId}`,
      to: `device:${session.deviceId}`,
      seq: session.sendSeq.toString(),
    }));
  }

  handleRelay(message) {
    const session = this.sessions.get(String(message.sessionId ?? ''));
    if (!session) throw new Error('relay references unknown session');
    if (message.from !== `device:${session.deviceId}` || message.to !== `operator:${this.config.operatorId}`) {
      throw new Error('relay endpoint mismatch');
    }
    const seq = BigInt(message.seq);
    if (seq !== session.receiveSeq + 1n) throw new Error('non-contiguous relay sequence');
    const inner = decryptRelay(message, session.key);
    session.receiveSeq = seq;
    if (!inner || typeof inner.requestId !== 'string'
        || !Number.isSafeInteger(inner.issuedAt)
        || !Number.isSafeInteger(inner.expiresAt)
        || inner.issuedAt > Date.now() + 60_000
        || inner.expiresAt < Date.now()) {
      throw new Error('invalid or expired encrypted message');
    }
    const pending = this.pendingCommands.get(inner.requestId);
    if (!pending) return;
    if (inner.type === 'command.output') {
      const stream = inner.body?.stream === 'stderr' ? 'stderr' : 'stdout';
      const text = String(inner.body?.text ?? '');
      pending[stream] += text;
      if (pending.printOutput) process.stdout.write(stream === 'stderr' ? `[stderr] ${text}` : text);
      return;
    }
    if (inner.type === 'command.complete') {
      clearTimeout(pending.timer);
      this.pendingCommands.delete(inner.requestId);
      pending.resolve({ complete: inner.body ?? {}, stdout: pending.stdout, stderr: pending.stderr });
      this.logger.info('command_completed', {
        deviceId: session.deviceId,
        sessionId: session.sessionId,
        requestId: inner.requestId,
        result: inner.body?.exitCode,
      });
    }
  }

  closeSession(sessionId, error) {
    const session = this.sessions.get(sessionId);
    if (!session) return;
    this.sessions.delete(sessionId);
    this.replay.closeSession(sessionId);
    this.logger.info('session_closed', { deviceId: session.deviceId, sessionId });
    for (const [requestId, pending] of this.pendingCommands) {
      if (pending.sessionId !== sessionId) continue;
      clearTimeout(pending.timer);
      pending.reject(error);
      this.pendingCommands.delete(requestId);
    }
  }

  resetConnection(error) {
    this.authenticated = false;
    for (const pending of this.pendingSession.values()) {
      clearTimeout(pending.timer);
      pending.reject(error);
    }
    this.pendingSession.clear();
    for (const pending of this.pendingCommands.values()) {
      clearTimeout(pending.timer);
      pending.reject(error);
    }
    this.pendingCommands.clear();
    this.sessions.clear();
    this.presence.clear();
    this.replay = new ReplayGuard();
  }

  stop() {
    this.stopped = true;
    this.ws?.close(1000, 'operator shutdown');
  }

  listDevices() {
    return Object.keys(this.config.devices).sort().map(deviceId => {
      const present = this.presence.get(deviceId);
      const session = [...this.sessions.values()].find(value => value.deviceId === deviceId);
      return {
        deviceId,
        connected: Boolean(present),
        connectedAt: present?.connectedAt ?? null,
        metadata: present?.metadata ?? null,
        sessionId: session?.sessionId ?? null,
      };
    });
  }
}

async function requestHandler(request, response) {
  try {
    if (!isLoopbackAddress(request.socket.remoteAddress)) return json(response, 403, { error: 'loopback only' });
    if (!authorized(request)) return json(response, 401, { error: 'bearer token required' });
    const requestUrl = new URL(request.url ?? '/', 'http://localhost');
    if (requestUrl.pathname === '/health' && request.method === 'GET') {
      return json(response, 200, { ok: true, brokerAuthenticated: broker.authenticated });
    }
    if (requestUrl.pathname === '/operator/clients' && request.method === 'GET') {
      return json(response, 200, broker.listDevices());
    }
    if (requestUrl.pathname === '/operator/execute' && request.method === 'POST') {
      const body = await readJsonBody(request);
      const result = await broker.execute(String(body.clientId ?? body.deviceId ?? ''), normalizeOperatorPayload(body));
      return json(response, 200, result);
    }
    return json(response, 404, { error: 'not found' });
  } catch (error) {
    logger.error('operator_request_failed', { error: error.message });
    return json(response, 500, { error: String(error.message ?? error) });
  }
}

function authorized(request) {
  const supplied = String(request.headers.authorization ?? '').replace(/^Bearer\s+/i, '');
  const left = Buffer.from(supplied);
  const right = Buffer.from(bearerToken);
  return left.length === right.length && crypto.timingSafeEqual(left, right);
}

function normalizeOperatorPayload(body) {
  const timeoutSeconds = positiveInt(body.timeoutSeconds) ?? config.commandTimeoutSeconds;
  if (body.toolName) {
    return { kind: 'tool', toolName: String(body.toolName), toolArgs: Array.isArray(body.toolArgs) ? body.toolArgs.map(String) : [], timeoutSeconds };
  }
  if (!body.command) throw new Error('operator payload requires command or toolName');
  return { kind: 'shell', command: String(body.command), runAsRoot: Boolean(body.runAsRoot), timeoutSeconds };
}

async function interactiveShell() {
  const rl = readline.createInterface({ input: process.stdin, output: process.stdout });
  while (!shuttingDown) {
    const line = (await rl.question(selectedDeviceId ? `${selectedDeviceId}> ` : 'operator> ')).trim();
    if (!line) continue;
    try {
      if (await handleCliLine(line)) {
        rl.close();
        shutdown('operator exit');
        return;
      }
    } catch (error) {
      console.error(`[error] ${error.message ?? error}`);
      logger.error('cli_request_failed', { error: error.message });
    }
  }
}

async function handleCliLine(line) {
  if (line === 'exit' || line === 'quit') return true;
  if (line === 'help') {
    printUsage();
    return false;
  }
  if (line === 'clients') {
    for (const item of broker.listDevices()) {
      console.log(`${item.deviceId}\tconnected=${item.connected}\tsession=${item.sessionId ?? '-'}`);
    }
    return false;
  }
  if (line.startsWith('use ')) {
    const id = line.slice(4).trim();
    if (!config.devices[id]) throw new Error(`device is not configured: ${id}`);
    selectedDeviceId = id;
    console.log(`Selected ${id}`);
    return false;
  }
  if (!selectedDeviceId) throw new Error('Select a device first: use <deviceId>');
  let payload;
  if (line.startsWith('tool ')) {
    const args = splitArgs(line.slice(5));
    if (!args.length) throw new Error('Usage: tool <name> [args...]');
    payload = { kind: 'tool', toolName: args[0], toolArgs: args.slice(1), timeoutSeconds: config.commandTimeoutSeconds };
  } else if (line.startsWith('root ') || line.startsWith('sudo ')) {
    payload = { kind: 'shell', command: line.slice(line.indexOf(' ') + 1), runAsRoot: true, timeoutSeconds: config.commandTimeoutSeconds };
  } else {
    payload = { kind: 'shell', command: line.startsWith('run ') ? line.slice(4) : line, runAsRoot: false, timeoutSeconds: config.commandTimeoutSeconds };
  }
  const result = await broker.execute(selectedDeviceId, payload, { printOutput: true });
  console.log(`[complete] exit=${result.complete?.exitCode ?? 'unknown'} cancelled=${Boolean(result.complete?.cancelled)} durationMs=${result.complete?.durationMs ?? 0}`);
  return false;
}

function readJsonBody(request) {
  return new Promise((resolve, reject) => {
    let body = '';
    request.setEncoding('utf8');
    request.on('data', chunk => {
      body += chunk;
      if (body.length > 1024 * 1024) {
        reject(new Error('request body too large'));
        request.destroy();
      }
    });
    request.on('end', () => {
      try { resolve(JSON.parse(body || '{}')); } catch (error) { reject(error); }
    });
    request.on('error', reject);
  });
}

function json(response, statusCode, value) {
  const body = `${JSON.stringify(value, null, 2)}\n`;
  response.writeHead(statusCode, {
    'content-type': 'application/json; charset=utf-8',
    'content-length': Buffer.byteLength(body),
  });
  response.end(body);
}

function splitArgs(input) {
  const args = [];
  let current = '';
  let quote = null;
  let escaped = false;
  for (const char of input) {
    if (escaped) { current += char; escaped = false; continue; }
    if (char === '\\') { escaped = true; continue; }
    if (quote) {
      if (char === quote) quote = null;
      else current += char;
      continue;
    }
    if (char === '"' || char === "'") { quote = char; continue; }
    if (/\s/.test(char)) {
      if (current) { args.push(current); current = ''; }
    } else current += char;
  }
  if (quote) throw new Error('unterminated quote');
  if (escaped) current += '\\';
  if (current) args.push(current);
  return args;
}

function positiveInt(value) {
  const parsed = Number(value);
  return Number.isInteger(parsed) && parsed > 0 ? parsed : null;
}

function isLoopbackHost(value) {
  return value === '127.0.0.1' || value === '::1' || value === 'localhost';
}

function isLoopbackAddress(value) {
  return value === '127.0.0.1' || value === '::1' || value === '::ffff:127.0.0.1';
}

function delay(ms) {
  return new Promise(resolve => setTimeout(resolve, ms));
}

function shutdown(reason) {
  if (shuttingDown) return;
  shuttingDown = true;
  console.log(`Stopping RMC operator (${reason})...`);
  broker.stop();
  apiServer.close(() => process.exit(0));
  setTimeout(() => process.exit(0), 3000).unref();
}
