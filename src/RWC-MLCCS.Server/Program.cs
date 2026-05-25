using System.Collections.Concurrent;
using System.Net;
using System.Net.WebSockets;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Server.Kestrel.Core;
using RWC_MLCCS.Common;

var configPath = args.Length >= 2 && args[0] == "--config" ? args[1] : AppPaths.DefaultServerConfigPath;
var config = ServerConfig.LoadOrCreate(configPath);
var logger = new FileLogger("server");
var registry = new ClientRegistry(logger);

if (args.Contains("--init", StringComparer.OrdinalIgnoreCase))
{
    var cert = CertificateFactory.EnsureCertificate(configPath, config, logger);
    Console.WriteLine($"Config: {configPath}");
    Console.WriteLine($"Certificate: {cert}");
    return;
}

var certificate = CertificateFactory.EnsureCertificate(configPath, config, logger);
var builder = WebApplication.CreateBuilder(args);
builder.WebHost.ConfigureKestrel(options =>
{
    ConfigureListen(options, config, certificate);
});

var app = builder.Build();
app.UseWebSockets(new WebSocketOptions
{
    KeepAliveInterval = TimeSpan.FromSeconds(20)
});

app.Map("/link", async context =>
{
    if (!context.WebSockets.IsWebSocketRequest)
    {
        context.Response.StatusCode = StatusCodes.Status400BadRequest;
        return;
    }

    using var socket = await context.WebSockets.AcceptWebSocketAsync();
    await ClientSession.HandleAsync(socket, config, registry, logger, context.RequestAborted);
});

var runTask = app.RunAsync();
Console.WriteLine($"RWC-MLCCS server listening on wss://{config.ListenHost}:{config.Port}/link");
Console.WriteLine($"Config: {configPath}");
Console.WriteLine("Commands: clients | use <clientId> | exit");

await InteractiveShell.RunAsync(registry, config, logger);
using (var stopCts = new CancellationTokenSource(TimeSpan.FromSeconds(3)))
{
    await app.StopAsync(stopCts.Token);
}
await runTask;

static void ConfigureListen(KestrelServerOptions options, ServerConfig config, X509Certificate2 certificate)
{
    if (config.ListenHost is "0.0.0.0" or "*" or "+")
    {
        options.ListenAnyIP(config.Port, listen => listen.UseHttps(certificate));
        return;
    }

    if (IPAddress.TryParse(config.ListenHost, out var address))
    {
        options.Listen(address, config.Port, listen => listen.UseHttps(certificate));
        return;
    }

    options.ListenLocalhost(config.Port, listen => listen.UseHttps(certificate));
}

internal static class InteractiveShell
{
    public static async Task RunAsync(ClientRegistry registry, ServerConfig config, FileLogger logger)
    {
        ConnectedClient? selected = null;
        while (true)
        {
            Console.Write(selected is null ? "server> " : $"{selected.ClientId}> ");
            var line = Console.ReadLine();
            if (line is null)
            {
                break;
            }

            line = line.Trim();
            if (line.Length == 0)
            {
                continue;
            }

            if (line.Equals("exit", StringComparison.OrdinalIgnoreCase) ||
                line.Equals("quit", StringComparison.OrdinalIgnoreCase))
            {
                break;
            }

            if (line.Equals("clients", StringComparison.OrdinalIgnoreCase))
            {
                foreach (var client in registry.List())
                {
                    Console.WriteLine($"{client.ClientId}\t{client.UserName}\t{client.MachineName}\tconnected={client.ConnectedAt:O}");
                }

                continue;
            }

            if (line.StartsWith("use ", StringComparison.OrdinalIgnoreCase))
            {
                var id = line[4..].Trim();
                selected = registry.Get(id);
                Console.WriteLine(selected is null ? $"Client not found: {id}" : $"Selected {selected.ClientId}");
                continue;
            }

            if (selected is null)
            {
                Console.WriteLine("Select a client first: use <clientId>");
                continue;
            }

            try
            {
                var result = await selected.ExecuteAsync(line, config.CommandTimeoutSeconds);
                Console.WriteLine($"[complete] exit={result.ExitCode} cancelled={result.Cancelled} winrm={result.UsedWinRm} durationMs={result.DurationMs}");
            }
            catch (Exception ex)
            {
                logger.Error("Command dispatch failed.", ex);
                Console.WriteLine($"[error] {ex.Message}");
            }
        }
    }
}

