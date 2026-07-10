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
    var type: String
    var requestId: String?
    var payload: JSONValue?

    static func make(_ type: String, requestId: String? = nil) -> ProtocolEnvelope {
        ProtocolEnvelope(type: type, requestId: requestId, payload: nil)
    }

    static func make<T: Encodable>(_ type: String, requestId: String? = nil, payload: T) throws -> ProtocolEnvelope {
        let data = try JSONEncoder.rmc.encode(payload)
        let value = try JSONDecoder.rmc.decode(JSONValue.self, from: data)
        return ProtocolEnvelope(type: type, requestId: requestId, payload: value)
    }

    func payloadAs<T: Decodable>(_ type: T.Type) throws -> T {
        guard let payload else {
            throw RMCError.protocolError("Message '\(self.type)' has no payload.")
        }
        let data = try JSONEncoder.rmc.encode(payload)
        return try JSONDecoder.rmc.decode(T.self, from: data)
    }
}

struct ChallengePayload: Codable {
    var nonce: String
}

struct AuthPayload: Codable {
    var clientId: String
    var userName: String
    var machineName: String
    var platform: String
    var appVersion: String
    var response: String
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
