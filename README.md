# CRC-MLCCS

CRC-MLCCS is a cross-platform reverse-control toolkit for authorized administration of macOS and Windows machines. It unifies two platform adapters behind the shared CRC operating model while keeping each native client and server implementation independently buildable.

## Adapters

| Adapter | Target | Client | Server |
| --- | --- | --- | --- |
| RMC | macOS | Native SwiftUI app | Node.js CLI |
| RWC | Windows | Elevated WinForms app | Cross-platform .NET CLI |

- [`adapters/macos-rmc`](adapters/macos-rmc): macOS reverse-control client and Node.js server.
- [`adapters/windows-rwc`](adapters/windows-rwc): Windows reverse-control client and cross-platform .NET server.

The RMC and RWC names remain as adapter identifiers and artifact names. CRC is the project name and shared operating model.

## CRC v2 relay

[`broker`](broker) is the MLCCS-hosted authenticated broker for operation across
NAT and arbitrary public networks. Devices and operators make outbound WSS
connections to `wss://lixinchen.ca/crc/v2/ws`; MLCCS routes short authorized
sessions by device ID without executing commands or holding E2EE keys. The shared
wire format and AES-256-GCM/HKDF implementation are in
[`protocol/v2`](protocol/v2).

## Security

CRC provides broad remote command execution and diagnostic capabilities. Use it only on systems you own or are explicitly authorized to administer. Replace every sample secret and development certificate before real use. Private configuration, generated keys, certificates, build output, and runtime state are intentionally excluded from this repository.

macOS privacy permissions and Windows UAC approval must be granted locally by the machine user; CRC does not bypass platform security controls.

## Build

Build instructions and platform requirements are documented in each adapter directory:

- [RMC build and usage](adapters/macos-rmc/README.md)
- [RWC build and usage](adapters/windows-rwc/README.md)

For one-device provisioning, use `scripts/provision-rwc-device.mjs` (Windows is
the default; pass `--platform macos` for RMC). FileShare delivery uses an
encrypted payload and a URL-fragment content key; plaintext device credentials
are never uploaded. Both device apps can later replace their installed
configuration from the UI using new URLs or a new local
`device.private.json`.

## License

CRC-MLCCS is released under the MIT License. See [LICENSE](LICENSE).
