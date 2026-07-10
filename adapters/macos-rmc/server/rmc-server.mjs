#!/usr/bin/env node
import crypto from 'node:crypto';
import fs from 'node:fs';
import http from 'node:http';
import https from 'node:https';
import os from 'node:os';
import path from 'node:path';
import process from 'node:process';
import readline from 'node:readline/promises';
import { fileURLToPath } from 'node:url';
import { WebSocketServer } from 'ws';

const DEFAULT_CONFIG = {
  listenHost: '0.0.0.0',
  port: 5002,
  sharedSecret: 'change-this-shared-secret',
  commandTimeoutSeconds: 600,
  tls: {
    enabled: true,
    certificatePath: 'tls/server.crt',
    keyPath: 'tls/server.key',
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
if (argv.http) {
  config.tls.enabled = false;
}

const logger = createLogger(resolveConfigPath(configPath, config.logsDirectory ?? 'logs'));
const clients = new Map();
let selectedClientId = null;

const appServer = createHttpServer(config, configPath, requestHandler);
const wss = new WebSocketServer({ noServer: true, maxPayload: 1024 * 1024 * 16 });

appServer.on('upgrade', (request, socket, head) => {
  const requestUrl = new URL(request.url ?? '/', 'http://localhost');
  if (requestUrl.pathname !== '/link') {
    socket.destroy();
    return;
  }

  wss.handleUpgrade(request, socket, head, ws => {
    void authenticateClient(ws, request).catch(error => {
      logger.error(`client authentication failed: ${error.stack ?? error}`);
      try {
        ws.close(1008, 'Authentication failed');
      } catch {
      }
    });
  });
});

const listenHost = config.listenHost === '*' || config.listenHost === '+' ? '0.0.0.0' : config.listenHost;
appServer.listen(config.port, listenHost, () => {
  const scheme = config.tls.enabled ? 'wss' : 'ws';
  console.log(`RMC-MLCCS server listening on ${scheme}://${config.listenHost}:${config.port}/link`);
  console.log(`Config: ${configPath}`);
  console.log('Commands: help | clients | use <clientId> | run <cmd> | root <cmd> | tool <name> [args...] | exit');
  if (!argv.headless) {
    void interactiveShell();
  }
});

process.on('SIGINT', () => shutdown('SIGINT'));
process.on('SIGTERM', () => shutdown('SIGTERM'));

function parseArgs(args) {
  const parsed = {};
  for (let index = 0; index < args.length; index += 1) {
    const arg = args[index];
    if (arg === '--config') {
      parsed.config = args[++index];
    } else if (arg === '--http') {
      parsed.http = true;
    } else if (arg === '--headless') {
      parsed.headless = true;
    } else if (arg === '--init') {
      parsed.init = true;
    } else if (arg === '--help' || arg === '-h') {
      parsed.help = true;
    } else {
      throw new Error(`Unknown argument: ${arg}`);
    }
  }
  return parsed;
}

function printUsage() {
  console.log(`Usage: rmc-server [--config path] [--http] [--headless] [--init]

Commands:
  clients                 List connected Macs
  use <clientId>          Select a client
  run <command>           Run a shell command as the logged-in user
  root <command>          Run a shell command through the client's sudo channel
  tool <name> [args...]   Run a built-in client tool
  json <payload>          Send a raw command payload JSON object
  exit                    Stop the server
`);
}

function initConfig(targetPath) {
  if (fs.existsSync(targetPath)) {
    console.log(`Config already exists: ${targetPath}`);
    return;
  }

  const created = {
    ...DEFAULT_CONFIG,
    sharedSecret: crypto.randomBytes(48).toString('base64url'),
  };
  fs.mkdirSync(path.dirname(targetPath), { recursive: true });
  fs.writeFileSync(targetPath, `${JSON.stringify(created, null, 2)}\n`, { mode: 0o600 });
  console.log(`Created server config: ${targetPath}`);
}

function loadConfig(targetPath) {
  if (!fs.existsSync(targetPath)) {
    throw new Error(`Config not found: ${targetPath}. Run with --init or use scripts/generate-private-config.sh.`);
  }

  const loaded = JSON.parse(fs.readFileSync(targetPath, 'utf8'));
  const merged = {
    ...DEFAULT_CONFIG,
    ...loaded,
    tls: {
      ...DEFAULT_CONFIG.tls,
      ...(loaded.tls ?? {}),
    },
  };

  if (!merged.sharedSecret || merged.sharedSecret === 'change-this-shared-secret') {
    throw new Error('Refusing to start with a placeholder sharedSecret.');
  }
  if (!Number.isInteger(merged.port) || merged.port <= 0) {
    throw new Error('Invalid port in server config.');
  }
  return merged;
}

function resolveConfigPath(baseConfigPath, configuredPath) {
  if (!configuredPath) {
    return configuredPath;
  }
  return path.isAbsolute(configuredPath)
    ? configuredPath
    : path.join(path.dirname(baseConfigPath), configuredPath);
}

function createHttpServer(serverConfig, baseConfigPath, handler) {
  if (!serverConfig.tls.enabled) {
    return http.createServer(handler);
  }

  const certificatePath = resolveConfigPath(baseConfigPath, serverConfig.tls.certificatePath);
  const keyPath = resolveConfigPath(baseConfigPath, serverConfig.tls.keyPath);
  return https.createServer({
    cert: fs.readFileSync(certificatePath),
    key: fs.readFileSync(keyPath),
  }, handler);
}

function createLogger(logsDirectory) {
  fs.mkdirSync(logsDirectory, { recursive: true });
  const logPath = path.join(logsDirectory, 'server.log');
  const write = (level, message) => {
    const line = `${new Date().toISOString()} [${level}] ${message}\n`;
    fs.appendFile(logPath, line, () => {});
  };
  return {
    info: message => write('INFO', message),
    error: message => write('ERROR', message),
  };
}

async function requestHandler(request, response) {
  try {
    const requestUrl = new URL(request.url ?? '/', 'http://localhost');
    if (requestUrl.pathname === '/operator/clients' && request.method === 'GET') {
      if (!isLoopback(request.socket.remoteAddress)) {
        return json(response, 403, { error: 'loopback only' });
      }

      return json(response, 200, listClients());
    }

    if (requestUrl.pathname === '/operator/execute' && request.method === 'POST') {
      if (!isLoopback(request.socket.remoteAddress)) {
        return json(response, 403, { error: 'loopback only' });
      }

      const body = await readJsonBody(request);
      const client = clients.get(body.clientId);
      if (!client) {
        return json(response, 404, { error: `client not found: ${body.clientId}` });
      }

      const payload = normalizeOperatorPayload(body);
      const result = await client.execute(payload, { printOutput: false });
      return json(response, 200, result);
    }

    if (requestUrl.pathname === '/health') {
      return json(response, 200, { ok: true, clients: clients.size, host: os.hostname() });
    }

    return json(response, 404, { error: 'not found' });
  } catch (error) {
    logger.error(`operator request failed: ${error.stack ?? error}`);
    return json(response, 500, { error: String(error.message ?? error) });
  }
}

function normalizeOperatorPayload(body) {
  const timeoutSeconds = positiveInt(body.timeoutSeconds) ?? config.commandTimeoutSeconds;
  if (body.toolName) {
    return {
      kind: 'tool',
      toolName: String(body.toolName),
      toolArgs: Array.isArray(body.toolArgs) ? body.toolArgs.map(String) : [],
      timeoutSeconds,
    };
  }

  if (!body.command) {
    throw new Error('operator payload requires command or toolName');
  }

  return {
    kind: 'shell',
    command: String(body.command),
    runAsRoot: Boolean(body.runAsRoot),
    timeoutSeconds,
  };
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
      try {
        resolve(JSON.parse(body || '{}'));
      } catch (error) {
        reject(error);
      }
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

function isLoopback(address) {
  return address === '127.0.0.1'
    || address === '::1'
    || address === '::ffff:127.0.0.1'
    || address === undefined;
}

async function authenticateClient(ws, request) {
  const nonce = crypto.randomBytes(32).toString('base64url');
  send(ws, envelope('challenge', null, { nonce }));

  const authEnvelope = await receiveFirst(ws, 10000);
  if (authEnvelope.type !== 'auth') {
    throw new Error('client did not send auth');
  }

  const auth = authEnvelope.payload ?? {};
  const clientId = String(auth.clientId ?? '').trim();
  if (!clientId) {
    throw new Error('clientId missing');
  }

  const expected = createAuthResponse(config.sharedSecret, nonce, clientId);
  const actual = String(auth.response ?? '');
  if (!constantTimeEqual(expected, actual)) {
    throw new Error(`bad auth for ${clientId}`);
  }

  const client = new ConnectedClient(ws, {
    clientId,
    userName: String(auth.userName ?? ''),
    machineName: String(auth.machineName ?? ''),
    platform: String(auth.platform ?? 'macOS'),
    appVersion: String(auth.appVersion ?? ''),
    remoteAddress: request.socket.remoteAddress ?? '',
  });

  if (clients.has(clientId)) {
    clients.get(clientId).close('replaced by a new connection');
  }

  clients.set(clientId, client);
  logger.info(`client connected: ${clientId} ${client.userName}@${client.machineName} ${client.remoteAddress}`);
  send(ws, envelope('hello', null, { serverTime: new Date().toISOString() }));

  ws.on('message', data => client.handleMessage(data));
  ws.on('close', () => removeClient(clientId));
  ws.on('error', error => {
    logger.error(`websocket error for ${clientId}: ${error.stack ?? error}`);
    removeClient(clientId);
  });
}

function receiveFirst(ws, timeoutMs) {
  return new Promise((resolve, reject) => {
    const timer = setTimeout(() => {
      cleanup();
      reject(new Error('timed out waiting for auth'));
    }, timeoutMs);
    const onMessage = data => {
      cleanup();
      try {
        resolve(parseEnvelope(data));
      } catch (error) {
        reject(error);
      }
    };
    const onClose = () => {
      cleanup();
      reject(new Error('socket closed'));
    };
    const cleanup = () => {
      clearTimeout(timer);
      ws.off('message', onMessage);
      ws.off('close', onClose);
    };
    ws.once('message', onMessage);
    ws.once('close', onClose);
  });
}

class ConnectedClient {
  constructor(ws, metadata) {
    this.ws = ws;
    this.clientId = metadata.clientId;
    this.userName = metadata.userName;
    this.machineName = metadata.machineName;
    this.platform = metadata.platform;
    this.appVersion = metadata.appVersion;
    this.remoteAddress = metadata.remoteAddress;
    this.connectedAt = new Date();
    this.pending = new Map();
  }

  close(reason) {
    try {
      this.ws.close(1000, reason);
    } catch {
    }
  }

  execute(payload, options = {}) {
    const timeoutSeconds = positiveInt(payload.timeoutSeconds) ?? config.commandTimeoutSeconds;
    const requestId = crypto.randomUUID();
    const pending = {
      stdout: '',
      stderr: '',
      printOutput: options.printOutput ?? true,
    };

    const promise = new Promise((resolve, reject) => {
      pending.resolve = resolve;
      pending.reject = reject;
      pending.timer = setTimeout(() => {
        this.pending.delete(requestId);
        reject(new Error(`command timed out after ${timeoutSeconds}s`));
      }, (timeoutSeconds + 20) * 1000);
    });

    this.pending.set(requestId, pending);
    send(this.ws, envelope('command', requestId, payload));
    logger.info(`sent command ${requestId} to ${this.clientId}: ${JSON.stringify(payload)}`);
    return promise;
  }

  handleMessage(data) {
    let message;
    try {
      message = parseEnvelope(data);
    } catch (error) {
      logger.error(`invalid message from ${this.clientId}: ${error.stack ?? error}`);
      return;
    }

    if (message.type === 'output') {
      const pending = this.pending.get(message.requestId);
      const payload = message.payload ?? {};
      const stream = payload.stream === 'stderr' ? 'stderr' : 'stdout';
      const text = String(payload.text ?? '');
      if (pending) {
        pending[stream] += text;
        if (pending.printOutput) {
          process.stdout.write(stream === 'stderr' ? `[stderr] ${text}` : text);
        }
      }
      return;
    }

    if (message.type === 'complete') {
      const pending = this.pending.get(message.requestId);
      if (!pending) {
        return;
      }

      clearTimeout(pending.timer);
      this.pending.delete(message.requestId);
      pending.resolve({
        complete: message.payload ?? {},
        stdout: pending.stdout,
        stderr: pending.stderr,
      });
      return;
    }

    if (message.type === 'disconnect') {
      removeClient(this.clientId);
    }
  }
}

function parseEnvelope(data) {
  const text = Buffer.isBuffer(data) ? data.toString('utf8') : String(data);
  const parsed = JSON.parse(text);
  if (!parsed || typeof parsed.type !== 'string') {
    throw new Error('invalid envelope');
  }
  return parsed;
}

function envelope(type, requestId = null, payload = null) {
  return { type, requestId, payload };
}

function send(ws, value) {
  ws.send(JSON.stringify(value));
}

function createAuthResponse(secret, nonce, clientId) {
  return crypto
    .createHmac('sha256', Buffer.from(secret, 'utf8'))
    .update(`${nonce}:${clientId}`, 'utf8')
    .digest('hex');
}

function constantTimeEqual(left, right) {
  const leftBuffer = Buffer.from(left);
  const rightBuffer = Buffer.from(right);
  return leftBuffer.length === rightBuffer.length && crypto.timingSafeEqual(leftBuffer, rightBuffer);
}

function removeClient(clientId) {
  const client = clients.get(clientId);
  if (!client) {
    return;
  }

  for (const pending of client.pending.values()) {
    clearTimeout(pending.timer);
    pending.reject(new Error('client disconnected'));
  }
  client.pending.clear();
  clients.delete(clientId);
  if (selectedClientId === clientId) {
    selectedClientId = null;
  }
  logger.info(`client disconnected: ${clientId}`);
}

function listClients() {
  return [...clients.values()]
    .sort((left, right) => left.clientId.localeCompare(right.clientId))
    .map(client => ({
      clientId: client.clientId,
      userName: client.userName,
      machineName: client.machineName,
      platform: client.platform,
      appVersion: client.appVersion,
      connectedAt: client.connectedAt.toISOString(),
      remoteAddress: client.remoteAddress,
    }));
}

async function interactiveShell() {
  const rl = readline.createInterface({ input: process.stdin, output: process.stdout });
  while (true) {
    const prompt = selectedClientId ? `${selectedClientId}> ` : 'server> ';
    const line = (await rl.question(prompt)).trim();
    if (!line) {
      continue;
    }

    try {
      const shouldExit = await handleCliLine(line);
      if (shouldExit) {
        rl.close();
        shutdown('operator exit');
        return;
      }
    } catch (error) {
      console.error(`[error] ${error.message ?? error}`);
      logger.error(`cli command failed: ${error.stack ?? error}`);
    }
  }
}

async function handleCliLine(line) {
  if (line === 'exit' || line === 'quit') {
    return true;
  }

  if (line === 'help') {
    printUsage();
    return false;
  }

  if (line === 'clients') {
    const rows = listClients();
    if (rows.length === 0) {
      console.log('No clients connected.');
      return false;
    }
    for (const client of rows) {
      console.log(`${client.clientId}\t${client.userName}@${client.machineName}\t${client.platform}\tconnected=${client.connectedAt}`);
    }
    return false;
  }

  if (line.startsWith('use ')) {
    const clientId = line.slice(4).trim();
    if (!clients.has(clientId)) {
      console.log(`Client not found: ${clientId}`);
      return false;
    }
    selectedClientId = clientId;
    console.log(`Selected ${clientId}`);
    return false;
  }

  const client = selectedClient();

  if (line.startsWith('json ')) {
    const payload = JSON.parse(line.slice(5));
    await executeCli(client, payload);
    return false;
  }

  if (line.startsWith('tool ')) {
    const args = splitArgs(line.slice(5));
    if (args.length === 0) {
      throw new Error('Usage: tool <name> [args...]');
    }
    await executeCli(client, {
      kind: 'tool',
      toolName: args[0],
      toolArgs: args.slice(1),
      timeoutSeconds: config.commandTimeoutSeconds,
    });
    return false;
  }

  if (line.startsWith('root ')) {
    await executeCli(client, {
      kind: 'shell',
      command: line.slice(5),
      runAsRoot: true,
      timeoutSeconds: config.commandTimeoutSeconds,
    });
    return false;
  }

  if (line.startsWith('run ')) {
    await executeCli(client, {
      kind: 'shell',
      command: line.slice(4),
      runAsRoot: false,
      timeoutSeconds: config.commandTimeoutSeconds,
    });
    return false;
  }

  if (line.startsWith('sudo ')) {
    await executeCli(client, {
      kind: 'shell',
      command: line.slice(5),
      runAsRoot: true,
      timeoutSeconds: config.commandTimeoutSeconds,
    });
    return false;
  }

  await executeCli(client, {
    kind: 'shell',
    command: line,
    runAsRoot: false,
    timeoutSeconds: config.commandTimeoutSeconds,
  });
  return false;
}

function selectedClient() {
  if (!selectedClientId) {
    throw new Error('Select a client first: use <clientId>');
  }
  const client = clients.get(selectedClientId);
  if (!client) {
    selectedClientId = null;
    throw new Error('Selected client disconnected.');
  }
  return client;
}

async function executeCli(client, payload) {
  const result = await client.execute(payload, { printOutput: true });
  const complete = result.complete ?? {};
  console.log(`[complete] exit=${complete.exitCode ?? 'unknown'} cancelled=${Boolean(complete.cancelled)} durationMs=${complete.durationMs ?? 0}`);
}

function splitArgs(input) {
  const args = [];
  let current = '';
  let quote = null;
  let escaped = false;
  for (const char of input) {
    if (escaped) {
      current += char;
      escaped = false;
      continue;
    }
    if (char === '\\') {
      escaped = true;
      continue;
    }
    if (quote) {
      if (char === quote) {
        quote = null;
      } else {
        current += char;
      }
      continue;
    }
    if (char === '"' || char === "'") {
      quote = char;
      continue;
    }
    if (/\s/.test(char)) {
      if (current) {
        args.push(current);
        current = '';
      }
      continue;
    }
    current += char;
  }
  if (quote) {
    throw new Error('unterminated quote');
  }
  if (escaped) {
    current += '\\';
  }
  if (current) {
    args.push(current);
  }
  return args;
}

function positiveInt(value) {
  const parsed = Number(value);
  return Number.isInteger(parsed) && parsed > 0 ? parsed : null;
}

function shutdown(reason) {
  console.log(`Stopping RMC-MLCCS server (${reason})...`);
  for (const client of clients.values()) {
    client.close('server shutting down');
  }
  wss.close();
  appServer.close(() => process.exit(0));
  setTimeout(() => process.exit(0), 3000).unref();
}

const mainPath = fileURLToPath(import.meta.url);
if (process.argv[1] === mainPath) {
  // Work is started by the top-level server setup above.
}
