using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace RWC_MLCCS.Common;

public sealed class ProtocolEnvelope
{
    public string Type { get; set; } = "";

    public string? RequestId { get; set; }

    public JsonNode? Payload { get; set; }

    public static ProtocolEnvelope Create(string type, string? requestId = null, object? payload = null)
    {
        return new ProtocolEnvelope
        {
            Type = type,
            RequestId = requestId,
            Payload = payload is null ? null : JsonSerializer.SerializeToNode(payload, Serializer.Options)
        };
    }

    public T PayloadAs<T>()
    {
        if (Payload is null)
        {
            throw new InvalidDataException($"Message '{Type}' has no payload.");
        }

        return Payload.Deserialize<T>(Serializer.Options)
               ?? throw new InvalidDataException($"Message '{Type}' payload could not be parsed.");
    }
}

public sealed record ChallengePayload(string Nonce);

public sealed record AuthPayload(string ClientId, string UserName, string MachineName, string Response);

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

public static class WebSocketJson
{
    public static async Task SendAsync(WebSocket socket, ProtocolEnvelope envelope, CancellationToken cancellationToken)
    {
        var json = JsonSerializer.Serialize(envelope, Serializer.Options);
        var bytes = Encoding.UTF8.GetBytes(json);
        await socket.SendAsync(bytes, WebSocketMessageType.Text, true, cancellationToken).ConfigureAwait(false);
    }

    public static async Task<ProtocolEnvelope?> ReceiveAsync(WebSocket socket, CancellationToken cancellationToken)
    {
        var buffer = new byte[8192];
        using var stream = new MemoryStream();

        while (true)
        {
            var result = await socket.ReceiveAsync(buffer, cancellationToken).ConfigureAwait(false);
            if (result.MessageType == WebSocketMessageType.Close)
            {
                return null;
            }

            stream.Write(buffer, 0, result.Count);
            if (result.EndOfMessage)
            {
                break;
            }
        }

        var json = Encoding.UTF8.GetString(stream.ToArray());
        return JsonSerializer.Deserialize<ProtocolEnvelope>(json, Serializer.Options);
    }
}
