import { createHmac } from "node:crypto";
import { existsSync, readFileSync, statSync } from "node:fs";
import { appendFile, mkdir, rename, rm } from "node:fs/promises";
import { dirname } from "node:path";

const GENESIS = "0".repeat(64);

function stable(value) {
  if (Array.isArray(value)) return `[${value.map(stable).join(",")}]`;
  if (value && typeof value === "object") {
    return `{${Object.keys(value).sort().map((key) => `${JSON.stringify(key)}:${stable(value[key])}`).join(",")}}`;
  }
  return JSON.stringify(value);
}

export class AuditLog {
  #path;
  #key;
  #previous = GENESIS;
  #pending = Promise.resolve();
  #bytes = 0;
  #maxBytes;
  #maxFiles;
  #lastError = null;

  constructor({ path, hmacKey, maxBytes = 50 * 1024 * 1024, maxFiles = 10 }) {
    this.#path = path;
    this.#key = Buffer.from(hmacKey, "base64url");
    if (
      typeof path !== "string" ||
      !path ||
      typeof hmacKey !== "string" ||
      !/^[A-Za-z0-9_-]+$/.test(hmacKey) ||
      this.#key.length !== 32 ||
      this.#key.toString("base64url") !== hmacKey
    ) throw new Error("audit config requires a path and canonical 32-byte base64url hmacKey");
    if (!Number.isInteger(maxBytes) || maxBytes < 1024 * 1024 || maxBytes > 1024 * 1024 * 1024) {
      throw new Error("audit.maxBytes must be between 1 MiB and 1 GiB");
    }
    if (!Number.isInteger(maxFiles) || maxFiles < 1 || maxFiles > 100) {
      throw new Error("audit.maxFiles must be between 1 and 100");
    }
    this.#maxBytes = maxBytes;
    this.#maxFiles = maxFiles;
    if (existsSync(path)) {
      for (const line of readFileSync(path, "utf8").split("\n").filter(Boolean)) {
        const record = JSON.parse(line);
        const { hash, ...unsigned } = record;
        if (unsigned.prevHash !== this.#previous) throw new Error("audit hash chain predecessor mismatch");
        const expected = createHmac("sha256", this.#key).update(stable(unsigned)).digest("hex");
        if (hash !== expected) throw new Error("audit hash chain verification failed");
        this.#previous = hash;
      }
      this.#bytes = statSync(path).size;
    }
  }

  write(event, metadata = {}) {
    let rotateBeforeWrite = false;
    let record = {
      ts: new Date().toISOString(),
      event,
      ...metadata,
      prevHash: this.#previous,
    };
    let hash = createHmac("sha256", this.#key).update(stable(record)).digest("hex");
    let line = `${JSON.stringify({ ...record, hash })}\n`;
    if (this.#bytes > 0 && this.#bytes + Buffer.byteLength(line) > this.#maxBytes) {
      rotateBeforeWrite = true;
      this.#previous = GENESIS;
      this.#bytes = 0;
      record = { ...record, prevHash: GENESIS };
      hash = createHmac("sha256", this.#key).update(stable(record)).digest("hex");
      line = `${JSON.stringify({ ...record, hash })}\n`;
    }
    this.#previous = hash;
    this.#bytes += Buffer.byteLength(line);
    this.#pending = this.#pending.then(async () => {
      await mkdir(dirname(this.#path), { recursive: true });
      if (rotateBeforeWrite) await this.#rotate();
      await appendFile(this.#path, line, { mode: 0o600 });
    }).catch((error) => {
      this.#lastError = error;
      process.stderr.write(`CRC audit write failed: ${error.message}\n`);
    });
    return this.#pending;
  }

  async #rotate() {
    await rm(`${this.#path}.${this.#maxFiles}`, { force: true });
    for (let index = this.#maxFiles - 1; index >= 1; index -= 1) {
      if (existsSync(`${this.#path}.${index}`)) {
        await rename(`${this.#path}.${index}`, `${this.#path}.${index + 1}`);
      }
    }
    if (existsSync(this.#path)) await rename(this.#path, `${this.#path}.1`);
  }

  get healthy() {
    return this.#lastError === null;
  }

  flush() {
    return this.#pending;
  }
}

export function auditStableString(value) {
  return stable(value);
}
