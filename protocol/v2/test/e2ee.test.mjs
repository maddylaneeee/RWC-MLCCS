import assert from "node:assert/strict";
import { readFile } from "node:fs/promises";
import test from "node:test";
import {
  base64url,
  decryptRelay,
  deriveSessionKey,
  encryptRelay,
} from "../index.mjs";

const vector = JSON.parse(
  await readFile(new URL("./vectors.json", import.meta.url), "utf8"),
);

test("E2EE deterministic interoperability vector", () => {
  const key = deriveSessionKey(vector);
  assert.equal(base64url(key), vector.key);
  const encrypted = encryptRelay({ ...vector, key, plaintext: Buffer.from(vector.plaintext, "utf8") });
  assert.deepEqual(encrypted, vector.encrypted);
  assert.equal(decryptRelay({ ...vector, key, ...encrypted }).toString(), vector.plaintext);
});

test("AAD tampering fails authentication", () => {
  const key = deriveSessionKey(vector);
  assert.throws(() => decryptRelay({ ...vector, key, ...vector.encrypted, seq: "2" }));
});
