using RWC_MLCCS.Common;

namespace RWC_MLCCS.Tests;

public sealed class ConfigTests
{
    [Fact]
    public void ClientConfigCreatesDefaultFile()
    {
        var directory = Path.Combine(Path.GetTempPath(), "RWC_MLCCSTests", Guid.NewGuid().ToString("N"));
        var path = Path.Combine(directory, "config.json");

        var config = ClientConfig.LoadOrCreate(path);

        Assert.True(File.Exists(path));
        Assert.Equal("wss://your-server.example:7580/link", config.ServerUrl);
        Assert.False(config.AllowInvalidServerCertificate);
        Assert.True(config.AllowLocalPowerShellFallback);
    }

    [Fact]
    public void ServerConfigCreatesDefaultFile()
    {
        var directory = Path.Combine(Path.GetTempPath(), "RWC_MLCCSTests", Guid.NewGuid().ToString("N"));
        var path = Path.Combine(directory, "server.json");

        var config = ServerConfig.LoadOrCreate(path);

        Assert.True(File.Exists(path));
        Assert.Equal("0.0.0.0", config.ListenHost);
        Assert.Equal(7580, config.Port);
    }
}
