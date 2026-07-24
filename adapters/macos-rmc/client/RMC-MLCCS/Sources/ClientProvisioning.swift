import CryptoKit
import Foundation

private struct PublicBootstrapConfig: Decodable {
    let protocolVersion: Int
    let brokerUrl: String
    let provisioning: String
    let containsSecrets: Bool
    let privateConfigFormat: String
}

private struct EncryptedProvisioningEnvelope: Decodable {
    let format: String
    let algorithm: String
    let nonce: String
    let ciphertext: String
    let tag: String
}

enum ProvisioningCodec {
    static let format = "crc-device-provisioning-v1"
    private static let algorithm = "AES-256-GCM"
    private static let publicLimit = 16 * 1024
    private static let privateLimit = 64 * 1024

    static func loadLocalConfig(from url: URL) throws -> ClientConfig {
        guard url.isFileURL else {
            throw RMCError.configuration("Local private configuration must be a file.")
        }
        let attributes = try FileManager.default.attributesOfItem(atPath: url.path)
        guard attributes[.type] as? FileAttributeType == .typeRegular else {
            throw RMCError.configuration("Local private configuration must be a regular file.")
        }
        let data = try Data(contentsOf: url, options: .mappedIfSafe)
        return try decodePrivateConfig(data)
    }

    static func downloadAndDecrypt(
        publicURLText: String,
        privateURLText: String
    ) async throws -> ClientConfig {
        let publicURL = try allowedPublicURL(publicURLText)
        let privateURLWithFragment = try allowedPrivateURL(privateURLText)
        guard let fragment = URLComponents(
            url: privateURLWithFragment,
            resolvingAgainstBaseURL: false
        )?.fragment,
              fragment.hasPrefix("crc-key="),
              let contentKey = decodeCanonicalKey(String(fragment.dropFirst("crc-key=".count))) else {
            throw RMCError.configuration("Private configuration URL has an invalid crc-key fragment.")
        }
        var components = URLComponents(url: privateURLWithFragment, resolvingAgainstBaseURL: false)
        components?.fragment = nil
        guard let privateURL = components?.url else {
            throw RMCError.configuration("Private configuration URL is invalid.")
        }

        let sessionConfiguration = URLSessionConfiguration.ephemeral
        sessionConfiguration.urlCache = nil
        sessionConfiguration.requestCachePolicy = .reloadIgnoringLocalAndRemoteCacheData
        sessionConfiguration.httpCookieStorage = nil
        sessionConfiguration.httpShouldSetCookies = false
        sessionConfiguration.timeoutIntervalForRequest = 20
        sessionConfiguration.timeoutIntervalForResource = 20
        let delegate = NoRedirectSessionDelegate()
        let session = URLSession(
            configuration: sessionConfiguration,
            delegate: delegate,
            delegateQueue: nil
        )
        defer { session.invalidateAndCancel() }

        let publicData = try await download(
            publicURL,
            limit: publicLimit,
            requireJSON: true,
            session: session
        )
        let bootstrap = try decodePublicBootstrap(publicData)
        let privateData = try await download(
            privateURL,
            limit: privateLimit,
            requireJSON: false,
            session: session
        )
        var plaintext = try decryptEnvelope(privateData, keyData: contentKey)
        defer { plaintext.resetBytes(in: 0..<plaintext.count) }
        let config = try decodePrivateConfig(plaintext)
        guard config.brokerUrl == bootstrap.brokerUrl else {
            throw RMCError.configuration("Public and private broker URLs do not match.")
        }
        return config
    }

    static func decodeCanonicalKey(_ value: String) -> Data? {
        guard !value.isEmpty,
              value.range(of: "^[A-Za-z0-9_-]+$", options: .regularExpression) != nil,
              let decoded = Data(provisioningBase64URL: value),
              decoded.count == 32,
              decoded.provisioningBase64URLString() == value else {
            return nil
        }
        return decoded
    }

    private static func decodePublicBootstrap(_ data: Data) throws -> PublicBootstrapConfig {
        try StrictJSON.requireExactTopLevelKeys(
            data,
            expected: [
                "protocolVersion", "brokerUrl", "provisioning",
                "containsSecrets", "privateConfigFormat"
            ]
        )
        let value = try JSONDecoder.rmc.decode(PublicBootstrapConfig.self, from: data)
        guard value.protocolVersion == 2,
              !value.containsSecrets,
              value.provisioning == "local-or-encrypted-url-private-config",
              value.privateConfigFormat == format,
              value.brokerUrl == "wss://lixinchen.ca/crc/v2/ws" else {
            throw RMCError.configuration("Public configuration does not declare supported CRC v2 provisioning.")
        }
        return value
    }

