# CRC protocol v2

CRC v2 uses JSON envelopes over WSS. The public endpoint is
`wss://lixinchen.ca/crc/v2/ws`; IIS proxies it to the broker's loopback endpoint
`ws://127.0.0.1:7590/ws`.

Every message uses only these top-level fields:

```text
{v:2,type,messageId,ts,sessionId?,seq?,from?,to?,body?}
```

`messageId` is a base64url identifier and `ts` is Unix time in milliseconds.

Authentication is a challenge/response exchange. The broker first sends
`auth.challenge.body.nonce`. The peer sends `auth.response` with `body` containing
`role`, `principalId`, `keyId`, a fresh 32-byte base64url `clientNonce`,
non-secret `metadata`, and
`proof = base64url(HMAC-SHA256(authKey, canonical))`,
where canonical is:

```text
crc-v2-auth
role
principalId
keyId
brokerNonce
clientNonce
ts
```

The `ts` in the proof is the top-level `auth.response.ts`. It must be within 30
seconds. Every peer has an independent 32-byte authentication key. Authentication
keys are broker credentials and are distinct from the endpoint-only E2EE secret.

After authentication, operators receive `presence.snapshot`. An operator may
send `session.open.body.deviceId` for a device in its ACL. Both peers receive
`session.ready`. `relay.data` has this shape:

```json
{
  "v": 2,
  "type": "relay.data",
  "sessionId": "...",
  "seq": "1",
  "messageId": "...",
  "ts": 0,
  "from": "operator:operator-alpha",
  "to": "device:device-mac-001",
  "body": {"nonce": "...", "ciphertext": "...", "tag": "..."}
}
```

`seq` is a strictly increasing canonical decimal integer, independently counted
from 1 in each direction. `messageId` is unique within the session. The broker
only validates and routes the three opaque E2EE fields.

## End-to-end encryption

Endpoints share a random 32-byte secret that is never sent to the broker. The
session key is HKDF-SHA256 with UTF-8 `sessionId` as salt and
`crc-v2-e2ee|operatorId|deviceId` as info. Relay messages use AES-256-GCM with a
fresh 12-byte nonce. The AAD is:

```text
crc-v2-aad
sessionId
from
to
seq
messageId
ts
```

The implementation and deterministic interop vector are in `index.mjs` and
`test/vectors.json`.
