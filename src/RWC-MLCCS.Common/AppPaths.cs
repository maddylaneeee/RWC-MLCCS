namespace RWC_MLCCS.Common;

public static class AppPaths
{
    public const string ProductName = "RWC-MLCCS";

    public const string DefaultClientConfigUrl = "https://lixinchen.ca/rwc-mlccs/config.json";

    public static string BaseDirectory => AppContext.BaseDirectory;

    public static string ClientDataDirectory
    {
        get
        {
            var path = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData),
                ProductName);
            Directory.CreateDirectory(path);
            return path;
        }
    }

    public static string DefaultClientConfigPath => Path.Combine(ClientDataDirectory, "config.json");

    public static string PolicyAcceptedPath => Path.Combine(ClientDataDirectory, "policy.accepted");

    public static string DefaultServerConfigPath => Path.Combine(BaseDirectory, "server.json");

    public static string LogDirectory
    {
        get
        {
            var path = Path.Combine(BaseDirectory, "logs");
            Directory.CreateDirectory(path);
            return path;
        }
    }
}
