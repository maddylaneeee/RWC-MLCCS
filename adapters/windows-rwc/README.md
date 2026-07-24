# RWC-MLCCS

RWC-MLCCS is CRC's Windows device adapter and cross-platform operator CLI. Both
programs make outbound, certificate-validated WSS connections to the CRC v2
broker. Neither program exposes a public listener.

## Security model

- Every device and operator has an independent 32-byte broker authentication
  key. Challenge responses use HMAC-SHA256 and fresh nonces.
- The broker authorizes operator/device sessions, enforces sequence and replay
  rules, and routes opaque ciphertext only.
- Command messages and results use AES-256-GCM end-to-end encryption. The
  per-session key is derived with HKDF-SHA256 from the separately provisioned
  device/operator E2EE key. That key is never sent to the broker.
- TLS verification is mandatory; there is no invalid-certificate production
  option.
- Logs contain identifiers and lifecycle events, not commands or command
  output.
- The optional HTTP API binds only to `127.0.0.1` and requires a randomly
  generated Bearer token.

Authentication keys and E2EE keys serve different purposes and should be
generated independently. Never commit populated configuration files.

## Configuration

The administrator-provisioned local device file contains:

```json
{
  "brokerUrl": "wss://lixinchen.ca/crc/v2/ws",
  "deviceId": "CLIENT-01",
  "keyId": "device-key-1",
  "brokerAuthKey": "<32-byte base64url>",
  "e2eeKey": "<32-byte base64url>",
  "allowLocalPowerShellFallback": true,
  "reconnectDelaySeconds": 2,
  "maxReconnectDelaySeconds": 60,
  "commandCancelGraceSeconds": 3
}
```

The operator configuration (`server.json`) contains its own broker identity and
an E2EE key for each authorized device:

```json
{
  "brokerUrl": "wss://lixinchen.ca/crc/v2/ws",
  "operatorId": "operator-01",
  "keyId": "operator-key-1",
  "brokerAuthKey": "<32-byte base64url>",
  "devices": {
    "CLIENT-01": { "e2eeKey": "<same device E2EE key>" }
  },
  "commandTimeoutSeconds": 600,
  "loopbackApi": {
    "enabled": false,
    "port": 7581,
    "bearerToken": ""
  }
}
```

Run `RWC-MLCCS.Server --init` once to create the configuration and a random API
token. Add matching peer identities and the operator's allowed device IDs to
the broker's private configuration.

## Build and test

The full package requires Windows and the .NET 8 SDK:

```powershell
pwsh -ExecutionPolicy Bypass -File .\build.ps1
```

Build only the cross-platform operator:

```powershell
pwsh -ExecutionPolicy Bypass -File .\build-server.ps1 -Runtime osx-arm64
```

Artifacts remain under `artifacts/client`, `artifacts/server*`, and
`artifacts/web/rwc-mlccs`.

## Operator CLI

```text
clients
use <deviceId>
hostname
tool sysinfo
exit
```

After `use`, any other line is encrypted locally and sent through the broker.
The Windows device continues to support the existing `tool ...` commands for
diagnostics, search, Python, file inspection, downloads, FileShare, and
clipboard access.

## Provision one Windows device

Codex can create a single-device provisioning transaction with:

```bash
node scripts/provision-rwc-device.mjs \
  --device-id CLIENT-01 \
  --broker-config broker/config/private.json \
  --operator-config adapters/windows-rwc/config/operator.private.json \
  --out /secure/transaction-directory
```

The transaction contains updated broker/operator candidates, a mode-0600 local
`device.private.json` fallback, and an AES-256-GCM encrypted FileShare payload.
The broker candidate contains the device authentication key but never the E2EE
key; the operator candidate contains the E2EE key but never the device
authentication key.

FileShare's temporary tier is not a true burn-after-reading service. Upload only
the encrypted random-name payload and append the generated `#crc-key=...` URL
fragment. HTTP does not send that fragment to FileShare or IIS. Never upload the
plaintext fallback.

## Windows device

Run `RWC-MLCCS.Client.exe`, approve UAC, and accept the policy. Enter:

1. `https://lixinchen.ca/rwc-mlccs/config.json`
2. the complete encrypted FileShare provisioning URL supplied by Codex

The private URL field is masked. The client requires certificate-validated
HTTPS, restricts the private file to the FileShare temporary route, downloads
with strict size and redirect limits, authenticates and decrypts it in memory,
checks that its broker matches the public bootstrap, and atomically saves only
the validated plaintext under an Administrators/SYSTEM-only ACL.

The previous local-file workflow remains available: select a local
`device.private.json` in the second field. Runtime state is stored under
`%ProgramData%\RWC-MLCCS`. Closing the program cancels the outbound connection
and child PowerShell process.

RWC-MLCCS is MIT licensed. See [LICENSE](LICENSE).
