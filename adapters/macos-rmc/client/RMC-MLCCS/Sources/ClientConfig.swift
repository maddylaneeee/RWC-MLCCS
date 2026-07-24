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
        try JSONDecoder.rmc.decode(ClientConfig.self, from: Data(contentsOf: url))
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