    private static func decodePrivateConfig(_ data: Data) throws -> ClientConfig {
        guard data.count <= privateLimit else {
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
        let config = try JSONDecoder.rmc.decode(ClientConfig.self, from: data)
        try config.validate()
        return config
    }

    private static func decryptEnvelope(_ data: Data, keyData: Data) throws -> Data {
        try StrictJSON.requireExactTopLevelKeys(
            data,
            expected: ["format", "algorithm", "nonce", "ciphertext", "tag"]
        )
        let envelope = try JSONDecoder.rmc.decode(EncryptedProvisioningEnvelope.self, from: data)
        guard envelope.format == format, envelope.algorithm == algorithm,
              let nonce = Data(provisioningBase64URL: envelope.nonce),
              let ciphertext = Data(provisioningBase64URL: envelope.ciphertext),
              let tag = Data(provisioningBase64URL: envelope.tag),
              nonce.count == 12, !ciphertext.isEmpty,
              ciphertext.count <= privateLimit, tag.count == 16 else {
            throw RMCError.configuration("Encrypted private configuration envelope is invalid.")
        }
        do {
            let box = try AES.GCM.SealedBox(
                nonce: AES.GCM.Nonce(data: nonce),
                ciphertext: ciphertext,
                tag: tag
            )
            return try AES.GCM.open(
                box,
                using: SymmetricKey(data: keyData),
                authenticating: Data(format.utf8)
            )
        } catch {
            throw RMCError.configuration("Encrypted private configuration authentication failed.")
        }
    }

    private static func allowedPublicURL(_ value: String) throws -> URL {
        let url = try allowedHTTPSURL(value, allowFragment: false)
        guard url.path == "/rwc-mlccs/config.json" else {
            throw RMCError.configuration("Public configuration URL path is not allowed.")
        }
        return url
    }

    private static func allowedPrivateURL(_ value: String) throws -> URL {
        let url = try allowedHTTPSURL(value, allowFragment: true)
        guard url.path.hasPrefix("/tempfileshare/") else {
            throw RMCError.configuration("Private configuration must use a temporary FileShare URL.")
        }
        return url
    }

    private static func allowedHTTPSURL(_ value: String, allowFragment: Bool) throws -> URL {
        guard value == value.trimmingCharacters(in: .whitespacesAndNewlines),
              let components = URLComponents(string: value),
              components.scheme == "https",
              components.host?.lowercased() == "lixinchen.ca",
              components.port == nil || components.port == 443,
              components.user == nil,
              components.password == nil,
              components.query == nil,
              allowFragment || components.fragment == nil,
              let url = components.url else {
            throw RMCError.configuration("Configuration URL must be an allowed lixinchen.ca HTTPS URL.")
        }
        return url
    }

    private static func download(
        _ url: URL,
        limit: Int,
        requireJSON: Bool,
        session: URLSession
    ) async throws -> Data {
        var request = URLRequest(url: url)
        request.cachePolicy = .reloadIgnoringLocalAndRemoteCacheData
        request.setValue("no-cache, no-store", forHTTPHeaderField: "Cache-Control")
        request.setValue("application/json", forHTTPHeaderField: "Accept")
        let (bytes, response) = try await session.bytes(for: request)
        guard let http = response as? HTTPURLResponse,
              (200..<300).contains(http.statusCode),
              http.url == url else {
            throw RMCError.configuration("Configuration download failed.")
        }
        if requireJSON {
            let mediaType = http.value(forHTTPHeaderField: "Content-Type")?
                .split(separator: ";", maxSplits: 1).first?
                .trimmingCharacters(in: .whitespacesAndNewlines).lowercased() ?? ""
            guard mediaType == "application/json" || mediaType.hasSuffix("+json") else {
                throw RMCError.configuration("Public configuration response is not JSON.")
            }
        }
        if let lengthText = http.value(forHTTPHeaderField: "Content-Length"),
           let length = Int(lengthText), length > limit {
            throw RMCError.configuration("Configuration download is too large.")
        }
        var data = Data()
        data.reserveCapacity(min(limit, 16 * 1024))
        for try await byte in bytes {
            guard data.count < limit else {
                throw RMCError.configuration("Configuration download is too large.")
            }
            data.append(byte)
        }
        return data
    }
}

final class NoRedirectSessionDelegate: NSObject, URLSessionTaskDelegate {
    func urlSession(
        _ session: URLSession,
        task: URLSessionTask,
        willPerformHTTPRedirection response: HTTPURLResponse,
        newRequest request: URLRequest,
        completionHandler: @escaping (URLRequest?) -> Void
    ) {
        completionHandler(nil)
    }
}

enum StrictJSON {
    static func requireExactTopLevelKeys(_ data: Data, expected: Set<String>) throws {
        let keys = try topLevelKeys(data)
        guard keys == expected else {
            throw RMCError.configuration("Configuration contains missing, duplicate, or unknown fields.")
        }
    }

