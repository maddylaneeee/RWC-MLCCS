#!/usr/bin/env node
import {
  createCipheriv,
  createHash,
  randomBytes,
  randomUUID,
} from "node:crypto";
import {
  chmod,
  mkdir,
  readFile,
  rename,
  writeFile,
} from "node:fs/promises";
import { basename, join, resolve } from "node:path";
import { pathToFileURL } from "node:url";

const ID_PATTERN = /^[A-Za-z0-9._:@-]{1,128}$/;
const FORMAT = "crc-device-provisioning-v1";
const ALGORITHM = "AES-256-GCM";
const AAD = Buffer.from(FORMAT, "utf8");

function base64url(value) {
  return Buffer.from(value).toString("base64url");
}

function stableJson(value) {
  return `${JSON.stringify(value, null, 2)}\n`;
}

async function writePrivateJson(path, value) {
  const candidate = `${path}.${randomUUID()}.tmp`;
  await writeFile(candidate, stableJson(value), { mode: 0o600 });
  await chmod(candidate, 0o600);
  await rename(candidate, path);
  await chmod(path, 0o600);
}

function parseArgs(argv, required = ["device-id", "broker-config", "operator-config", "out"]) {
  const values = {};
  for (let index = 0; index < argv.length; index += 1) {
    const name = argv[index];
    if (!name.startsWith("--") || index + 1 >= argv.length) {
      throw new Error(`invalid argument: ${name}`);
    }
    values[name.slice(2)] = argv[index + 1];
    index += 1;
  }
  for (const name of required) {
    if (!values[name]) throw new Error(`--${name} is required`);
  }
  return values;
}

function assertCanonicalKey(value, name) {
  if (
    typeof value !== "string" ||
    !/^[A-Za-z0-9_-]+$/.test(value) ||
    Buffer.from(value, "base64url").length !== 32 ||
    Buffer.from(value, "base64url").toString("base64url") !== value
  ) throw new Error(`${name} must be a canonical 32-byte base64url key`);
}

export async function provisionRwcDevice({
  deviceId,
  keyId,
  brokerConfigPath,
  operatorConfigPath,
  outputDirectory,
}) {
  if (!ID_PATTERN.test(deviceId)) throw new Error("deviceId is invalid");
  const effectiveKeyId = keyId || `device-${new Date().toISOString().slice(0, 10).replaceAll("-", "")}-${base64url(randomBytes(5))}`;
  if (!ID_PATTERN.test(effectiveKeyId)) throw new Error("keyId is invalid");

  const brokerPath = resolve(brokerConfigPath);
  const operatorPath = resolve(operatorConfigPath);
  const out = resolve(outputDirectory);
  const [broker, operator] = await Promise.all([
    readFile(brokerPath, "utf8").then(JSON.parse),
    readFile(operatorPath, "utf8").then(JSON.parse),
  ]);
  if (!Array.isArray(broker.auth?.peers)) throw new Error("broker auth.peers is invalid");
  if (!ID_PATTERN.test(operator.operatorId) || !ID_PATTERN.test(operator.keyId)) {
    throw new Error("operator identity is invalid");
  }
  if (
    typeof operator.brokerUrl !== "string" ||
    !operator.brokerUrl.startsWith("wss://") ||
    !operator.brokerUrl.startsWith("wss://lixinchen.ca/")
  ) throw new Error("operator brokerUrl must use the production lixinchen.ca WSS endpoint");
  assertCanonicalKey(operator.brokerAuthKey, "operator brokerAuthKey");
  const brokerOperator = broker.auth.peers.find((peer) =>
    peer.role === "operator" &&
    peer.principalId === operator.operatorId &&
    peer.keyId === operator.keyId);
  if (!brokerOperator || brokerOperator.authKey !== operator.brokerAuthKey ||
      !Array.isArray(brokerOperator.allowedDevices)) {
    throw new Error("operator config does not match the broker registry");
  }
  if (broker.auth.peers.some((peer) => peer.role === "device" && peer.principalId === deviceId)) {
    throw new Error("device already exists in broker config; explicit rotation is required");
  }
  if (operator.devices?.[deviceId]) {
    throw new Error("device already exists in operator config; explicit rotation is required");
  }

  const brokerAuthKey = base64url(randomBytes(32));
  const e2eeKey = base64url(randomBytes(32));
  const contentKey = randomBytes(32);
  const nonce = randomBytes(12);
  const deviceConfig = {
    brokerUrl: operator.brokerUrl,
    deviceId,
    keyId: effectiveKeyId,
    brokerAuthKey,
    e2eeKey,
    allowLocalPowerShellFallback: true,
    reconnectDelaySeconds: 2,
    maxReconnectDelaySeconds: 60,
    commandCancelGraceSeconds: 3,
  };
  const plaintext = Buffer.from(stableJson(deviceConfig), "utf8");
  const cipher = createCipheriv("aes-256-gcm", contentKey, nonce);
  cipher.setAAD(AAD);
  const ciphertext = Buffer.concat([cipher.update(plaintext), cipher.final()]);
  const randomName = `${base64url(randomBytes(18))}.device.private.json`;
  const envelope = {
    format: FORMAT,
    algorithm: ALGORITHM,
    nonce: base64url(nonce),
    ciphertext: base64url(ciphertext),
    tag: base64url(cipher.getAuthTag()),
  };

  const nextBroker = structuredClone(broker);
  nextBroker.auth.peers.push({
    role: "device",
    principalId: deviceId,
    keyId: effectiveKeyId,
    authKey: brokerAuthKey,
  });
  const nextBrokerOperator = nextBroker.auth.peers.find((peer) =>
    peer.role === "operator" &&
    peer.principalId === operator.operatorId &&
    peer.keyId === operator.keyId);
  nextBrokerOperator.allowedDevices = [...new Set([...nextBrokerOperator.allowedDevices, deviceId])];

  const nextOperator = structuredClone(operator);
  nextOperator.devices ??= {};
  nextOperator.devices[deviceId] = { e2eeKey };

  await mkdir(out, { recursive: false, mode: 0o700 });
  await chmod(out, 0o700);
  const localFallbackPath = join(out, "device.private.json");
  const uploadPath = join(out, randomName);
  const fragmentPath = join(out, "url-fragment.txt");
  const brokerNextPath = join(out, "broker.private.next.json");
  const operatorNextPath = join(out, "operator.private.next.json");
  await Promise.all([
    writePrivateJson(localFallbackPath, deviceConfig),
    writePrivateJson(uploadPath, envelope),
    writePrivateJson(brokerNextPath, nextBroker),
    writePrivateJson(operatorNextPath, nextOperator),
    writeFile(fragmentPath, `#crc-key=${base64url(contentKey)}\n`, { mode: 0o600 }),
  ]);
  await chmod(fragmentPath, 0o600);
  const receipt = {
    format: "crc-device-provisioning-receipt-v1",
    transactionId: randomUUID(),
    createdAt: new Date().toISOString(),
    deviceId,
    keyId: effectiveKeyId,
    operatorId: operator.operatorId,
    brokerConfigPath: brokerPath,
    operatorConfigPath: operatorPath,
    localFallbackPath,
    uploadPath,
    uploadName: basename(uploadPath),
    fragmentPath,
    brokerNextPath,
    operatorNextPath,
    plaintextSha256: createHash("sha256").update(plaintext).digest("hex"),
    envelopeSha256: createHash("sha256").update(stableJson(envelope)).digest("hex"),
  };
  const receiptPath = join(out, "receipt.json");
  await writePrivateJson(receiptPath, receipt);
  return { ...receipt, receiptPath };
}

