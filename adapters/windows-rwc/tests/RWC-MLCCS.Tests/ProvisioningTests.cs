using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using RWC_MLCCS.Common;

namespace RWC_MLCCS.Tests;

public sealed class ProvisioningTests
{
    [Fact]
    public async Task DownloadsBootstrapAndDecryptsPrivateFileShareEnvelope()
    {
        var contentKey = RandomNumberGenerator.GetBytes(32);
        var privateConfig = ValidClientConfig();
        var envelope = Encrypt(privateConfig, contentKey);
        using var client = new HttpClient(new StubHandler(request =>
        {
            Assert.Empty(request.RequestUri!.Fragment);
            var json = request.RequestUri!.AbsolutePath switch
            {
                "/rwc-mlccs/config.json" => PublicBootstrap(),
                "/tempfileshare/1234567890123/device.private.json" => JsonSerializer.Serialize(envelope, JsonOptions),
                _ => throw new InvalidOperationException("Unexpected test URL.")
            };
            return Json(json);
        }));

        var config = await ClientProvisioning.DownloadAndDecryptAsync(
            ClientProvisioning.PublicBootstrapUrl,
            $"https://lixinchen.ca/tempfileshare/1234567890123/device.private.json#crc-key={Base64Url.Encode(contentKey)}",
            client,
            CancellationToken.None);

        Assert.Equal(privateConfig.DeviceId, config.DeviceId);
        Assert.Equal(privateConfig.BrokerAuthKey, config.BrokerAuthKey);
        Assert.Equal(privateConfig.E2eeKey, config.E2eeKey);
    }

    [Fact]
    public async Task RejectsTamperedEnvelopeWithoutLeakingUrl()
    {
        var contentKey = RandomNumberGenerator.GetBytes(32);
        var envelope = Encrypt(ValidClientConfig(), contentKey);
        envelope.Tag = Base64Url.Encode(RandomNumberGenerator.GetBytes(16));
        using var client = new HttpClient(new StubHandler(request =>
            Json(request.RequestUri!.AbsolutePath.StartsWith("/rwc-", StringComparison.Ordinal)
                ? PublicBootstrap()
                : JsonSerializer.Serialize(envelope, JsonOptions))));

        var error = await Assert.ThrowsAsync<ProvisioningException>(() =>
            ClientProvisioning.DownloadAndDecryptAsync(
                ClientProvisioning.PublicBootstrapUrl,
                $"https://lixinchen.ca/tempfileshare/1234567890123/secret.json#crc-key={Base64Url.Encode(contentKey)}",
                client,
                CancellationToken.None));

        Assert.Equal("private_auth_failed", error.Code);
        Assert.DoesNotContain("secret.json", error.ToString(), StringComparison.Ordinal);
        Assert.DoesNotContain(Base64Url.Encode(contentKey), error.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public void RejectsDuplicateOrSecretBearingPublicBootstrap()
    {
        Assert.Throws<ProvisioningException>(() =>
            ClientProvisioning.ParsePublicBootstrap(
                Encoding.UTF8.GetBytes("""
                {"protocolVersion":2,"protocolVersion":2,"brokerUrl":"wss://lixinchen.ca/crc/v2/ws","provisioning":"local-or-encrypted-url-private-config","containsSecrets":false,"privateConfigFormat":"crc-device-provisioning-v1"}
                """)));
        Assert.Throws<ProvisioningException>(() =>
            ClientProvisioning.ParsePublicBootstrap(
                Encoding.UTF8.GetBytes(PublicBootstrap().Replace(
                    "\"containsSecrets\": false", "\"containsSecrets\": true", StringComparison.Ordinal))));
    }

    [Theory]
    [InlineData("http://lixinchen.ca/tempfileshare/123/file.json#crc-key=abc")]
    [InlineData("https://evil.example/tempfileshare/123/file.json#crc-key=abc")]
    [InlineData("https://lixinchen.ca:444/tempfileshare/123/file.json#crc-key=abc")]
    [InlineData("https://lixinchen.ca/fileshare/123/file.json#crc-key=abc")]
    [InlineData("https://lixinchen.ca/tempfileshare/123/file.json")]
    public async Task RejectsUnsafePrivateUrls(string privateUrl)
    {
        using var client = new HttpClient(new StubHandler(_ => throw new InvalidOperationException()));
        await Assert.ThrowsAsync<ProvisioningException>(() =>
            ClientProvisioning.DownloadAndDecryptAsync(
                ClientProvisioning.PublicBootstrapUrl, privateUrl, client, CancellationToken.None));
    }

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase
    };

    private static ClientConfig ValidClientConfig() => new()
    {
        BrokerUrl = "wss://lixinchen.ca/crc/v2/ws",
        DeviceId = "CLIENT-URL",
        KeyId = "device-key-url",
        BrokerAuthKey = Base64Url.Encode(RandomNumberGenerator.GetBytes(32)),
        E2eeKey = Base64Url.Encode(RandomNumberGenerator.GetBytes(32)),
        AllowLocalPowerShellFallback = true,
        ReconnectDelaySeconds = 2,
        MaxReconnectDelaySeconds = 60,
        CommandCancelGraceSeconds = 3
    };

    private static EncryptedProvisioningEnvelope Encrypt(ClientConfig config, byte[] contentKey)
    {
        var plaintext = JsonSerializer.SerializeToUtf8Bytes(config, JsonOptions);
        var nonce = RandomNumberGenerator.GetBytes(12);
        var ciphertext = new byte[plaintext.Length];
        var tag = new byte[16];
        using var aes = new AesGcm(contentKey, 16);
        aes.Encrypt(
            nonce,
            plaintext,
            ciphertext,
            tag,
            Encoding.UTF8.GetBytes(ClientProvisioning.EnvelopeFormat));
        return new EncryptedProvisioningEnvelope
        {
            Format = ClientProvisioning.EnvelopeFormat,
            Algorithm = ClientProvisioning.EnvelopeAlgorithm,
            Nonce = Base64Url.Encode(nonce),
            Ciphertext = Base64Url.Encode(ciphertext),
            Tag = Base64Url.Encode(tag)
        };
    }

    private static string PublicBootstrap() => """
        {
          "protocolVersion": 2,
          "brokerUrl": "wss://lixinchen.ca/crc/v2/ws",
          "provisioning": "local-or-encrypted-url-private-config",
          "containsSecrets": false,
          "privateConfigFormat": "crc-device-provisioning-v1"
        }
        """;

    private static HttpResponseMessage Json(string value) => new(HttpStatusCode.OK)
    {
        Content = new StringContent(value, Encoding.UTF8, "application/json")
    };

    private sealed class StubHandler(Func<HttpRequestMessage, HttpResponseMessage> handler) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken) =>
            Task.FromResult(handler(request));
    }
}
