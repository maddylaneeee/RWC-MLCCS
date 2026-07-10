import CryptoKit
import Foundation
import Security

actor WebSocketWriter {
    private let task: URLSessionWebSocketTask

    init(task: URLSessionWebSocketTask) {
        self.task = task
    }

    func send(_ envelope: ProtocolEnvelope) async throws {
        let data = try JSONEncoder.rmc.encode(envelope)
        guard let text = String(data: data, encoding: .utf8) else {
            throw RMCError.protocolError("Could not encode WebSocket message.")
        }
        try await task.send(.string(text))
    }
}

final class RemoteClient: NSObject, URLSessionDelegate {
    private let config: ClientConfig
    private let executor: ShellExecutor
    private let onLog: (String) -> Void
    private let onConnectionChange: (Bool) -> Void
    private var currentTask: URLSessionWebSocketTask?
    private var session: URLSession?
    private var stopped = false

    init(
        config: ClientConfig,
        passwordProvider: @escaping ShellExecutor.PasswordProvider,
        onLog: @escaping (String) -> Void,
        onConnectionChange: @escaping (Bool) -> Void
    ) {
        self.config = config
        self.executor = ShellExecutor(passwordProvider: passwordProvider)
        self.onLog = onLog
        self.onConnectionChange = onConnectionChange
    }

    func run() async {
        stopped = false
        while !stopped && !Task.isCancelled {
            do {
                try await runOnce()
            } catch {
                if !stopped && !Task.isCancelled {
                    onLog("连接失败：\(error.localizedDescription)")
                }
            }

            onConnectionChange(false)
            if stopped || Task.isCancelled {
                break
            }

            let delay = UInt64(max(1, config.reconnectDelaySeconds)) * 1_000_000_000
            try? await Task.sleep(nanoseconds: delay)
        }
    }

    func stop() {
        stopped = true
        currentTask?.cancel(with: .goingAway, reason: nil)
        session?.invalidateAndCancel()
    }

    private func runOnce() async throws {
        guard let url = URL(string: config.serverUrl) else {
            throw RMCError.configuration("Invalid server URL: \(config.serverUrl)")
        }

        let session = URLSession(configuration: .default, delegate: self, delegateQueue: nil)
        self.session = session
        let task = session.webSocketTask(with: url)
        currentTask = task
        task.resume()
        defer {
            task.cancel(with: .goingAway, reason: nil)
            session.invalidateAndCancel()
            currentTask = nil
        }

        onLog("正在连接 \(config.serverUrl)")
        let writer = WebSocketWriter(task: task)
        let challengeEnvelope = try await receiveEnvelope(from: task)
        guard challengeEnvelope.type == "challenge" else {
            throw RMCError.protocolError("Server did not send a challenge.")
        }

        let challenge = try challengeEnvelope.payloadAs(ChallengePayload.self)
        let auth = AuthPayload(
            clientId: config.clientId,
            userName: NSUserName(),
            machineName: Host.current().localizedName ?? "macOS",
            platform: "macOS",
            appVersion: Bundle.main.object(forInfoDictionaryKey: "CFBundleShortVersionString") as? String ?? "0",
            response: createAuthResponse(nonce: challenge.nonce, clientId: config.clientId)
        )
        try await writer.send(try ProtocolEnvelope.make("auth", payload: auth))

        let hello = try await receiveEnvelope(from: task)
        guard hello.type == "hello" else {
            throw RMCError.protocolError("Server rejected authentication.")
        }

        onConnectionChange(true)
        onLog("已连接并通过服务端认证")

        while !stopped && !Task.isCancelled {
            let message = try await receiveEnvelope(from: task)
            if message.type == "command", let requestId = message.requestId {
                await handleCommand(message, requestId: requestId, writer: writer)
            } else if message.type == "disconnect" {
                onLog("服务端请求断开连接")
                return
            }
        }
    }

