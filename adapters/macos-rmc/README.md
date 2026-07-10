# RMC-MLCCS

RMC-MLCCS is the macOS adapter in the CRC remote-control toolset. It remains a native Mac client and Node CLI server, while CRC is the shared Codex skill and operating model that also covers the Windows RWC adapter. A macOS client app opens an outbound WebSocket connection to a CLI server. The server can then run shell commands, root commands, file-transfer helpers, or built-in tools on the connected Mac through the reverse channel.

## Design

- Native SwiftUI macOS client with a permission/status UI.
- Node CLI server with interactive commands and loopback operator endpoints.
- HMAC challenge-response authentication using a pre-shared secret.
- Private default configuration for `wss://lixinchen.ca:5002/link`.
- Self-signed TLS generation with client-side certificate fingerprint pinning.
- Sudo password is requested by the client UI and kept only in memory while the app is running.
- macOS privacy permissions are detected and routed to System Settings; they are not silently granted.

## Build

```bash
npm install
npm run build
```

Outputs:

```text
artifacts/client/RMC-MLCCS.app
artifacts/server/rmc-server.mjs
artifacts/server/server.private.json
```

The build creates private server/client config if missing. To rotate the secret and pinned certificate, delete:

```text
config/client.private.json
config/server.private.json
config/rmc.shared-secret
config/tls/
```

Then run `npm run build` again.

## Run The Server

```bash
node artifacts/server/rmc-server.mjs --config artifacts/server/server.private.json
```

Interactive commands:

```text
help
clients
use <clientId>
run <shell command>
root <shell command>
tool <name> [args...]
exit
```

After selecting a client, a plain line is treated as `run <line>`. A line beginning with `sudo ` is sent as `root <line after sudo>`.

## Built-In Client Tools

```text
tool sysinfo
tool permissions
tool screenshot
tool processes
tool network
tool apps
tool brew
tool deps
tool rg <pattern> [path] [maxLines]
tool python <code> [args...]
tool python-file <path> [args...]
tool download-url <url> [outputPath]
tool fileshare-upload <path> [baseUrl]
tool file-info <path>
tool read-text <path> [maxBytes]
tool clipboard
tool password-popup [title] [message]
```

`password-popup` displays a local hidden password prompt on the Mac and only reports whether input was received or cancelled; it does not return the password value to the server. Use `fileshare-upload` with the default `https://lixinchen.ca` base URL to move files from a client Mac back to FileShare, and `download-url` to pull files from FileShare or another HTTPS endpoint onto the client.

## Security Notice

RMC-MLCCS grants the connected server broad control over the client Mac, including root command execution after the local user validates sudo in the app. Only run it on Macs you own or are explicitly authorized to administer, and only connect it to a server you control.
