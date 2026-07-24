using System.Text.Json;
using System.Text.Json.Serialization;
using System.Diagnostics;
using System.Security.Principal;

namespace RWC_MLCCS.Common;

public sealed class ClientConfig
{
    public string BrokerUrl { get; set; } = BuildDefaults.ServerUrl;
    public string DeviceId { get; set; } = Environment.MachineName;
    public string KeyId { get; set; } = "device-key-1";
    public string BrokerAuthKey { get; set; } = "replace-with-32-byte-base64url-key";
    public string E2eeKey { get; set; } = "replace-with-32-byte-base64url-key";
    public bool AllowLocalPowerShellFallback { get; set; } = true;
    public int ReconnectDelaySeconds { get; set; } = 2;
    public int MaxReconnectDelaySeconds { get; set; } = 60;
    public int CommandCancelGraceSeconds { get; set; } = 3;
    public static ClientConfig LoadOrCreate(string path) => JsonConfig.LoadOrCreate(path, new ClientConfig());

    public void Validate()
    {
        if (!Uri.TryCreate(BrokerUrl, UriKind.Absolute, out var uri) || uri.Scheme != "wss")
            throw new InvalidDataException("brokerUrl must be an absolute wss:// URL.");
        if (string.IsNullOrWhiteSpace(DeviceId) || string.IsNullOrWhiteSpace(KeyId))
            throw new InvalidDataException("deviceId and keyId are required.");
        Base64Url.Decode(BrokerAuthKey, 32);
        Base64Url.Decode(E2eeKey, 32);
    }
}

public sealed class OperatorDeviceConfig
{
    public string E2eeKey { get; set; } = "replace-with-32-byte-base64url-key";
}

public sealed class LoopbackApiConfig
{
    public bool Enabled { get; set; }
    public int Port { get; set; } = 7581;
    public string BearerToken { get; set; } = "";
}

public sealed class ServerConfig
{
    public string BrokerUrl { get; set; } = BuildDefaults.ServerUrl;
    public string OperatorId { get; set; } = Environment.MachineName + "-operator";
    public string KeyId { get; set; } = "operator-key-1";
    public string BrokerAuthKey { get; set; } = "replace-with-32-byte-base64url-key";
    public Dictionary<string, OperatorDeviceConfig> Devices { get; set; } = new(StringComparer.OrdinalIgnoreCase);
    public int CommandTimeoutSeconds { get; set; } = 600;
    public int ReconnectDelaySeconds { get; set; } = 2;
    public int MaxReconnectDelaySeconds { get; set; } = 60;
    public LoopbackApiConfig LoopbackApi { get; set; } = new();

    public static ServerConfig LoadOrCreate(string path)
    {
        var existed = File.Exists(path);
        var config = JsonConfig.LoadOrCreate(path, new ServerConfig());
        if (string.IsNullOrWhiteSpace(config.LoopbackApi.BearerToken))
        {
            config.LoopbackApi.BearerToken = BrokerCrypto.RandomId(32);
            JsonConfig.Write(path, config);
        }
        else if (!existed)
        {
            JsonConfig.Write(path, config);
        }
        return config;
    }

    public void Validate()
    {
        if (!Uri.TryCreate(BrokerUrl, UriKind.Absolute, out var uri) || uri.Scheme != "wss")
            throw new InvalidDataException("brokerUrl must be an absolute wss:// URL.");
        if (string.IsNullOrWhiteSpace(OperatorId) || string.IsNullOrWhiteSpace(KeyId))
            throw new InvalidDataException("operatorId and keyId are required.");
        Base64Url.Decode(BrokerAuthKey, 32);
        foreach (var device in Devices)
        {
            if (string.IsNullOrWhiteSpace(device.Key)) throw new InvalidDataException("Device id is required.");
            Base64Url.Decode(device.Value.E2eeKey, 32);
        }
        if (LoopbackApi.Enabled && (LoopbackApi.Port is < 1 or > 65535 ||
                                    Base64Url.Decode(LoopbackApi.BearerToken).Length < 32))
            throw new InvalidDataException("Invalid loopback API configuration.");
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

    public static T LoadOrCreate<T>(string path, T defaults)
    {
        if (!File.Exists(path))
        {
            Write(path, defaults);
            return defaults;
        }
        RestrictPrivateFile(path);
        return Read<T>(path);
    }

    public static T Read<T>(string path) => JsonSerializer.Deserialize<T>(File.ReadAllText(path), Options)
        ?? throw new InvalidDataException($"Could not parse config: {path}");

    public static void Write<T>(string path, T value)
    {
        var directory = Path.GetDirectoryName(path);
        if (!string.IsNullOrWhiteSpace(directory)) Directory.CreateDirectory(directory);
        var candidate = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            File.WriteAllText(candidate, JsonSerializer.Serialize(value, Options));
            RestrictPrivateFile(candidate);
            File.Move(candidate, path, overwrite: true);
            RestrictPrivateFile(path);
        }
        finally
        {
            if (File.Exists(candidate)) File.Delete(candidate);
        }
    }

    public static void RestrictPrivateFile(string path)
    {
        if (!OperatingSystem.IsWindows())
        {
            File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite);
            return;
        }

        var currentSid = WindowsIdentity.GetCurrent().User?.Value
            ?? throw new InvalidOperationException("Unable to resolve the current Windows identity.");
        using var process = Process.Start(new ProcessStartInfo
        {
            FileName = Path.Combine(Environment.SystemDirectory, "icacls.exe"),
            UseShellExecute = false,
            CreateNoWindow = true,
            ArgumentList =
            {
                path,
                "/inheritance:r",
                "/grant:r",
                $"*{currentSid}:F",
                "*S-1-5-18:F",
                "*S-1-5-32-544:F"
            }
        }) ?? throw new InvalidOperationException("Unable to start icacls.");
        process.WaitForExit();
        if (process.ExitCode != 0)
            throw new InvalidOperationException("Unable to restrict private configuration ACL.");
    }
}
