using System.Globalization;
using System.Net.WebSockets;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace RWC_MLCCS.Common;

public sealed class ProtocolEnvelope
{
    public int V { get; set; } = 2;
    public string Type { get; set; } = "";
    public string MessageId { get; set; } = "";
    public long Ts { get; set; }
    public string? SessionId { get; set; }
    public string? Seq { get; set; }
    public string? From { get; set; }
    public string? To { get; set; }
    public JsonNode? Body { get; set; }

    public static ProtocolEnvelope Create(string type, object? body = null) => new()
    {
        Type = type,
        MessageId = BrokerCrypto.RandomId(),
        Ts = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
        Body = body is null ? null : JsonSerializer.SerializeToNode(body, Serializer.Options)
    };

    public T BodyAs<T>()
    {
        if (Body is null) throw new InvalidDataException($"Message '{Type}' has no body.");
        var value = Body.Deserialize<T>(Serializer.Options);
        return value is null
            ? throw new InvalidDataException($"Message '{Type}' has no valid body.")
            : value;
    }
}

public sealed record AuthChallengeBody(string Nonce);
public sealed record AuthResponseBody(
    string Role,
    string PrincipalId,
    string KeyId,
    string ClientNonce,
    string Proof,
    object Metadata);
public sealed record AuthOkBody(string ConnectionId, int HeartbeatSeconds, long ServerTime);
public sealed record SessionOpenBody(string DeviceId);
public sealed record SessionReadyBody(string OperatorId, string DeviceId, long ExpiresAt);
public sealed record PresenceDevice(string DeviceId, JsonNode? Metadata, long ConnectedAt);
public sealed record PresenceSnapshotBody(PresenceDevice[] Devices);
public sealed record RelayCipherBody(string Nonce, string Ciphertext, string Tag);
public sealed record HeartbeatBody(string Nonce);

public sealed class InnerMessage
{
    public string Type { get; set; } = "";
    public string RequestId { get; set; } = "";
    public long IssuedAt { get; set; }
    public long ExpiresAt { get; set; }
    public JsonNode? Body { get; set; }

    public static InnerMessage Create(string type, string requestId, object body, TimeSpan lifetime) => new()
    {
        Type = type,
        RequestId = requestId,
        IssuedAt = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
        ExpiresAt = DateTimeOffset.UtcNow.Add(lifetime).ToUnixTimeMilliseconds(),
        Body = JsonSerializer.SerializeToNode(body, Serializer.Options)
    };

    public T BodyAs<T>()
    {
        if (Body is null) throw new InvalidDataException($"Inner message '{Type}' has no body.");
        var value = Body.Deserialize<T>(Serializer.Options);
        return value is null
            ? throw new InvalidDataException($"Inner message '{Type}' has no valid body.")
            : value;
    }
}

public sealed record CommandPayload(string Command, int TimeoutSeconds);
public sealed record OutputPayload(string Stream, string Text);
public sealed record CompletePayload(int ExitCode, bool Cancelled, bool UsedWinRm, long DurationMs);

public static class Serializer
{
    public static readonly JsonSerializerOptions Options = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = false
    };
}

public static class BrokerCrypto
{
    private const int KeyBytes = 32;
    private const int NonceBytes = 12;
    public const int MaxMessageBytes = 1_100_000;
    public const int MaxPlaintextBytes = 1_048_000;

    public static string RandomId(int bytes = 18) => Base64Url.Encode(RandomNumberGenerator.GetBytes(bytes));

    public static string CreateAuthProof(
        string authKey,
        string role,
        string principalId,
        string keyId,
        string brokerNonce,
        string clientNonce,
        long ts)
    {
        var canonical = string.Join('\n', "crc-v2-auth", role, principalId, keyId, brokerNonce, clientNonce,
            ts.ToString(CultureInfo.InvariantCulture));
        using var hmac = new HMACSHA256(Base64Url.Decode(authKey, KeyBytes));
        return Base64Url.Encode(hmac.ComputeHash(Encoding.UTF8.GetBytes(canonical)));
    }

    public static byte[] DeriveSessionKey(string e2eeKey, string sessionId, string operatorId, string deviceId)
    {
        var inputKey = Base64Url.Decode(e2eeKey, KeyBytes);
        return HKDF.DeriveKey(
            HashAlgorithmName.SHA256,
            inputKey,
            KeyBytes,
            Encoding.UTF8.GetBytes(sessionId),
            Encoding.UTF8.GetBytes($"crc-v2-e2ee|{operatorId}|{deviceId}"));
    }

