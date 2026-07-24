import CryptoKit
import Foundation

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

actor SessionChannel {
    let sessionId: String
    let operatorId: String
    let deviceId: String
    let expiresAt: Int64
    private let key: SymmetricKey
    private var sendSequence: UInt64 = 0
    private var receiveSequence: UInt64 = 0

    init(sessionId: String, operatorId: String, deviceId: String, expiresAt: Int64, rootKey: Data) {
        self.sessionId = sessionId
        self.operatorId = operatorId
        self.deviceId = deviceId
        self.expiresAt = expiresAt
        self.key = HKDF<SHA256>.deriveKey(
            inputKeyMaterial: SymmetricKey(data: rootKey),
            salt: Data(sessionId.utf8),
            info: Data("crc-v2-e2ee|\(operatorId)|\(deviceId)".utf8),
            outputByteCount: 32
        )
    }

    func decrypt(_ envelope: ProtocolEnvelope) throws -> InnerRawEnvelope {
        guard expiresAt > nowMs() else {
            throw RMCError.protocolError("Session authorization expired.")
        }
        guard envelope.type == "relay.data",
              envelope.sessionId == sessionId,
              envelope.from == "operator:\(operatorId)",
              envelope.to == "device:\(deviceId)",
              let sequenceText = envelope.seq,
              let sequence = UInt64(sequenceText),
              sequence == receiveSequence + 1 else {
            throw RMCError.protocolError("Rejected relay with invalid endpoint or sequence.")
        }
        let relay = try envelope.bodyAs(RelayBody.self)
        guard let nonceData = Data(base64URLEncoded: relay.nonce),
              let ciphertext = Data(base64URLEncoded: relay.ciphertext),
              let tag = Data(base64URLEncoded: relay.tag),
              nonceData.count == 12,
              tag.count == 16 else {
            throw RMCError.protocolError("Invalid AES-GCM fields.")
        }
        let aad = Data(
            "crc-v2-aad\n\(sessionId)\n\(envelope.from!)\n\(envelope.to!)\n\(sequenceText)\n\(envelope.messageId)\n\(envelope.ts)".utf8
        )
        let box = try AES.GCM.SealedBox(
            nonce: AES.GCM.Nonce(data: nonceData),
            ciphertext: ciphertext,
            tag: tag
        )
        let plaintext = try AES.GCM.open(box, using: key, authenticating: aad)
        let inner = try JSONDecoder.rmc.decode(InnerRawEnvelope.self, from: plaintext)
        receiveSequence = sequence
        return inner
    }

    func encrypt<T: Codable>(_ inner: InnerEnvelope<T>) throws -> ProtocolEnvelope {
        sendSequence += 1
        let messageId = UUID().uuidString.lowercased()
        let timestamp = nowMs()
        let sequenceText = String(sendSequence)
        let from = "device:\(deviceId)"
        let to = "operator:\(operatorId)"
        let aad = Data(
            "crc-v2-aad\n\(sessionId)\n\(from)\n\(to)\n\(sequenceText)\n\(messageId)\n\(timestamp)".utf8
        )
        let plaintext = try JSONEncoder.rmc.encode(inner)
        let sealed = try AES.GCM.seal(plaintext, using: key, authenticating: aad)
        guard let combinedNonce = sealed.nonce.withUnsafeBytes({ Data($0) }) as Data? else {
            throw RMCError.protocolError("Could not encode AES-GCM nonce.")
        }
        let body = RelayBody(
            nonce: combinedNonce.base64URLEncodedString(),
            ciphertext: sealed.ciphertext.base64URLEncodedString(),
            tag: sealed.tag.base64URLEncodedString()
        )
        var envelope = try ProtocolEnvelope.make("relay.data", body: body, ts: timestamp)
        envelope.messageId = messageId
        envelope.sessionId = sessionId
        envelope.seq = sequenceText
        envelope.from = from
        envelope.to = to
        return envelope
    }
}

