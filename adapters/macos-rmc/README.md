# RMC-MLCCS

RMC-MLCCS is CRC's macOS adapter. Both the Swift device app and the Node operator connect **outbound only** to the CRC v2 broker over `wss://lixinchen.ca/crc/v2/ws` (HTTPS port 443). Neither side needs a public IP or an inbound firewall rule.

## Security model

- The device and operator authenticate independently with timestamped HMAC-SHA256 challenge responses.
- The broker authorizes `session.open`, assigns a session ID, and routes opaque `relay.data`; it cannot execute commands.
- Command, output, and completion bodies use AES-256-GCM end-to-end encryption.
- Per-session keys use HKDF-SHA256 with salt `sessionId` and info `crc-v2-e2ee|operatorId|deviceId`.
- AAD covers session, endpoints, sequence, message ID, and timestamp.
- Message IDs and strictly contiguous per-direction sequences prevent replay and reordering.
- TLS always uses system trust validation. There is no invalid-certificate bypass.
- The operator API binds to loopback and requires a random bearer token stored in a mode `0600` file.
- Audit logs contain identifiers and outcomes, never command or output plaintext.
- Connections use heartbeats and jittered exponential reconnect backoff.

The broker authentication keys are different for every principal. The device/operator pair shares only its E2EE key; do not register that key with the broker.

## Configuration

Copy `config/client.sample.json` and `config/server.sample.json`, or generate private local files:

```bash
bash scripts/generate-private-config.sh
```

Register the generated device and operator authentication keys/key IDs in the broker key registry. Keep `rmc.e2ee-key` only on the device and authorized operator hosts. All private files are ignored by Git.

The normal client build does not embed private credentials. Provision
`~/Library/Application Support/RMC-MLCCS/config.json` through a secure channel
before launching the app. `CRC_EMBED_PRIVATE_CONFIG=1` exists only for tightly
controlled one-off deployments because anyone who can copy that app bundle can
extract its device credentials.

## Build and test

```bash
npm install
npm run check
npm test
npm run build
```

Outputs:

```text
artifacts/client/RMC-MLCCS.app
artifacts/server/rmc-server.mjs
artifacts/server/protocol-v2.mjs
artifacts/server/server.private.json
```

## Run the operator

```bash
node artifacts/server/rmc-server.mjs --config artifacts/server/server.private.json
```

Interactive commands remain:

```text
help
clients
use <deviceId>
run <shell command>
root <shell command>
tool <name> [args...]
exit
```

The loopback HTTP API requires:

```text
Authorization: Bearer <contents of operator.token>
```

Endpoints are `GET /health`, `GET /operator/clients`, and `POST /operator/execute`.

## Built-in device tools

The existing `sysinfo`, `permissions`, `screenshot`, `processes`, `network`, `apps`, `brew`, `deps`, `rg`, `python`, `python-file`, `download-url`, `fileshare-upload`, `file-info`, `read-text`, `clipboard`, and `password-popup` tools remain available. macOS privacy approval and sudo validation still happen locally and are never bypassed.
