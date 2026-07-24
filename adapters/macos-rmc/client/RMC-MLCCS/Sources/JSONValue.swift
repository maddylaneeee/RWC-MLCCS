import Foundation

enum JSONValue: Codable, Equatable {
    case string(String)
    case number(Double)
    case bool(Bool)
    case object([String: JSONValue])
    case array([JSONValue])
    case null

    init(from decoder: Decoder) throws {
        let container = try decoder.singleValueContainer()
        if container.decodeNil() {
            self = .null
        } else if let value = try? container.decode(Bool.self) {
            self = .bool(value)
        } else if let value = try? container.decode(Double.self) {
            self = .number(value)
        } else if let value = try? container.decode(String.self) {
            self = .string(value)
        } else if let value = try? container.decode([String: JSONValue].self) {
            self = .object(value)
        } else if let value = try? container.decode([JSONValue].self) {
            self = .array(value)
        } else {
            throw DecodingError.dataCorruptedError(in: container, debugDescription: "Unsupported JSON value")
        }
    }

    func encode(to encoder: Encoder) throws {
        var container = encoder.singleValueContainer()
        switch self {
        case .string(let value):
            try container.encode(value)
        case .number(let value):
            try container.encode(value)
        case .bool(let value):
            try container.encode(value)
        case .object(let value):
            try container.encode(value)
        case .array(let value):
            try container.encode(value)
        case .null:
            try container.encodeNil()
        }
    }
}

struct ProtocolEnvelope: Codable {
    var v: Int
    var type: String
    var messageId: String
    var ts: Int64
    var sessionId: String? = nil
    var seq: String? = nil
    var from: String? = nil
    var to: String? = nil
    var body: JSONValue? = nil

    static func make(_ type: String) -> ProtocolEnvelope {
        ProtocolEnvelope(v: 2, type: type, messageId: UUID().uuidString.lowercased(), ts: nowMs(), body: nil)
    }

    static func make<T: Encodable>(_ type: String, body: T, ts: Int64? = nil) throws -> ProtocolEnvelope {
        let data = try JSONEncoder.rmc.encode(body)
        let value = try JSONDecoder.rmc.decode(JSONValue.self, from: data)
        return ProtocolEnvelope(
            v: 2,
            type: type,
            messageId: UUID().uuidString.lowercased(),
            ts: ts ?? nowMs(),
            body: value
        )
    }

    func bodyAs<T: Decodable>(_ type: T.Type) throws -> T {
        guard let body else {
            throw RMCError.protocolError("Message '\(self.type)' has no body.")
        }
        let data = try JSONEncoder.rmc.encode(body)
        return try JSONDecoder.rmc.decode(T.self, from: data)
    }
}

struct ChallengePayload: Codable {
    var nonce: String
}

struct AuthPayload: Codable {
    var role: String
    var principalId: String
    var keyId: String
    var clientNonce: String
    var proof: String
    var metadata: DeviceMetadata
}

struct DeviceMetadata: Codable {
    var userName: String
    var machineName: String
    var platform: String
    var appVersion: String
    var adapter: String
}

struct AuthOKPayload: Codable {
    var connectionId: String
    var heartbeatSeconds: Int
    var serverTime: Int64
}

struct SessionReadyPayload: Codable {
    var operatorId: String
    var deviceId: String
    var expiresAt: Int64
}

struct RelayBody: Codable {
    var nonce: String
    var ciphertext: String
    var tag: String
}

struct HeartbeatPayload: Codable {
    var nonce: String
}

struct CommandPayload: Codable {
    var kind: String?
    var command: String?
    var runAsRoot: Bool?
    var timeoutSeconds: Int?
    var toolName: String?
    var toolArgs: [String]?
}

struct OutputPayload: Codable {
    var stream: String
    var text: String
}

struct CompletePayload: Codable {
    var exitCode: Int
    var cancelled: Bool
    var durationMs: Int64
}

struct InnerEnvelope<T: Codable>: Codable {
    var type: String
    var requestId: String
    var issuedAt: Int64
    var expiresAt: Int64
    var body: T
}

struct InnerRawEnvelope: Codable {
    var type: String
    var requestId: String
    var issuedAt: Int64
    var expiresAt: Int64
    var body: JSONValue

    func bodyAs<T: Decodable>(_ type: T.Type) throws -> T {
        let data = try JSONEncoder.rmc.encode(body)
        return try JSONDecoder.rmc.decode(T.self, from: data)
    }
}

func nowMs() -> Int64 {
    Int64(Date().timeIntervalSince1970 * 1000)
}

enum RMCError: Error, LocalizedError {
    case protocolError(String)
    case configuration(String)
    case execution(String)

    var errorDescription: String? {
        switch self {
        case .protocolError(let message), .configuration(let message), .execution(let message):
            return message
        }
    }
}

extension JSONEncoder {
    static let rmc: JSONEncoder = {
        let encoder = JSONEncoder()
        encoder.outputFormatting = []
        return encoder
    }()
}

extension JSONDecoder {
    static let rmc = JSONDecoder()
}
