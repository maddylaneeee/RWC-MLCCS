# RWC-MLCCS

RWC-MLCCS is the Windows adapter in the CRC remote-control toolset. It remains a Windows remote-control client and cross-platform server utility for authorized MLCCS administration, while CRC is the shared Codex skill and operating model that also covers the macOS RMC adapter. A Windows 11 client opens an outbound TLS WebSocket connection to a CLI server, and the server can then run PowerShell commands or built-in client tools through the reverse channel. The client remains Windows-only; the server can be published for Windows or macOS.

The client is packaged as a single elevated installer-style executable. During setup it shows a step-by-step wizard, asks for user policy acceptance, downloads its runtime configuration from an HTTPS URL supplied by the operator, stores local state under `%ProgramData%\RWC-MLCCS`, and starts the background connection while the program remains open.

## Features

- Windows 11 WinForms setup wizard with UAC elevation.
- Single-file client executable for initialization and runtime.
- TLS WebSocket reverse connection to the configured server endpoint.
- Pre-shared-key challenge-response authentication.
- Cross-platform CLI server with interactive `clients`, `use <clientId>`, and PowerShell command execution on connected Windows clients.
- Built-in client tools exposed as `tool ...` commands for diagnostics, search, Python, clipboard, file inspection, URL download, and FileShare upload.
- Long-lived self-signed development certificate generation for local testing.
- Client logs and configuration stored under `%ProgramData%\RWC-MLCCS`.
- MIT licensed.

## Security Notice

RWC-MLCCS grants the connected server broad control over the client machine. Only run the client on machines you own or are explicitly authorized to administer, and only connect it to a server you control. Replace the sample pre-shared secret before real use.

Self-signed certificates and `allowInvalidServerCertificate` are intended for local development only. Production deployments should use a normal certificate for the hostname clients connect to.

## Repository Layout

```text
src/RWC-MLCCS.Client   Windows installer-style GUI client
src/RWC-MLCCS.Server   Cross-platform CLI server
src/RWC-MLCCS.Common   shared config, protocol, auth, and command execution
tests/RWC-MLCCS.Tests  unit tests
assets/                MLCCS logo and application icon
```

## Configuration

Server configuration lives beside the server executable as `server.json`:

```json
{
  "listenHost": "0.0.0.0",
  "port": 7580,
  "sharedSecret": "change-this-shared-secret",
  "certificatePath": "server-dev.pfx",
  "certificatePassword": "change-this-cert-password",
  "commandTimeoutSeconds": 600
}
```

Client bootstrap configuration is downloaded from the HTTPS URL entered in the setup wizard. For example:

```text
https://your-server.example/rwc-mlccs/config.json
```

The downloaded client configuration is stored at:

```text
%ProgramData%\RWC-MLCCS\config.json
```

The `sharedSecret` must match on both sides.

## Distribution

The client installer executable can be hosted from any operator-controlled HTTPS location, for example:

```text
https://your-server.example/rwc-mlccs/RWC-MLCCS.Client.exe
```

The same directory can also serve the client bootstrap configuration:

```text
https://your-server.example/rwc-mlccs/config.json
```

## Build

Requirements for the full Windows client/server package:

- Windows
- .NET 8 SDK capable of building `net8.0` and `net8.0-windows`

Build, test, and publish:

```powershell
powershell -ExecutionPolicy Bypass -File .\build.ps1
```

Outputs:

```text
artifacts/client/RWC-MLCCS.Client.exe
artifacts/server/RWC-MLCCS.Server.exe
artifacts/server-osx-arm64/RWC-MLCCS.Server
artifacts/server-osx-x64/RWC-MLCCS.Server
artifacts/web/rwc-mlccs/config.json
```

The client artifact is intended to be distributed as a single executable.

To build only the server for the current platform, including macOS:

```powershell
pwsh -ExecutionPolicy Bypass -File ./build-server.ps1
```

To build specific server runtime targets:

```powershell
pwsh -ExecutionPolicy Bypass -File ./build-server.ps1 -Runtime osx-arm64,osx-x64
```

Server-only builds do not compile or change the Windows client.

If multiple .NET SDK versions are installed on macOS through Homebrew, make sure the .NET 8 SDK is first on `PATH` for this repository.

## Run The Server

Initialize configuration and the development certificate:

```powershell
.\artifacts\server\RWC-MLCCS.Server.exe --init
```

Start the server:

```powershell
cd .\artifacts\server
.\RWC-MLCCS.Server.exe
```

On macOS, use the matching macOS server artifact:

```bash
cd artifacts/server-osx-arm64
./RWC-MLCCS.Server --init
./RWC-MLCCS.Server
```

Interactive commands:

```text
clients
use <clientId>
hostname
whoami
exit
```

After selecting a client with `use <clientId>`, any non-control line is sent to that client as a PowerShell command.

Built-in client tools are invoked with the same text command path:

```text
tool deps
tool sysinfo
tool processes
tool network
tool apps
tool rg <pattern> [path] [maxLines]
tool python <code> [args...]
tool python-file <path> [args...]
tool download-url <url> [outputPath]
tool fileshare-upload <path> [baseUrl]
tool file-info <path>
tool read-text <path> [maxBytes]
tool clipboard
```

`fileshare-upload` defaults to `https://lixinchen.ca` and uses the same chunked API as the FileShare upload page. `download-url` is useful for pulling FileShare URLs or prepared artifacts onto a connected Windows client. `password-popup` is intentionally macOS-only in the CRC toolset; Windows clients should use normal UAC or operator-provided credential workflows.

Windows clients do not need code changes to connect to a macOS server. Keep the same `sharedSecret`, set the client `serverUrl` to the Mac-hosted `wss://host:port/link` endpoint, and make sure the endpoint certificate is trusted or the client configuration allows the development certificate.

## Run The Client

Run:

```powershell
.\artifacts\client\RWC-MLCCS.Client.exe
```

Approve UAC, read the policy page, click through the wizard, and keep the window open while the reverse control channel should remain active. Closing the program stops the channel and any PowerShell child process created by the tool.

## License

RWC-MLCCS is released under the MIT License. See [LICENSE](LICENSE).