internal sealed class ClientRegistry
{
    private readonly ConcurrentDictionary<string, ConnectedClient> _clients = new(StringComparer.OrdinalIgnoreCase);
    private readonly FileLogger _logger;

    public ClientRegistry(FileLogger logger)
    {
        _logger = logger;
    }

    public void Add(ConnectedClient client)
    {
        _clients[client.ClientId] = client;
        _logger.Info($"Client connected: {client.ClientId} {client.UserName}@{client.MachineName}");
    }

    public void Remove(string clientId)
    {
        _clients.TryRemove(clientId, out _);
        _logger.Info($"Client disconnected: {clientId}");
    }

    public ConnectedClient? Get(string clientId)
    {
        _clients.TryGetValue(clientId, out var client);
        return client;
    }

    public IReadOnlyCollection<ConnectedClient> List() => _clients.Values.OrderBy(c => c.ClientId).ToArray();
}

internal sealed class ConnectedClient
{
    private readonly WebSocket _socket;
    private readonly FileLogger _logger;
    private readonly SemaphoreSlim _sendLock = new(1, 1);
    private readonly ConcurrentDictionary<string, TaskCompletionSource<CompletePayload>> _pending = new();

    public ConnectedClient(string clientId, string userName, string machineName, WebSocket socket, FileLogger logger)
    {
        ClientId = clientId;
        UserName = userName;
        MachineName = machineName;
        ConnectedAt = DateTimeOffset.Now;
        _socket = socket;
        _logger = logger;
    }

    public string ClientId { get; }

    public string UserName { get; }

    public string MachineName { get; }

    public DateTimeOffset ConnectedAt { get; }

    public async Task<CompletePayload> ExecuteAsync(string command, int timeoutSeconds)
    {
        var requestId = Guid.NewGuid().ToString("N");
        var tcs = new TaskCompletionSource<CompletePayload>(TaskCreationOptions.RunContinuationsAsynchronously);
        _pending[requestId] = tcs;

        await SendAsync(ProtocolEnvelope.Create("command", requestId, new CommandPayload(command, timeoutSeconds)), CancellationToken.None);
        _logger.Info($"Sent command to {ClientId}. requestId={requestId} command={command}");
        return await tcs.Task.WaitAsync(TimeSpan.FromSeconds(timeoutSeconds + 30));
    }

    public async Task ReceiveLoopAsync(CancellationToken cancellationToken)
    {
        while (_socket.State == WebSocketState.Open && !cancellationToken.IsCancellationRequested)
        {
            var message = await WebSocketJson.ReceiveAsync(_socket, cancellationToken);
            if (message is null)
            {
                break;
            }

            if (message.Type.Equals("output", StringComparison.OrdinalIgnoreCase))
            {
                var output = message.PayloadAs<OutputPayload>();
                Console.Write(output.Stream.Equals("stderr", StringComparison.OrdinalIgnoreCase) ? $"[stderr] {output.Text}" : output.Text);
                _logger.Info($"Output {ClientId} {message.RequestId} {output.Stream}: {output.Text.TrimEnd()}");
            }
            else if (message.Type.Equals("complete", StringComparison.OrdinalIgnoreCase) && message.RequestId is not null)
            {
                var complete = message.PayloadAs<CompletePayload>();
                if (_pending.TryRemove(message.RequestId, out var pending))
                {
                    pending.TrySetResult(complete);
                }
            }
            else if (message.Type.Equals("disconnect", StringComparison.OrdinalIgnoreCase))
            {
                break;
            }
        }
    }

    private async Task SendAsync(ProtocolEnvelope envelope, CancellationToken cancellationToken)
    {
        await _sendLock.WaitAsync(cancellationToken);
        try
        {
            await WebSocketJson.SendAsync(_socket, envelope, cancellationToken);
        }
        finally
        {
            _sendLock.Release();
        }
    }
}

