using System.Net;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace RWC_MLCCS.Common;

public sealed class ProvisioningException : Exception
{
    public ProvisioningException(string code, string message) : base(message) => Code = code;
    public string Code { get; }
}

public sealed class PublicBootstrapConfig
{
    public int ProtocolVersion { get; set; }
    public string BrokerUrl { get; set; } = "";
    public string Provisioning { get; set; } = "";
    public bool ContainsSecrets { get; set; }
    public string PrivateConfigFormat { get; set; } = "";
}

public sealed class EncryptedProvisioningEnvelope
{
    public string Format { get; set; } = "";
    public string Algorithm { get; set; } = "";
    public string Nonce { get; set; } = "";
    public string Ciphertext { get; set; } = "";
    public string Tag { get; set; } = "";
}

public static class ClientProvisioning
{
    public const string PublicBootstrapUrl = "https://lixinchen.ca/rwc-mlccs/config.json";
    public const string EnvelopeFormat = "crc-device-provisioning-v1";
    public const string EnvelopeAlgorithm = "AES-256-GCM";
    private const int PublicLimitBytes = 16 * 1024;
    private const int PrivateLimitBytes = 64 * 1024;
    private static readonly byte[] ProvisioningAad = Encoding.UTF8.GetBytes(EnvelopeFormat);
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = false,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase
    };

    public static HttpClient CreateHttpClient()
    {
        var handler = new HttpClientHandler
        {
            AllowAutoRedirect = false,
            UseCookies = false,
            AutomaticDecompression = DecompressionMethods.None
        };
        return new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(20) };
    }

    public static async Task<ClientConfig> DownloadAndDecryptAsync(
        string publicConfigUrl,
        string privateConfigUrl,
        HttpClient httpClient,
        CancellationToken cancellationToken)
    {
        var publicUri = ParseAllowedUri(publicConfigUrl, requireFileShare: false, allowFragment: false);
        var privateUriWithFragment = ParseAllowedUri(privateConfigUrl, requireFileShare: true, allowFragment: true);
        var contentKey = ParseFragmentKey(privateUriWithFragment.Fragment);
        var privateUri = new UriBuilder(privateUriWithFragment) { Fragment = "" }.Uri;

        try
        {
            var publicBytes = await DownloadAsync(
                httpClient, publicUri, PublicLimitBytes, requireJsonContentType: true,
                requiredPathPrefix: null, cancellationToken: cancellationToken);
            var bootstrap = ParsePublicBootstrap(publicBytes);

            var privateBytes = await DownloadAsync(
                httpClient, privateUri, PrivateLimitBytes, requireJsonContentType: false,
                requiredPathPrefix: "/tempfileshare/", cancellationToken: cancellationToken);
            var config = DecryptPrivateConfig(privateBytes, contentKey);
            config.Validate();
            if (!BrokerUrlsEqual(bootstrap.BrokerUrl, config.BrokerUrl))
                throw Safe("broker_mismatch", "公开配置与私有配置的 broker 地址不一致。");
            return config;
        }
        finally
        {
            CryptographicOperations.ZeroMemory(contentKey);
        }
    }

    public static PublicBootstrapConfig ParsePublicBootstrap(ReadOnlySpan<byte> json)
    {
        EnsureUniqueKnownProperties(
            json,
            ["protocolVersion", "brokerUrl", "provisioning", "containsSecrets", "privateConfigFormat"],
            "public_invalid");
        PublicBootstrapConfig bootstrap;
        try
        {
            bootstrap = JsonSerializer.Deserialize<PublicBootstrapConfig>(json, JsonOptions)
                ?? throw new JsonException();
        }
        catch (JsonException)
        {
            throw Safe("public_invalid", "公开配置格式无效。");
        }
        if (bootstrap.ProtocolVersion != 2 ||
            bootstrap.ContainsSecrets ||
            bootstrap.Provisioning != "local-or-encrypted-url-private-config" ||
            bootstrap.PrivateConfigFormat != EnvelopeFormat ||
            !Uri.TryCreate(bootstrap.BrokerUrl, UriKind.Absolute, out var brokerUri) ||
            brokerUri.Scheme != "wss" ||
            !string.Equals(brokerUri.Host, "lixinchen.ca", StringComparison.OrdinalIgnoreCase) ||
            brokerUri.Port != 443 ||
            brokerUri.AbsolutePath != "/crc/v2/ws" ||
            !string.IsNullOrEmpty(brokerUri.UserInfo) ||
            !string.IsNullOrEmpty(brokerUri.Query) ||
            !string.IsNullOrEmpty(brokerUri.Fragment))
            throw Safe("public_invalid", "公开配置未声明受支持的 CRC v2 安全配网方式。");
        return bootstrap;
    }

    public static ClientConfig DecryptPrivateConfig(ReadOnlySpan<byte> json, ReadOnlySpan<byte> contentKey)
    {
        EnsureUniqueKnownProperties(json, ["format", "algorithm", "nonce", "ciphertext", "tag"], "private_invalid");
        EncryptedProvisioningEnvelope envelope;
        try
        {
            envelope = JsonSerializer.Deserialize<EncryptedProvisioningEnvelope>(json, JsonOptions)
                ?? throw new JsonException();
        }
        catch (JsonException)
        {
            throw Safe("private_invalid", "私有配网包格式无效。");
        }
        if (envelope.Format != EnvelopeFormat || envelope.Algorithm != EnvelopeAlgorithm)
            throw Safe("private_invalid", "私有配网包版本或算法不受支持。");

        byte[] nonce;
        byte[] ciphertext;
        byte[] tag;
        try
        {
            nonce = Base64Url.Decode(envelope.Nonce, 12);
            ciphertext = Base64Url.Decode(envelope.Ciphertext);
            tag = Base64Url.Decode(envelope.Tag, 16);
        }
        catch (FormatException)
        {
            throw Safe("private_invalid", "私有配网包编码无效。");
        }
        if (ciphertext.Length is 0 or > PrivateLimitBytes)
            throw Safe("private_invalid", "私有配网包大小无效。");

        var plaintext = new byte[ciphertext.Length];
        try
        {
            using var aes = new AesGcm(contentKey, 16);
            aes.Decrypt(nonce, ciphertext, tag, plaintext, ProvisioningAad);
            EnsureUniqueKnownProperties(
                plaintext,
                [
                    "brokerUrl", "deviceId", "keyId", "brokerAuthKey", "e2eeKey",
                    "allowLocalPowerShellFallback", "reconnectDelaySeconds",
                    "maxReconnectDelaySeconds", "commandCancelGraceSeconds"
                ],
                "private_invalid");
            return JsonSerializer.Deserialize<ClientConfig>(plaintext, JsonOptions)
                ?? throw new JsonException();
        }
        catch (CryptographicException)
        {
            throw Safe("private_auth_failed", "私有配网包认证失败；链接可能不完整或已被替换。");
        }
        catch (JsonException)
        {
            throw Safe("private_invalid", "解密后的私有配置格式无效。");
        }
        finally
        {
            CryptographicOperations.ZeroMemory(plaintext);
        }
    }

    private static async Task<byte[]> DownloadAsync(
        HttpClient client,
        Uri initialUri,
        int maxBytes,
        bool requireJsonContentType,
        string? requiredPathPrefix,
        CancellationToken cancellationToken)
    {
        var current = initialUri;
        var visited = new HashSet<string>(StringComparer.Ordinal);
        for (var redirect = 0; redirect <= 3; redirect++)
        {
            if (!visited.Add(current.AbsoluteUri))
                throw Safe("redirect_invalid", "配置下载发生循环重定向。");
            using var request = new HttpRequestMessage(HttpMethod.Get, current);
            request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
            request.Headers.CacheControl = new CacheControlHeaderValue { NoCache = true, NoStore = true };
            HttpResponseMessage response;
            try
            {
                response = await client.SendAsync(
                    request, HttpCompletionOption.ResponseHeadersRead, cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
            {
                throw Safe("download_timeout", "配置下载超时。");
            }
            catch (HttpRequestException)
            {
                throw Safe("download_failed", "配置下载连接失败。");
            }
            using (response)
            {
                if (response.StatusCode is HttpStatusCode.MovedPermanently or
                    HttpStatusCode.Found or
                    HttpStatusCode.SeeOther or
                    HttpStatusCode.TemporaryRedirect or
                    HttpStatusCode.PermanentRedirect)
                {
                    if (redirect == 3 || response.Headers.Location is null)
                        throw Safe("redirect_invalid", "配置下载重定向无效。");
                    var next = response.Headers.Location.IsAbsoluteUri
                        ? response.Headers.Location
                        : new Uri(current, response.Headers.Location);
                    if (next.Scheme != Uri.UriSchemeHttps ||
                        !string.Equals(next.Host, current.Host, StringComparison.OrdinalIgnoreCase) ||
                        next.Port != current.Port ||
                        !string.IsNullOrEmpty(next.UserInfo) ||
                        !string.IsNullOrEmpty(next.Fragment) ||
                        (requiredPathPrefix is not null &&
                         !next.AbsolutePath.StartsWith(requiredPathPrefix, StringComparison.Ordinal)))
                        throw Safe("redirect_invalid", "配置下载拒绝跨来源或非 HTTPS 重定向。");
                    current = next;
                    continue;
                }
                if (!response.IsSuccessStatusCode)
                    throw Safe("download_failed", $"配置下载失败（HTTP {(int)response.StatusCode}）。");
                if (response.Content.Headers.ContentEncoding.Count > 0)
                    throw Safe("content_encoding", "配置下载不接受压缩响应。");
                if (response.Content.Headers.ContentLength is > 0 &&
                    response.Content.Headers.ContentLength > maxBytes)
                    throw Safe("too_large", "配置文件超过允许大小。");
                if (requireJsonContentType)
                {
                    var mediaType = response.Content.Headers.ContentType?.MediaType ?? "";
                    if (mediaType != "application/json" &&
                        !mediaType.EndsWith("+json", StringComparison.Ordinal))
                        throw Safe("content_type", "公开配置响应不是 JSON。");
                }
                await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken)
                    .ConfigureAwait(false);
                using var memory = new MemoryStream();
                var buffer = new byte[4096];
                while (true)
                {
                    var count = await stream.ReadAsync(buffer, cancellationToken).ConfigureAwait(false);
                    if (count == 0) break;
                    if (memory.Length + count > maxBytes)
                        throw Safe("too_large", "配置文件超过允许大小。");
                    memory.Write(buffer, 0, count);
                }
                return memory.ToArray();
            }
        }
        throw Safe("redirect_invalid", "配置下载重定向次数过多。");
    }

    private static Uri ParseAllowedUri(string value, bool requireFileShare, bool allowFragment)
    {
        if (!Uri.TryCreate(value, UriKind.Absolute, out var uri) ||
            uri.Scheme != Uri.UriSchemeHttps ||
            uri.Port != 443 ||
            !string.IsNullOrEmpty(uri.UserInfo) ||
            !string.IsNullOrEmpty(uri.Query) ||
            (!allowFragment && !string.IsNullOrEmpty(uri.Fragment)) ||
            !string.Equals(uri.Host, "lixinchen.ca", StringComparison.OrdinalIgnoreCase))
            throw Safe("url_invalid", "配置 URL 必须是 lixinchen.ca 的 HTTPS 地址。");
        if (requireFileShare && !uri.AbsolutePath.StartsWith("/tempfileshare/", StringComparison.Ordinal))
            throw Safe("url_invalid", "私有配置必须使用 FileShare temporary URL。");
        if (!requireFileShare && uri.AbsolutePath != "/rwc-mlccs/config.json")
            throw Safe("url_invalid", "公开配置 URL 路径无效。");
        return uri;
    }

    private static byte[] ParseFragmentKey(string fragment)
    {
        const string prefix = "#crc-key=";
        if (!fragment.StartsWith(prefix, StringComparison.Ordinal))
            throw Safe("key_missing", "私有配置 URL 缺少 crc-key fragment。");
        try
        {
            return Base64Url.Decode(Uri.UnescapeDataString(fragment[prefix.Length..]), 32);
        }
        catch (FormatException)
        {
            throw Safe("key_invalid", "私有配置 URL 的 crc-key 无效。");
        }
    }

    private static void EnsureUniqueKnownProperties(
        ReadOnlySpan<byte> json,
        IReadOnlyCollection<string> known,
        string code)
    {
        try
        {
            using var document = JsonDocument.Parse(json.ToArray());
            if (document.RootElement.ValueKind != JsonValueKind.Object)
                throw Safe(code, "配置 JSON 根节点必须是对象。");
            var seen = new HashSet<string>(StringComparer.Ordinal);
            foreach (var property in document.RootElement.EnumerateObject())
            {
                if (!seen.Add(property.Name) || !known.Contains(property.Name))
                    throw Safe(code, "配置包含重复或未知字段。");
            }
            if (seen.Count != known.Count)
                throw Safe(code, "配置缺少必要字段。");
        }
        catch (JsonException)
        {
            throw Safe(code, "配置 JSON 无效。");
        }
    }

    private static bool BrokerUrlsEqual(string left, string right) =>
        Uri.TryCreate(left, UriKind.Absolute, out var leftUri) &&
        Uri.TryCreate(right, UriKind.Absolute, out var rightUri) &&
        Uri.Compare(leftUri, rightUri, UriComponents.AbsoluteUri, UriFormat.SafeUnescaped,
            StringComparison.Ordinal) == 0;

    private static ProvisioningException Safe(string code, string message) => new(code, message);
}