    private static func topLevelKeys(_ data: Data) throws -> Set<String> {
        let bytes = [UInt8](data)
        var index = 0
        func skipWhitespace() {
            while index < bytes.count, [9, 10, 13, 32].contains(bytes[index]) { index += 1 }
        }
        func parseString() throws -> String {
            guard index < bytes.count, bytes[index] == 34 else {
                throw RMCError.configuration("Configuration JSON is invalid.")
            }
            let start = index
            index += 1
            var escaped = false
            while index < bytes.count {
                let byte = bytes[index]
                index += 1
                if escaped {
                    escaped = false
                } else if byte == 92 {
                    escaped = true
                } else if byte == 34 {
                    let slice = Data(bytes[start..<index])
                    return try JSONDecoder.rmc.decode(String.self, from: slice)
                }
            }
            throw RMCError.configuration("Configuration JSON string is unterminated.")
        }
        func skipValue() throws {
            var nesting = 0
            var inString = false
            var escaped = false
            let start = index
            while index < bytes.count {
                let byte = bytes[index]
                if inString {
                    index += 1
                    if escaped { escaped = false }
                    else if byte == 92 { escaped = true }
                    else if byte == 34 { inString = false }
                    continue
                }
                if byte == 34 { inString = true; index += 1; continue }
                if byte == 123 || byte == 91 { nesting += 1; index += 1; continue }
                if byte == 125 || byte == 93 {
                    if nesting == 0 { break }
                    nesting -= 1
                    index += 1
                    continue
                }
                if byte == 44, nesting == 0 { break }
                index += 1
            }
            guard index > start, !inString, nesting == 0 else {
                throw RMCError.configuration("Configuration JSON value is invalid.")
            }
        }

        skipWhitespace()
        guard index < bytes.count, bytes[index] == 123 else {
            throw RMCError.configuration("Configuration JSON root must be an object.")
        }
        index += 1
        var keys = Set<String>()
        skipWhitespace()
        if index < bytes.count, bytes[index] == 125 {
            index += 1
        } else {
            while true {
                skipWhitespace()
                let key = try parseString()
                guard keys.insert(key).inserted else {
                    throw RMCError.configuration("Configuration contains a duplicate field.")
                }
                skipWhitespace()
                guard index < bytes.count, bytes[index] == 58 else {
                    throw RMCError.configuration("Configuration JSON is invalid.")
                }
                index += 1
                skipWhitespace()
                try skipValue()
                skipWhitespace()
                guard index < bytes.count else {
                    throw RMCError.configuration("Configuration JSON is incomplete.")
                }
                if bytes[index] == 125 { index += 1; break }
                guard bytes[index] == 44 else {
                    throw RMCError.configuration("Configuration JSON is invalid.")
                }
                index += 1
            }
        }
        skipWhitespace()
        guard index == bytes.count else {
            throw RMCError.configuration("Configuration JSON has trailing data.")
        }
        return keys
    }
}

private extension Data {
    init?(provisioningBase64URL value: String) {
        var normalized = value.replacingOccurrences(of: "-", with: "+")
            .replacingOccurrences(of: "_", with: "/")
        normalized += String(repeating: "=", count: (4 - normalized.count % 4) % 4)
        self.init(base64Encoded: normalized)
    }

    func provisioningBase64URLString() -> String {
        base64EncodedString()
            .replacingOccurrences(of: "+", with: "-")
            .replacingOccurrences(of: "/", with: "_")
            .replacingOccurrences(of: "=", with: "")
    }
}
