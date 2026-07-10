import Foundation

struct ClientConfig: Codable {
    var serverUrl: String
    var sharedSecret: String
    var clientId: String
    var allowInvalidServerCertificate: Bool
    var pinnedServerCertificateSha256: String
    var reconnectDelaySeconds: Int
    var commandTimeoutSeconds: Int
    var requireSudoBeforeConnect: Bool
    var allowRootCommands: Bool

    static func load() -> ClientConfig {
        let supportURL = appSupportDirectory().appendingPathComponent("config.json")
        if let bundledURL = Bundle.main.url(forResource: "config", withExtension: "json"),
           let bundled = try? load(from: bundledURL) {
            try? FileManager.default.createDirectory(at: appSupportDirectory(), withIntermediateDirectories: true)
            try? Data(contentsOf: bundledURL).write(to: supportURL, options: .atomic)
            return bundled.normalized()
        }

        if FileManager.default.fileExists(atPath: supportURL.path),
           let loaded = try? load(from: supportURL) {
            return loaded.normalized()
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
        if copy.clientId.trimmingCharacters(in: .whitespacesAndNewlines).isEmpty {
            let host = Host.current().localizedName ?? "mac"
            copy.clientId = "\(host)-\(NSUserName())"
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
            serverUrl: "wss://lixinchen.ca:5002/link",
            sharedSecret: "change-this-shared-secret",
            clientId: "",
            allowInvalidServerCertificate: false,
            pinnedServerCertificateSha256: "",
            reconnectDelaySeconds: 5,
            commandTimeoutSeconds: 600,
            requireSudoBeforeConnect: true,
            allowRootCommands: true
        )
    }
}
