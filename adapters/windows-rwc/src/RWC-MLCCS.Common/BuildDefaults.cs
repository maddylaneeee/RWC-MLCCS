using System.Reflection;

namespace RWC_MLCCS.Common;

public static class BuildDefaults
{
    public static string ServerUrl => GetMetadata(
        "RwcDefaultServerUrl",
        "wss://lixinchen.ca/crc/v2/ws");

    private static string GetMetadata(string key, string fallback)
    {
        var value = typeof(BuildDefaults).Assembly
            .GetCustomAttributes<AssemblyMetadataAttribute>()
            .FirstOrDefault(attribute => string.Equals(attribute.Key, key, StringComparison.Ordinal))?
            .Value;

        return string.IsNullOrWhiteSpace(value) ? fallback : value;
    }
}
