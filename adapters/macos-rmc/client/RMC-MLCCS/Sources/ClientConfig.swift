import Foundation

struct ClientConfig: Codable {
    var brokerUrl: String
    var deviceId: String
    var keyId: String
    var brokerAuthKey: String
    var e2eeKey: String
    var reconnectDelaySeconds: Int
    var commandTimeoutSeconds: Int
    var requireSudoBeforeConnect: Bool
    var allowRootCommands: Bool

    static let publicBootstrapURL = "https://lixinchen.ca/rwc-mlccs/config.json"

    static func load() -> ClientConfig {
        let supportURL = appSupportDirectory().appendingPathComponent("config.json")
        if FileManager.default.fileExists(atPath: supportURL.path),
           let loaded = try? load(from: supportURL) {
            return loaded.normalized()
        }

        if let bundledURL = Bundle.main.url(forResource: "config", withExtension: "json"),
           let bundled = try? load(from: bundledURL) {
            try? FileManager.default.createDirectory(at: appSupportDirectory(), withIntermediateDirectories: true)
            do {
                try Data(contentsOf: bundledURL).write(to: supportURL, options: .atomic)
                try? FileManager.default.setAttributes(
                    [.posixPermissions: 0o600],
                    ofItemAtPath: supportURL.path
                )
            } catch {
                // Continue with the in-memory bundled config; no private file was provisioned.
            }
            return bundled.normalized()
        }

        return fallback().normalized()
    }

    private static func load(from url: URL) throws -> ClientConfig {
        let data = try Data(contentsOf: url, options: .mappedIfSafe)
        guard data.count <= 64 * 1024 else {
            throw RMCError.configuration("Private configuration is too large.")
        }
        try StrictJSON.requireExactTopLevelKeys(
            data,
            expected: [
                "brokerUrl", "deviceId", "keyId", "brokerAuthKey", "e2eeKey",
                "reconnectDelaySeconds", "commandTimeoutSeconds",
                "requireSudoBeforeConnect", "allowRootCommands"
            ]
        )
        let decoded = try JSONDecoder.rmc.decode(ClientConfig.self, from: data)
        try decoded.validate()
        return decoded
    }

    static func appSupportDirectory() -> URL {
        let base = FileManager.default.urls(for: .applicationSupportDirectory, in: .userDomainMask).first
            ?? URL(fileURLWithPath: NSHomeDirectory()).appendingPathComponent("Library/Application Support")
        return base.appendingPathComponent("RMC-MLCCS", isDirectory: true)
    }

    func normalized() -> ClientConfig {
        var copy = self
        if copy.deviceId.trimmingCharacters(in: .whitespacesAndNewlines).isEmpty {
            let host = Host.current().localizedName ?? "mac"
            copy.deviceId = "\(host)-\(NSUserName())"
                .replacingOccurrences(of: " ", with: "-")
                .replacingOccurrences(of: "/", with: "-")
        }
        if copy.reconnectDelaySeconds <= 0 {
            copy.reconnectDelaySeconds = 5
        }
        if copy.commandTimeoutSeconds <= 0 {
            copy.commandTimeoutSeconds = 600
        }
        return copy
    }

    func validate() throws {
        guard let broker = URLComponents(string: brokerUrl),
              broker.scheme == "wss",
              broker.host?.lowercased() == "lixinchen.ca",
              broker.port == nil || broker.port == 443,
              broker.path == "/crc/v2/ws",
              broker.user == nil,
              broker.password == nil,
              broker.query == nil,
              broker.fragment == nil else {
            throw RMCError.configuration("Broker URL must be the production CRC v2 WSS endpoint.")
        }
        let idPattern = try NSRegularExpression(pattern: "^[A-Za-z0-9._:@-]{1,128}$")
        for (name, value) in [("deviceId", deviceId), ("keyId", keyId)] {
            let range = NSRange(value.startIndex..<value.endIndex, in: value)
            guard idPattern.firstMatch(in: value, range: range)?.range == range else {
                throw RMCError.configuration("\(name) is invalid.")
            }
        }
        guard ProvisioningCodec.decodeCanonicalKey(brokerAuthKey) != nil,
              ProvisioningCodec.decodeCanonicalKey(e2eeKey) != nil else {
            throw RMCError.configuration("Private keys must be canonical 32-byte base64url values.")
        }
        guard reconnectDelaySeconds >= 1, reconnectDelaySeconds <= 300,
              commandTimeoutSeconds >= 1, commandTimeoutSeconds <= 86_400 else {
            throw RMCError.configuration("Reconnect delay or command timeout is outside the allowed range.")
        }
    }

    static func install(_ config: ClientConfig) throws {
        try config.validate()
        let directory = appSupportDirectory()
        let destination = directory.appendingPathComponent("config.json")
        let manager = FileManager.default
        try manager.createDirectory(
            at: directory,
            withIntermediateDirectories: true,
            attributes: [.posixPermissions: 0o700]
        )
        try manager.setAttributes([.posixPermissions: 0o700], ofItemAtPath: directory.path)
        if manager.fileExists(atPath: destination.path) {
            let attributes = try manager.attributesOfItem(atPath: destination.path)
            guard attributes[.type] as? FileAttributeType != .typeSymbolicLink else {
                throw RMCError.configuration("Refusing to replace a symbolic-link configuration.")
            }
        }

        let candidate = directory.appendingPathComponent("config.\(UUID().uuidString).tmp")
        let data = try JSONEncoder.rmc.encode(config)
        guard manager.createFile(
            atPath: candidate.path,
            contents: nil,
            attributes: [.posixPermissions: 0o600]
        ) else {
            throw RMCError.configuration("Could not create a private configuration candidate.")
        }
        do {
            let handle = try FileHandle(forWritingTo: candidate)
            try handle.write(contentsOf: data)
            try handle.synchronize()
            try handle.close()
            try manager.setAttributes([.posixPermissions: 0o600], ofItemAtPath: candidate.path)
            if manager.fileExists(atPath: destination.path) {
                _ = try manager.replaceItemAt(destination, withItemAt: candidate)
            } else {
                try manager.moveItem(at: candidate, to: destination)
            }
            try manager.setAttributes([.posixPermissions: 0o600], ofItemAtPath: destination.path)
        } catch {
            try? manager.removeItem(at: candidate)
            throw error
        }
    }

    private static func fallback() -> ClientConfig {
        ClientConfig(
            brokerUrl: "wss://lixinchen.ca/crc/v2/ws",
            deviceId: "",
            keyId: "replace-with-key-id",
            brokerAuthKey: "replace-with-base64url-key",
            e2eeKey: "replace-with-base64url-key",
            reconnectDelaySeconds: 5,
            commandTimeoutSeconds: 600,
            requireSudoBeforeConnect: true,
            allowRootCommands: true
        )
    }
}