internal static class ClientSession
{
    public static async Task HandleAsync(
        WebSocket socket,
        ServerConfig config,
        ClientRegistry registry,
        FileLogger logger,
        CancellationToken cancellationToken)
    {
        var nonce = PskAuthenticator.CreateNonce();
        await WebSocketJson.SendAsync(socket, ProtocolEnvelope.Create("challenge", payload: new ChallengePayload(nonce)), cancellationToken);

        var authMessage = await WebSocketJson.ReceiveAsync(socket, cancellationToken);
        if (authMessage is null || !authMessage.Type.Equals("auth", StringComparison.OrdinalIgnoreCase))
        {
            await CloseAsync(socket, WebSocketCloseStatus.PolicyViolation, "Missing auth", cancellationToken);
            return;
        }

        var auth = authMessage.PayloadAs<AuthPayload>();
        if (!PskAuthenticator.VerifyResponse(config.SharedSecret, nonce, auth.ClientId, auth.Response))
        {
            logger.Info($"Rejected client auth: {auth.ClientId}");
            await CloseAsync(socket, WebSocketCloseStatus.PolicyViolation, "Bad auth", cancellationToken);
            return;
        }

        await WebSocketJson.SendAsync(socket, ProtocolEnvelope.Create("hello", payload: new { serverTime = DateTimeOffset.Now }), cancellationToken);

        var client = new ConnectedClient(auth.ClientId, auth.UserName, auth.MachineName, socket, logger);
        registry.Add(client);
        try
        {
            await client.ReceiveLoopAsync(cancellationToken);
        }
        catch (OperationCanceledException)
        {
        }
        catch (WebSocketException ex)
        {
            logger.Error($"WebSocket failed for {auth.ClientId}.", ex);
        }
        finally
        {
            registry.Remove(auth.ClientId);
        }
    }

    private static async Task CloseAsync(WebSocket socket, WebSocketCloseStatus status, string description, CancellationToken cancellationToken)
    {
        if (socket.State == WebSocketState.Open)
        {
            await socket.CloseAsync(status, description, cancellationToken);
        }
    }
}

internal static class CertificateFactory
{
    public static X509Certificate2 EnsureCertificate(string configPath, ServerConfig config, FileLogger logger)
    {
        var certPath = Path.IsPathRooted(config.CertificatePath)
            ? config.CertificatePath
            : Path.Combine(Path.GetDirectoryName(configPath) ?? AppPaths.BaseDirectory, config.CertificatePath);

        if (File.Exists(certPath))
        {
            var existing = new X509Certificate2(certPath, config.CertificatePassword);
            if (existing.NotAfter.ToUniversalTime() > DateTime.UtcNow.AddYears(80))
            {
                return existing;
            }

            existing.Dispose();
            File.Delete(certPath);
            logger.Info($"Regenerating development TLS certificate because it expires before the long-lived threshold: {certPath}");
        }

        Directory.CreateDirectory(Path.GetDirectoryName(certPath) ?? AppPaths.BaseDirectory);
        using var rsa = RSA.Create(2048);
        var request = new CertificateRequest(
            "CN=lixinchen.ca",
            rsa,
            HashAlgorithmName.SHA256,
            RSASignaturePadding.Pkcs1);
        request.CertificateExtensions.Add(new X509BasicConstraintsExtension(false, false, 0, false));
        request.CertificateExtensions.Add(new X509KeyUsageExtension(X509KeyUsageFlags.DigitalSignature, false));
        request.CertificateExtensions.Add(new X509SubjectKeyIdentifierExtension(request.PublicKey, false));

        var sanBuilder = new SubjectAlternativeNameBuilder();
        sanBuilder.AddDnsName("lixinchen.ca");
        sanBuilder.AddDnsName("localhost");
        sanBuilder.AddIpAddress(IPAddress.Loopback);
        sanBuilder.AddIpAddress(IPAddress.Parse("127.0.0.1"));
        request.CertificateExtensions.Add(sanBuilder.Build());

        using var generated = request.CreateSelfSigned(DateTimeOffset.Now.AddDays(-1), DateTimeOffset.Now.AddYears(100));
        var pfx = generated.Export(X509ContentType.Pfx, config.CertificatePassword);
        File.WriteAllBytes(certPath, pfx);
        logger.Info($"Generated development TLS certificate: {certPath}");
        return new X509Certificate2(certPath, config.CertificatePassword);
    }
}