    public static RelayCipherBody Encrypt(
        byte[] key,
        InnerMessage inner,
        string sessionId,
        string from,
        string to,
        string seq,
        string messageId,
        long ts)
    {
        var plaintext = JsonSerializer.SerializeToUtf8Bytes(inner, Serializer.Options);
        if (plaintext.Length > MaxPlaintextBytes) throw new InvalidDataException("Encrypted message exceeds size limit.");
        var nonce = RandomNumberGenerator.GetBytes(NonceBytes);
        var ciphertext = new byte[plaintext.Length];
        var tag = new byte[16];
        using var aes = new AesGcm(key, 16);
        aes.Encrypt(nonce, plaintext, ciphertext, tag, Aad(sessionId, from, to, seq, messageId, ts));
        return new RelayCipherBody(Base64Url.Encode(nonce), Base64Url.Encode(ciphertext), Base64Url.Encode(tag));
    }

    public static InnerMessage Decrypt(byte[] key, ProtocolEnvelope relay)
    {
        if (relay.SessionId is null || relay.Seq is null || relay.From is null || relay.To is null)
            throw new InvalidDataException("Relay metadata is incomplete.");
        var body = relay.BodyAs<RelayCipherBody>();
        var nonce = Base64Url.Decode(body.Nonce, NonceBytes);
        var ciphertext = Base64Url.Decode(body.Ciphertext);
        if (ciphertext.Length > MaxPlaintextBytes) throw new InvalidDataException("Ciphertext exceeds size limit.");
        var tag = Base64Url.Decode(body.Tag, 16);
        var plaintext = new byte[ciphertext.Length];
        using var aes = new AesGcm(key, 16);
        aes.Decrypt(nonce, ciphertext, tag, plaintext,
            Aad(relay.SessionId, relay.From, relay.To, relay.Seq, relay.MessageId, relay.Ts));
        return JsonSerializer.Deserialize<InnerMessage>(plaintext, Serializer.Options)
            ?? throw new InvalidDataException("Encrypted message is invalid.");
    }

    private static byte[] Aad(string sessionId, string from, string to, string seq, string messageId, long ts) =>
        Encoding.UTF8.GetBytes(string.Join('\n', "crc-v2-aad", sessionId, from, to, seq, messageId,
            ts.ToString(CultureInfo.InvariantCulture)));
}

public static class Base64Url
{
    public static string Encode(ReadOnlySpan<byte> value) =>
        Convert.ToBase64String(value).TrimEnd('=').Replace('+', '-').Replace('/', '_');

    public static byte[] Decode(string value, int? expectedLength = null)
    {
        if (string.IsNullOrEmpty(value) || value.Any(c => !(char.IsAsciiLetterOrDigit(c) || c is '-' or '_')))
            throw new FormatException("Invalid base64url.");
        var padded = value.Replace('-', '+').Replace('_', '/');
        padded += new string('=', (4 - padded.Length % 4) % 4);
        var bytes = Convert.FromBase64String(padded);
        if (Encode(bytes) != value) throw new FormatException("Non-canonical base64url.");
        if (expectedLength is not null && bytes.Length != expectedLength)
            throw new FormatException($"Expected {expectedLength} bytes.");
        return bytes;
    }
}

public static class WebSocketJson
{
    public static async Task SendAsync(
        WebSocket socket,
        ProtocolEnvelope envelope,
        SemaphoreSlim sendLock,
        CancellationToken cancellationToken)
    {
        var bytes = JsonSerializer.SerializeToUtf8Bytes(envelope, Serializer.Options);
        if (bytes.Length > BrokerCrypto.MaxMessageBytes) throw new InvalidDataException("Message exceeds size limit.");
        await sendLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await socket.SendAsync(bytes, WebSocketMessageType.Text, true, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            sendLock.Release();
        }
    }

    public static async Task<ProtocolEnvelope?> ReceiveAsync(WebSocket socket, CancellationToken cancellationToken)
    {
        var buffer = new byte[16 * 1024];
        using var stream = new MemoryStream();
        while (true)
        {
            var result = await socket.ReceiveAsync(buffer, cancellationToken).ConfigureAwait(false);
            if (result.MessageType == WebSocketMessageType.Close) return null;
            if (result.MessageType != WebSocketMessageType.Text) throw new InvalidDataException("Binary frames are not allowed.");
            if (stream.Length + result.Count > BrokerCrypto.MaxMessageBytes)
                throw new InvalidDataException("Message exceeds size limit.");
            stream.Write(buffer, 0, result.Count);
            if (result.EndOfMessage) break;
        }

        var envelope = JsonSerializer.Deserialize<ProtocolEnvelope>(stream.ToArray(), Serializer.Options)
            ?? throw new InvalidDataException("Message is invalid.");
        if (envelope.V != 2 || string.IsNullOrWhiteSpace(envelope.Type) ||
            string.IsNullOrWhiteSpace(envelope.MessageId) || envelope.Ts <= 0)
            throw new InvalidDataException("Invalid CRC v2 envelope.");
        return envelope;
    }
}
