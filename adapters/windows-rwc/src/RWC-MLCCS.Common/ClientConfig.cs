using System.Text.Json;
using System.Text.Json.Serialization;

namespace RWC_MLCCS.Common;

public sealed class ClientConfig
{
    public string ServerUrl { get; set; } = BuildDefaults.ServerUrl;

    public string SharedSecret { get; set; } = "change-this-shared-secret";

    public string ClientId { get; set; } = Environment.MachineName;

    public bool AllowLocalPowerShellFallback { get; set; } = true;

    public bool AllowInvalidServerCertificate { get; set; } = false;

    public int ReconnectDelaySeconds { get; set; } = 5;

    public int CommandCancelGraceSeconds { get; set; } = 3;

    public string ConfigSourceUrl { get; set; } = AppPaths.DefaultClientConfigUrl;

    public static ClientConfig LoadOrCreate(string path)
    {
        if (!File.Exists(path))
        {
            var defaults = new ClientConfig();
            JsonConfig.Write(path, defaults);
            return defaults;
        }

        var loaded = JsonConfig.Read<ClientConfig>(path);
        if (string.IsNullOrWhiteSpace(loaded.ServerUrl))
        {
            loaded.ServerUrl = BuildDefaults.ServerUrl;
        }

        if (string.IsNullOrWhiteSpace(loaded.SharedSecret))
        {
            loaded.SharedSecret = "change-this-shared-secret";
        }

        if (string.IsNullOrWhiteSpace(loaded.ClientId))
        {
            loaded.ClientId = Environment.MachineName;
        }

        return loaded;
    }
}

public sealed class ServerConfig
{
    public string ListenHost { get; set; } = "0.0.0.0";

    public int Port { get; set; } = 7580;

    public string SharedSecret { get; set; } = "change-this-shared-secret";

    public string CertificatePath { get; set; } = "server-dev.pfx";

    public string CertificatePassword { get; set; } = "change-this-cert-password";

    public string[] CertificateDnsNames { get; set; } = [];

    public int CommandTimeoutSeconds { get; set; } = 600;

    public static ServerConfig LoadOrCreate(string path)
    {
        if (!File.Exists(path))
        {
            var config = new ServerConfig();
            JsonConfig.Write(path, config);
            return config;
        }

        var loaded = JsonConfig.Read<ServerConfig>(path);
        if (loaded.Port <= 0)
        {
            loaded.Port = 7580;
        }

        if (string.IsNullOrWhiteSpace(loaded.ListenHost))
        {
            loaded.ListenHost = "0.0.0.0";
        }

        if (string.IsNullOrWhiteSpace(loaded.SharedSecret))
        {
            loaded.SharedSecret = "change-this-shared-secret";
        }

        if (string.IsNullOrWhiteSpace(loaded.CertificatePath))
        {
            loaded.CertificatePath = "server-dev.pfx";
        }

        if (string.IsNullOrWhiteSpace(loaded.CertificatePassword))
        {
            loaded.CertificatePassword = "change-this-cert-password";
        }

        return loaded;
    }
}

public static class JsonConfig
{
    private static readonly JsonSerializerOptions Options = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
    };

    public static T Read<T>(string path)
    {
        var json = File.ReadAllText(path);
        return JsonSerializer.Deserialize<T>(json, Options)
               ?? throw new InvalidDataException($"Could not parse config: {path}");
    }

    public static void Write<T>(string path, T value)
    {
        var directory = Path.GetDirectoryName(path);
        if (!string.IsNullOrWhiteSpace(directory))
        {
            Directory.CreateDirectory(directory);
        }

        File.WriteAllText(path, JsonSerializer.Serialize(value, Options));
    }
}
