# RWC-MLCCS

RWC-MLCCS is a Windows remote-control client/server utility for authorized MLCCS administration. A Windows 11 client opens an outbound TLS WebSocket connection to a CLI server, and the server can then run PowerShell commands on the connected client through the reverse channel.

The client is packaged as a single elevated installer-style executable. During setup it shows a step-by-step wizard, asks for user policy acceptance, downloads its runtime configuration from `https://lixinchen.ca/rwc-mlccs/config.json`, stores local state under `%ProgramData%\RWC-MLCCS`, and starts the background connection while the program remains open.

## Features

- Windows 11 WinForms setup wizard with UAC elevation.
- Single-file client executable for initialization and runtime.
- TLS WebSocket reverse connection to `lixinchen.ca:7580`.
- Pre-shared-key challenge-response authentication.
- CLI server with interactive `clients`, `use <clientId>`, and PowerShell command execution.
- Long-lived self-signed development certificate generation for local testing.
- Client logs and configuration stored under `%ProgramData%\RWC-MLCCS`.
- MIT licensed.

## Security Notice

RWC-MLCCS grants the connected server broad control over the client machine. Only run the client on machines you own or are explicitly authorized to administer, and only connect it to a server you control. Replace the sample pre-shared secret before real use.

Self-signed certificates and `allowInvalidServerCertificate` are intended for local development only. Production deployments should use a normal certificate for the hostname clients connect to.

## Repository Layout

```text
src/RWC-MLCCS.Client   Windows installer-style GUI client
src/RWC-MLCCS.Server   CLI server
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

Client bootstrap configuration is downloaded from:

```text
https://lixinchen.ca/rwc-mlccs/config.json
```

The downloaded client configuration is stored at:

```text
%ProgramData%\RWC-MLCCS\config.json
```

The `sharedSecret` must match on both sides.

## Download

The current client installer executable can be served from:

```text
https://lixinchen.ca/rwc-mlccs/RWC-MLCCS.Client.exe
```

The same directory also serves the client bootstrap configuration:

```text
https://lixinchen.ca/rwc-mlccs/config.json
```

## Build

Requirements:

- Windows
- .NET SDK capable of building `net8.0` and `net8.0-windows`

Build, test, and publish:

```powershell
powershell -ExecutionPolicy Bypass -File .\build.ps1
```

Outputs:

```text
artifacts/client/RWC-MLCCS.Client.exe
artifacts/server/RWC-MLCCS.Server.exe
artifacts/web/rwc-mlccs/config.json
```

The client artifact is intended to be distributed as a single executable.

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

Interactive commands:

```text
clients
use <clientId>
hostname
whoami
exit
```

After selecting a client with `use <clientId>`, any non-control line is sent to that client as a PowerShell command.

## Run The Client

Run:

```powershell
.\artifacts\client\RWC-MLCCS.Client.exe
```

Approve UAC, read the policy page, click through the wizard, and keep the window open while the reverse control channel should remain active. Closing the program stops the channel and any PowerShell child process created by the tool.

## License

RWC-MLCCS is released under the MIT License. See [LICENSE](LICENSE).