export async function finalizeProvisioningUrl({
  receiptPath,
  fileshareResultPath,
  outputPath,
}) {
  const receipt = JSON.parse(await readFile(resolve(receiptPath), "utf8"));
  const upload = JSON.parse(await readFile(resolve(fileshareResultPath), "utf8"));
  const file = upload.files?.[0];
  const url = file?.server_url || file?.page_url;
  if (
    upload.ok !== true ||
    typeof url !== "string" ||
    !url.startsWith("https://lixinchen.ca/tempfileshare/") ||
    basename(file.path) !== receipt.uploadName
  ) throw new Error("FileShare result does not match this provisioning transaction");
  const fragment = (await readFile(receipt.fragmentPath, "utf8")).trim();
  const out = resolve(outputPath);
  await writeFile(out, `${url}${fragment}\n`, { mode: 0o600 });
  await chmod(out, 0o600);
  return out;
}

async function main() {
  if (process.argv[2] === "finalize-url") {
    const args = parseArgs(process.argv.slice(3), ["receipt", "fileshare-result", "out"]);
    const out = await finalizeProvisioningUrl({
      receiptPath: args.receipt,
      fileshareResultPath: args["fileshare-result"],
      outputPath: args.out,
    });
    process.stdout.write(`${JSON.stringify({ ok: true, provisioningUrlPath: out })}\n`);
    return;
  }
  const args = parseArgs(process.argv.slice(2));
  const result = await provisionRwcDevice({
    deviceId: args["device-id"],
    keyId: args["key-id"],
    brokerConfigPath: args["broker-config"],
    operatorConfigPath: args["operator-config"],
    outputDirectory: args.out,
  });
  process.stdout.write(`${JSON.stringify({
    ok: true,
    transactionId: result.transactionId,
    deviceId: result.deviceId,
    keyId: result.keyId,
    localFallbackPath: result.localFallbackPath,
    uploadPath: result.uploadPath,
    fragmentPath: result.fragmentPath,
    brokerNextPath: result.brokerNextPath,
    operatorNextPath: result.operatorNextPath,
    receiptPath: result.receiptPath,
  })}\n`);
}

if (import.meta.url === pathToFileURL(process.argv[1]).href) {
  main().catch((error) => {
    process.stderr.write(`Provisioning failed: ${error.message}\n`);
    process.exitCode = 1;
  });
}
