using System.Text.Json;
using RWC_MLCCS.Common;

namespace RWC_MLCCS.Tests;

public sealed class ProtocolTests
{
    [Fact]
    public void EnvelopeRoundTripsTypedPayload()
    {
        var envelope = ProtocolEnvelope.Create("command", "req-1", new CommandPayload("hostname", 30));
        var json = JsonSerializer.Serialize(envelope, Serializer.Options);
        var parsed = JsonSerializer.Deserialize<ProtocolEnvelope>(json, Serializer.Options);

        Assert.NotNull(parsed);
        Assert.Equal("command", parsed!.Type);
        Assert.Equal("req-1", parsed.RequestId);
        Assert.Equal("hostname", parsed.PayloadAs<CommandPayload>().Command);
    }

    [Fact]
    public void WinRmWrapperUsesLocalhostInvokeCommand()
    {
        var script = PowerShellScripts.WrapForLocalWinRm("hostname");

        Assert.Contains("Invoke-Command -ComputerName localhost", script);
        Assert.Contains("hostname", script);
    }
}
