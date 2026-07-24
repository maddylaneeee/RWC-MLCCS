using RWC_MLCCS.Common;

namespace RWC_MLCCS.Tests;

public sealed class ConfigTests
{
    [Fact]
    public void ClientConfigCreatesBrokerV2DefaultsWithoutTlsBypass()
    {
        var path = NewPath("config.json");
        var config = ClientConfig.LoadOrCreate(path);

        Assert.True(File.Exists(path));
        Assert.Equal("wss://lixinchen.ca/crc/v2/ws", config.BrokerUrl);
        Assert.DoesNotContain("AllowInvalidServerCertificate", File.ReadAllText(path));
        if (!OperatingSystem.IsWindows())
        {
            Assert.Equal(
                UnixFileMode.UserRead | UnixFileMode.UserWrite,
                File.GetUnixFileMode(path) & (UnixFileMode)0x1FF);
        }
        Assert.ThrowsAny<Exception>(config.Validate);
    }

    [Fact]
    public void OperatorConfigCreatesRandomLoopbackBearerToken()
    {
        var path = NewPath("server.json");
        var first = ServerConfig.LoadOrCreate(path);
        var second = ServerConfig.LoadOrCreate(path);

        Assert.True(Base64Url.Decode(first.LoopbackApi.BearerToken).Length >= 32);
        Assert.Equal(first.LoopbackApi.BearerToken, second.LoopbackApi.BearerToken);
        Assert.DoesNotContain("listenHost", File.ReadAllText(path), StringComparison.OrdinalIgnoreCase);
    }

    private static string NewPath(string name) =>
        Path.Combine(Path.GetTempPath(), "RWC_MLCCSTests", Guid.NewGuid().ToString("N"), name);
}
