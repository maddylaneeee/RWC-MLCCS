import assert from "node:assert/strict";
import { createDecipheriv, randomBytes } from "node:crypto";
import { mkdtemp, readFile, stat, writeFile } from "node:fs/promises";
import { tmpdir } from "node:os";
import { join } from "node:path";
import test from "node:test";
import {
  finalizeProvisioningUrl,
  provisionRwcDevice,
} from "../../../scripts/provision-rwc-device.mjs";

const key = () => randomBytes(32).toString("base64url");

async function fixture() {
  const dir = await mkdtemp(join(tmpdir(), "crc-provision-test-"));
  const brokerConfigPath = join(dir, "broker.json");
  const operatorConfigPath = join(dir, "operator.json");
  const authKey = key();
  await writeFile(brokerConfigPath, JSON.stringify({
    auth: {
      peers: [{
        role: "operator",
        principalId: "operator-one",
        keyId: "operator-key-one",
        authKey,
        allowedDevices: ["existing-device"],
      }],
    },
  }));
  await writeFile(operatorConfigPath, JSON.stringify({
    brokerUrl: "wss://lixinchen.ca/crc/v2/ws",
    operatorId: "operator-one",
    keyId: "operator-key-one",
    brokerAuthKey: authKey,
    devices: { "existing-device": { e2eeKey: key() } },
  }));
  return { dir, brokerConfigPath, operatorConfigPath };
}

test("creates an encrypted one-device provisioning transaction without leaking E2EE to broker", async () => {
  const fx = await fixture();
  const result = await provisionRwcDevice({
    deviceId: "CLIENT-NEW",
    brokerConfigPath: fx.brokerConfigPath,
    operatorConfigPath: fx.operatorConfigPath,
    outputDirectory: join(fx.dir, "transaction"),
  });
  const device = JSON.parse(await readFile(result.localFallbackPath, "utf8"));
  const envelope = JSON.parse(await readFile(result.uploadPath, "utf8"));
  const broker = JSON.parse(await readFile(result.brokerNextPath, "utf8"));
  const operator = JSON.parse(await readFile(result.operatorNextPath, "utf8"));
  const receipt = JSON.parse(await readFile(result.receiptPath, "utf8"));
  const fragment = await readFile(result.fragmentPath, "utf8");

  assert.equal(Buffer.from(device.brokerAuthKey, "base64url").length, 32);
  assert.equal(Buffer.from(device.e2eeKey, "base64url").length, 32);
  assert.notEqual(device.brokerAuthKey, device.e2eeKey);
  assert.equal(envelope.format, "crc-device-provisioning-v1");
  assert.ok(fragment.startsWith("#crc-key="));
  const contentKey = Buffer.from(fragment.trim().slice("#crc-key=".length), "base64url");
  const decipher = createDecipheriv(
    "aes-256-gcm",
    contentKey,
    Buffer.from(envelope.nonce, "base64url"),
  );
  decipher.setAAD(Buffer.from(envelope.format, "utf8"));
  decipher.setAuthTag(Buffer.from(envelope.tag, "base64url"));
  const decrypted = JSON.parse(Buffer.concat([
    decipher.update(Buffer.from(envelope.ciphertext, "base64url")),
    decipher.final(),
  ]));
  assert.equal(decrypted.deviceId, "CLIENT-NEW");
  assert.equal(decrypted.brokerAuthKey, device.brokerAuthKey);
  assert.ok(broker.auth.peers.some((peer) => peer.principalId === "CLIENT-NEW"));
  assert.ok(broker.auth.peers[0].allowedDevices.includes("CLIENT-NEW"));
  assert.equal(JSON.stringify(broker).includes(device.e2eeKey), false);
  assert.equal(operator.devices["CLIENT-NEW"].e2eeKey, device.e2eeKey);
  assert.equal(JSON.stringify(operator).includes(device.brokerAuthKey), false);
  assert.equal(JSON.stringify(receipt).includes(device.brokerAuthKey), false);
  assert.equal(JSON.stringify(receipt).includes(device.e2eeKey), false);
  if (process.platform !== "win32") {
    assert.equal((await stat(result.localFallbackPath)).mode & 0o777, 0o600);
    assert.equal((await stat(result.uploadPath)).mode & 0o777, 0o600);
  }
});

test("fails closed when a device identity already exists", async () => {
  const fx = await fixture();
  await assert.rejects(
    provisionRwcDevice({
      deviceId: "existing-device",
      brokerConfigPath: fx.brokerConfigPath,
      operatorConfigPath: fx.operatorConfigPath,
      outputDirectory: join(fx.dir, "transaction"),
    }),
    /already exists/,
  );
});

test("finalizes an encrypted temporary FileShare URL with the transaction fragment", async () => {
  const fx = await fixture();
  const result = await provisionRwcDevice({
    deviceId: "CLIENT-LINK",
    brokerConfigPath: fx.brokerConfigPath,
    operatorConfigPath: fx.operatorConfigPath,
    outputDirectory: join(fx.dir, "transaction"),
  });
  const uploadResultPath = join(fx.dir, "fileshare.json");
  await writeFile(uploadResultPath, JSON.stringify({
    ok: true,
    files: [{
      path: result.uploadPath,
      server_url: `https://lixinchen.ca/tempfileshare/1234567890123/${result.uploadName}`,
    }],
  }));
  const outputPath = join(fx.dir, "provisioning-url.txt");
  await finalizeProvisioningUrl({
    receiptPath: result.receiptPath,
    fileshareResultPath: uploadResultPath,
    outputPath,
  });
  const url = (await readFile(outputPath, "utf8")).trim();
  assert.ok(url.startsWith("https://lixinchen.ca/tempfileshare/"));
  assert.ok(url.includes("#crc-key="));
  if (process.platform !== "win32") {
    assert.equal((await stat(outputPath)).mode & 0o777, 0o600);
  }
});
