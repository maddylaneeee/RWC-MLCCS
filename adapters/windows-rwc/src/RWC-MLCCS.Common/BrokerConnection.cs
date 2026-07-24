using System.Net.WebSockets;

namespace RWC_MLCCS.Common;

public static class BrokerConnection
{
    public static async Task<AuthOkBody> AuthenticateAsync(
        ClientWebSocket socket,
        SemaphoreSlim sendLock,
        string role,
        string principalId,
        string keyId,
        string authKey,
        object metadata,
        CancellationToken cancellationToken)
    {
        var challenge = await WebSocketJson.ReceiveAsync(socket, cancellationToken);
        if (challenge?.Type != "auth.challenge") throw new InvalidDataException("Broker did not send auth.challenge.");
        var brokerNonce = challenge.BodyAs<AuthChallengeBody>().Nonce;
        Base64Url.Decode(brokerNonce, 32);

        var clientNonce = BrokerCrypto.RandomId(32);
        var response = ProtocolEnvelope.Create("auth.response");
        var proof = BrokerCrypto.CreateAuthProof(
            authKey, role, principalId, keyId, brokerNonce, clientNonce, response.Ts);
        response.Body = System.Text.Json.JsonSerializer.SerializeToNode(
            new AuthResponseBody(role, principalId, keyId, clientNonce, proof, metadata),
            Serializer.Options);
        await WebSocketJson.SendAsync(socket, response, sendLock, cancellationToken);

        var accepted = await WebSocketJson.ReceiveAsync(socket, cancellationToken);
        if (accepted?.Type != "auth.ok") throw new InvalidDataException("Broker rejected authentication.");
        return accepted.BodyAs<AuthOkBody>();
    }

    public static async Task ReplyHeartbeatAsync(
        WebSocket socket,
        SemaphoreSlim sendLock,
        ProtocolEnvelope message,
        CancellationToken cancellationToken)
    {
        if (message.Type != "heartbeat.ping") return;
        var ping = message.BodyAs<HeartbeatBody>();
        await WebSocketJson.SendAsync(socket, ProtocolEnvelope.Create("heartbeat.pong", new HeartbeatBody(ping.Nonce)),
            sendLock, cancellationToken);
    }
}