@MainActor
final class RemoteClient: NSObject {
    private let config: ClientConfig
    private let executor: ShellExecutor
    private let onLog: (String) -> Void
    private let onConnectionChange: (Bool) -> Void
    private var currentTask: URLSessionWebSocketTask?
    private var session: URLSession?
    private var stopped = false
    private var seenMessageIds = Set<String>()
    private var seenMessageOrder: [String] = []
    private var channels: [String: SessionChannel] = [:]
    private var commandTasks: [String: Task<Void, Never>] = [:]

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
        var failureCount = 0
        while !stopped && !Task.isCancelled {
            do {
                try validateConfiguration()
                try await runOnce()
                failureCount = 0
            } catch {
                if !stopped && !Task.isCancelled {
                    onLog("连接失败：\(error.localizedDescription)")
                    failureCount += 1
                }
            }
            onConnectionChange(false)
            if stopped || Task.isCancelled { break }
            let base = min(60.0, Double(max(1, config.reconnectDelaySeconds)) * pow(2.0, Double(min(failureCount, 6))))
            let jittered = base * Double.random(in: 0.75...1.25)
            try? await Task.sleep(nanoseconds: UInt64(jittered * 1_000_000_000))
        }
    }

    func stop() {
        stopped = true
        currentTask?.cancel(with: .goingAway, reason: nil)
        session?.invalidateAndCancel()
        for task in commandTasks.values { task.cancel() }
        commandTasks.removeAll()
    }

    private func validateConfiguration() throws {
        guard config.brokerUrl.hasPrefix("wss://"), URL(string: config.brokerUrl) != nil else {
            throw RMCError.configuration("brokerUrl must be a valid wss:// URL.")
        }
        guard !config.deviceId.isEmpty, !config.keyId.isEmpty,
              decodeKey(config.brokerAuthKey) != nil,
              decodeKey(config.e2eeKey) != nil else {
            throw RMCError.configuration("deviceId, keyId, brokerAuthKey and e2eeKey are required.")
        }
    }

    private func runOnce() async throws {
        guard let url = URL(string: config.brokerUrl) else {
            throw RMCError.configuration("Invalid broker URL.")
        }
        let session = URLSession(configuration: .default)
        self.session = session
        let task = session.webSocketTask(with: url)
        currentTask = task
        task.resume()
        defer {
            task.cancel(with: .goingAway, reason: nil)
            session.invalidateAndCancel()
            currentTask = nil
            channels.removeAll()
            seenMessageIds.removeAll()
            seenMessageOrder.removeAll()
            for task in commandTasks.values { task.cancel() }
            commandTasks.removeAll()
        }

        onLog("正在连接 CRC broker")
        let writer = WebSocketWriter(task: task)
        let challenge = try await receiveEnvelope(from: task)
        try acceptEnvelope(challenge)
        guard challenge.type == "auth.challenge" else {
            throw RMCError.protocolError("Broker did not send auth.challenge.")
        }
        let challengeBody = try challenge.bodyAs(ChallengePayload.self)
        let clientNonce = randomBase64URL(bytes: 32)
        let timestamp = nowMs()
        let proof = try createAuthProof(
            brokerNonce: challengeBody.nonce,
            clientNonce: clientNonce,
            timestamp: timestamp
        )
        let auth = AuthPayload(
            role: "device",
            principalId: config.deviceId,
            keyId: config.keyId,
            clientNonce: clientNonce,
            proof: proof,
            metadata: DeviceMetadata(
                userName: NSUserName(),
                machineName: Host.current().localizedName ?? "macOS",
                platform: "macOS",
                appVersion: Bundle.main.object(forInfoDictionaryKey: "CFBundleShortVersionString") as? String ?? "0",
                adapter: "macos-rmc"
            )
        )
        try await writer.send(try ProtocolEnvelope.make("auth.response", body: auth, ts: timestamp))
        let authOK = try await receiveEnvelope(from: task)
        try acceptEnvelope(authOK)
        guard authOK.type == "auth.ok" else {
            throw RMCError.protocolError("Broker rejected authentication.")
        }
        _ = try authOK.bodyAs(AuthOKPayload.self)
        onConnectionChange(true)
        onLog("已连接 CRC broker 并通过设备认证")

        while !stopped && !Task.isCancelled {
            let envelope = try await receiveEnvelope(from: task)
            try acceptEnvelope(envelope)
            switch envelope.type {
            case "heartbeat.ping":
                let ping = try envelope.bodyAs(HeartbeatPayload.self)
                try await writer.send(try ProtocolEnvelope.make("heartbeat.pong", body: ping))
            case "heartbeat.pong":
                break
            case "session.ready":
                try establishSession(envelope)
            case "relay.data":
                try await handleRelay(envelope, writer: writer)
            case "session.closed":
                if let id = envelope.sessionId { channels.removeValue(forKey: id) }
            default:
                throw RMCError.protocolError("Unexpected broker message: \(envelope.type)")
            }
        }
    }

    private func establishSession(_ envelope: ProtocolEnvelope) throws {
        guard let sessionId = envelope.sessionId else {
            throw RMCError.protocolError("session.ready is missing sessionId.")
        }
        let ready = try envelope.bodyAs(SessionReadyPayload.self)
        guard ready.deviceId == config.deviceId, ready.expiresAt > nowMs(),
              let rootKey = decodeKey(config.e2eeKey) else {
            throw RMCError.protocolError("Invalid or expired session.ready.")
        }
        channels[sessionId] = SessionChannel(
            sessionId: sessionId,
            operatorId: ready.operatorId,
            deviceId: config.deviceId,
            expiresAt: ready.expiresAt,
            rootKey: rootKey
        )
        onLog("已建立端到端加密会话")
    }

    private func handleRelay(_ envelope: ProtocolEnvelope, writer: WebSocketWriter) async throws {
        guard let sessionId = envelope.sessionId, let channel = channels[sessionId] else {
            throw RMCError.protocolError("relay.data references an unknown session.")
        }
        let inner = try await channel.decrypt(envelope)
        guard inner.type == "command.execute",
              inner.issuedAt <= nowMs() + 60_000,
              inner.expiresAt >= nowMs() else {
            throw RMCError.protocolError("Rejected invalid or expired encrypted command.")
        }
        guard commandTasks[inner.requestId] == nil else {
            throw RMCError.protocolError("Duplicate command requestId.")
        }
        let command = try inner.bodyAs(CommandPayload.self)
        let requestId = inner.requestId
        let task = Task { [weak self] in
            guard let self else { return }
            await self.handleCommand(
                command,
                requestId: requestId,
                expiresAt: inner.expiresAt,
                channel: channel,
                writer: writer
            )
            self.commandTasks.removeValue(forKey: requestId)
        }
        commandTasks[requestId] = task
    }

    private func handleCommand(
        _ command: CommandPayload,
        requestId: String,
        expiresAt: Int64,
        channel: SessionChannel,
        writer: WebSocketWriter
    ) async {
        let startedAt = nowMs()
        do {
            let remaining = max(1, Int((expiresAt - startedAt) / 1000))
            let timeout = min(command.timeoutSeconds ?? config.commandTimeoutSeconds, remaining)
            let result: ExecutionResult
            if command.kind == "tool" {
                let toolName = command.toolName ?? ""
                result = await ToolRunner.run(
                    name: toolName,
                    args: command.toolArgs ?? [],
                    executor: executor,
                    timeoutSeconds: timeout
                )
                onLog("已执行受授权的工具请求")
            } else {
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
                    result = await executor.execute(
                        command: command.command ?? "",
                        runAsRoot: runAsRoot,
                        timeoutSeconds: timeout
                    )
                }
                onLog("已执行受授权的 \(runAsRoot ? "root" : "user") 请求")
            }
            try await sendOutput(result.stdout, stream: "stdout", requestId: requestId, channel: channel, writer: writer)
            try await sendOutput(result.stderr, stream: "stderr", requestId: requestId, channel: channel, writer: writer)
            let complete = InnerEnvelope(
                type: "command.complete",
                requestId: requestId,
                issuedAt: nowMs(),
                expiresAt: nowMs() + 60_000,
                body: CompletePayload(exitCode: result.exitCode, cancelled: result.cancelled, durationMs: result.durationMs)
            )
            try await writer.send(try await channel.encrypt(complete))
        } catch {
            let errorOutput = InnerEnvelope(
                type: "command.output",
                requestId: requestId,
                issuedAt: nowMs(),
                expiresAt: nowMs() + 60_000,
                body: OutputPayload(stream: "stderr", text: "Command failed.\n")
            )
            try? await writer.send(try await channel.encrypt(errorOutput))
            let complete = InnerEnvelope(
                type: "command.complete",
                requestId: requestId,
                issuedAt: nowMs(),
                expiresAt: nowMs() + 60_000,
                body: CompletePayload(exitCode: -1, cancelled: false, durationMs: nowMs() - startedAt)
            )
            try? await writer.send(try await channel.encrypt(complete))
        }
    }

    private func sendOutput(
        _ text: String,
        stream: String,
        requestId: String,
        channel: SessionChannel,
        writer: WebSocketWriter
    ) async throws {
        guard !text.isEmpty else { return }
        let chunkSize = 60_000
        var index = text.startIndex
        while index < text.endIndex {
            let end = text.index(index, offsetBy: chunkSize, limitedBy: text.endIndex) ?? text.endIndex
            let inner = InnerEnvelope(
                type: "command.output",
                requestId: requestId,
                issuedAt: nowMs(),
                expiresAt: nowMs() + 60_000,
                body: OutputPayload(stream: stream, text: String(text[index..<end]))
            )
            try await writer.send(try await channel.encrypt(inner))
            index = end
        }
    }

    private func receiveEnvelope(from task: URLSessionWebSocketTask) async throws -> ProtocolEnvelope {
        let message = try await task.receive()
        let data: Data
        switch message {
        case .string(let text):
            guard let encoded = text.data(using: .utf8) else {
                throw RMCError.protocolError("Received non-UTF8 text.")
            }
            data = encoded
        case .data(let value):
            data = value
        @unknown default:
            throw RMCError.protocolError("Received an unsupported WebSocket message.")
        }
        return try JSONDecoder.rmc.decode(ProtocolEnvelope.self, from: data)
    }

    private func acceptEnvelope(_ envelope: ProtocolEnvelope) throws {
        guard envelope.v == 2,
              !envelope.messageId.isEmpty,
              abs(nowMs() - envelope.ts) <= 300_000,
              !seenMessageIds.contains(envelope.messageId) else {
            throw RMCError.protocolError("Rejected invalid, stale, or replayed broker message.")
        }
        seenMessageIds.insert(envelope.messageId)
        seenMessageOrder.append(envelope.messageId)
        if seenMessageOrder.count > 10_000 {
            seenMessageIds.remove(seenMessageOrder.removeFirst())
        }
    }

    private func createAuthProof(brokerNonce: String, clientNonce: String, timestamp: Int64) throws -> String {
        guard let keyData = decodeKey(config.brokerAuthKey) else {
            throw RMCError.configuration("brokerAuthKey is not valid base64/base64url.")
        }
        let canonical = "crc-v2-auth\ndevice\n\(config.deviceId)\n\(config.keyId)\n\(brokerNonce)\n\(clientNonce)\n\(timestamp)"
        let digest = HMAC<SHA256>.authenticationCode(
            for: Data(canonical.utf8),
            using: SymmetricKey(data: keyData)
        )
        return Data(digest).base64URLEncodedString()
    }
}

private func decodeKey(_ value: String) -> Data? {
    let decoded = Data(base64URLEncoded: value) ?? Data(base64Encoded: value)
    guard let decoded, decoded.count == 32 else { return nil }
    return decoded
}

private func randomBase64URL(bytes: Int) -> String {
    var data = Data(count: bytes)
    data.withUnsafeMutableBytes { buffer in
        guard let address = buffer.baseAddress else { return }
        _ = SecRandomCopyBytes(kSecRandomDefault, bytes, address)
    }
    return data.base64URLEncodedString()
}

private extension Data {
    init?(base64URLEncoded value: String) {
        var normalized = value.replacingOccurrences(of: "-", with: "+")
            .replacingOccurrences(of: "_", with: "/")
        normalized += String(repeating: "=", count: (4 - normalized.count % 4) % 4)
        self.init(base64Encoded: normalized)
    }

    func base64URLEncodedString() -> String {
        base64EncodedString()
            .replacingOccurrences(of: "+", with: "-")
            .replacingOccurrences(of: "/", with: "_")
            .replacingOccurrences(of: "=", with: "")
    }
}
