# CRC v2 broker

The broker is an untrusted metadata router. Devices and operators make outbound
WSS connections to `wss://lixinchen.ca/crc/v2/ws`; IIS terminates TLS and proxies
to `ws://127.0.0.1:7590/ws`. The broker authenticates peers, applies operator
ACLs, creates short sessions, rejects replayed traffic, and writes a chained
metadata-only audit log. It never receives the E2EE shared secret or plaintext.

Create a private configuration with `npm run config:init`, review its peer IDs
and ACLs, then run `npm run broker`. Do not commit `config/private.json`.

On MLCCS, run `scripts/install-windows.ps1` as Administrator. It installs the
`CRC-MLCCS Broker` Scheduled Task as SYSTEM at startup and restricts config and
audit directories to SYSTEM and Administrators. Install IIS URL Rewrite and
Application Request Routing, enable WebSocket proxying, and merge
`iis/web.config.example` into the lixinchen.ca site configuration.
