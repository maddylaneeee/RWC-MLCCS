using System.Text.Json;
using System.Security.Cryptography;
using RWC_MLCCS.Common;

namespace RWC_MLCCS.Tests;

public sealed class ProtocolTests
{
    private const string TestKey = "AAECAwQFBgcICQoLDA0ODxAREhMUFRYXGBkaGxwdHh8";

    [Fact]
    public void EnvelopeRoundTripsV2Body()
    {
        var envelope = ProtocolEnvelope.Create("session.open", new SessionOpenBody("device-1"));
        var json = JsonSerializer.Serialize(envelope, Serializer.Options);
        var parsed = JsonSerializer.Deserialize<ProtocolEnvelope>(json, Serializer.Options);

        Assert.NotNull(parsed);
        Assert.Equal(2, parsed!.V);
        Assert.Equal("session.open", parsed.Type);
        Assert.Equal("device-1", parsed.BodyAs<SessionOpenBody>().DeviceId);
    }

    [Fact]
    public void SessionKeyMatchesNodeInteropVector()
    {
        var key = BrokerCrypto.DeriveSessionKey(
            TestKey, "session-vector-001", "operator-alpha", "device-mac-001");

        Assert.Equal("mhcv9dBfh7aY68wBUDn30sgLa7H6yrHosbXg1NLfOGQ", Base64Url.Encode(key));
    }

    [Fact]
    public void EncryptedInnerMessageRoundTripsAndAuthenticatesMetadata()
    {
        var key = BrokerCrypto.DeriveSessionKey(TestKey, "session-1", "operator-1", "device-1");
        var inner = InnerMessage.Create("command.execute", "request-1", new CommandPayload("hostname", 30),
            TimeSpan.FromMinutes(1));
        var relay = ProtocolEnvelope.Create("relay.data");
        relay.SessionId = "session-1";
        relay.Seq = "1";
        relay.From = "operator:operator-1";
        relay.To = "device:device-1";
        relay.Body = JsonSerializer.SerializeToNode(
            BrokerCrypto.Encrypt(key, inner, relay.SessionId, relay.From, relay.To, relay.Seq, relay.MessageId, relay.Ts),
            Serializer.Options);

        var decrypted = BrokerCrypto.Decrypt(key, relay);
        Assert.Equal("hostname", decrypted.BodyAs<CommandPayload>().Command);

        relay.Seq = "2";
        Assert.ThrowsAny<CryptographicException>(() => BrokerCrypto.Decrypt(key, relay));
    }

    [Fact]
    public void WinRmWrapperUsesLocalhostInvokeCommand()
    {
        var script = PowerShellScripts.WrapForLocalWinRm("hostname");
        Assert.Contains("Invoke-Command -ComputerName localhost", script);
    }
}