    private func handleCommand(_ envelope: ProtocolEnvelope, requestId: String, writer: WebSocketWriter) async {
        do {
            let command = try envelope.payloadAs(CommandPayload.self)
            let timeout = command.timeoutSeconds ?? config.commandTimeoutSeconds
            let result: ExecutionResult

            if command.kind == "tool" {
                let toolName = command.toolName ?? ""
                result = await ToolRunner.run(
                    name: toolName,
                    args: command.toolArgs ?? [],
                    executor: executor,
                    timeoutSeconds: timeout
                )
                onLog("执行工具：\(toolName)")
            } else {
                let shellCommand = command.command ?? ""
                let runAsRoot = command.runAsRoot ?? false
                if runAsRoot && !config.allowRootCommands {
                    result = ExecutionResult(
                        exitCode: 77,
                        stdout: "",
                        stderr: "root command execution is disabled by client config.\n",
                        cancelled: false,
                        durationMs: 0
                    )
                } else {
                    result = await executor.execute(command: shellCommand, runAsRoot: runAsRoot, timeoutSeconds: timeout)
                }
                onLog("执行命令：\(runAsRoot ? "root" : "user") \(shellCommand)")
            }

            try await sendOutput(result.stdout, stream: "stdout", requestId: requestId, writer: writer)
            try await sendOutput(result.stderr, stream: "stderr", requestId: requestId, writer: writer)
            try await writer.send(try ProtocolEnvelope.make(
                "complete",
                requestId: requestId,
                payload: CompletePayload(exitCode: result.exitCode, cancelled: result.cancelled, durationMs: result.durationMs)
            ))
        } catch {
            try? await writer.send(try ProtocolEnvelope.make(
                "output",
                requestId: requestId,
                payload: OutputPayload(stream: "stderr", text: "\(error.localizedDescription)\n")
            ))
            try? await writer.send(try ProtocolEnvelope.make(
                "complete",
                requestId: requestId,
                payload: CompletePayload(exitCode: -1, cancelled: false, durationMs: 0)
            ))
        }
    }

    private func sendOutput(_ text: String, stream: String, requestId: String, writer: WebSocketWriter) async throws {
        guard !text.isEmpty else {
            return
        }

        let chunkSize = 60_000
        var index = text.startIndex
        while index < text.endIndex {
            let end = text.index(index, offsetBy: chunkSize, limitedBy: text.endIndex) ?? text.endIndex
            let chunk = String(text[index..<end])
            try await writer.send(try ProtocolEnvelope.make(
                "output",
                requestId: requestId,
                payload: OutputPayload(stream: stream, text: chunk)
            ))
            index = end
        }
    }

    private func receiveEnvelope(from task: URLSessionWebSocketTask) async throws -> ProtocolEnvelope {
        let message = try await task.receive()
        switch message {
        case .string(let text):
            guard let data = text.data(using: .utf8) else {
                throw RMCError.protocolError("Received non-UTF8 text.")
            }
            return try JSONDecoder.rmc.decode(ProtocolEnvelope.self, from: data)
        case .data(let data):
            return try JSONDecoder.rmc.decode(ProtocolEnvelope.self, from: data)
        @unknown default:
            throw RMCError.protocolError("Received an unsupported WebSocket message.")
        }
    }

    private func createAuthResponse(nonce: String, clientId: String) -> String {
        let key = SymmetricKey(data: Data(config.sharedSecret.utf8))
        let message = Data("\(nonce):\(clientId)".utf8)
        let digest = HMAC<SHA256>.authenticationCode(for: message, using: key)
        return digest.map { String(format: "%02x", $0) }.joined()
    }

    func urlSession(
        _ session: URLSession,
        didReceive challenge: URLAuthenticationChallenge,
        completionHandler: @escaping (URLSession.AuthChallengeDisposition, URLCredential?) -> Void
    ) {
        guard challenge.protectionSpace.authenticationMethod == NSURLAuthenticationMethodServerTrust,
              let trust = challenge.protectionSpace.serverTrust else {
            completionHandler(.performDefaultHandling, nil)
            return
        }

        let pinned = normalizedFingerprint(config.pinnedServerCertificateSha256)
        if !pinned.isEmpty,
           let chain = SecTrustCopyCertificateChain(trust) as? [SecCertificate],
           let certificate = chain.first {
            let data = SecCertificateCopyData(certificate) as Data
            let fingerprint = SHA256.hash(data: data).map { String(format: "%02x", $0) }.joined()
            if normalizedFingerprint(fingerprint) == pinned {
                completionHandler(.useCredential, URLCredential(trust: trust))
                return
            }
        }

        if config.allowInvalidServerCertificate {
            completionHandler(.useCredential, URLCredential(trust: trust))
        } else {
            completionHandler(.performDefaultHandling, nil)
        }
    }

    private func normalizedFingerprint(_ value: String) -> String {
        value
            .replacingOccurrences(of: ":", with: "")
            .replacingOccurrences(of: " ", with: "")
            .trimmingCharacters(in: .whitespacesAndNewlines)
            .lowercased()
    }
}
