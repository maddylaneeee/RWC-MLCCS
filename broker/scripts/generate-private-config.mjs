import { randomBytes } from "node:crypto";
import { mkdir, writeFile } from "node:fs/promises";
import { dirname, resolve } from "node:path";

const output = resolve(process.argv[2] ?? "broker/config/private.json");
const key = () => randomBytes(32).toString("base64url");
const config = {
  listen: { host: "127.0.0.1", port: 7590, path: "/ws" },
  auth: {
    windowMs: 30_000,
    attemptsPerMinute: 20,
    peers: [
      {
        role: "operator",
        principalId: "primary-operator",
        keyId: "key-1",
        authKey: key(),
        allowedDevices: ["device-1"],
      },
      {
        role: "device",
        principalId: "device-1",
        keyId: "key-1",
        authKey: key(),
      },
    ],
  },
  sessions: { ttlMs: 28_800_000 },
  limits: {
    maxMessageBytes: 1_100_000,
    maxCiphertextBytes: 1_048_576,
    maxConnections: 200,
    maxUnauthenticated: 50,
  },
  heartbeat: { intervalMs: 20_000, timeoutMs: 60_000 },
  audit: {
    path: resolve(dirname(output), "../state/audit.jsonl"),
    hmacKey: key(),
    maxBytes: 52_428_800,
    maxFiles: 10,
  },
};

await mkdir(dirname(output), { recursive: true });
await writeFile(output, `${JSON.stringify(config, null, 2)}\n`, { mode: 0o600, flag: "wx" });
console.log(`Created ${output}. Distribute each peer authKey only to that peer.`);
