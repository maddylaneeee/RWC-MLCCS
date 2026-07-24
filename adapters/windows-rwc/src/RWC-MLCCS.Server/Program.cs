using System.Collections.Concurrent;
using System.Net;
using System.Net.WebSockets;
using System.Security.Cryptography;
using System.Text;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Hosting;
using RWC_MLCCS.Common;

var configPath = args.Length >= 2 && args[0] == "--config" ? args[1] : AppPaths.DefaultServerConfigPath;
var config = ServerConfig.LoadOrCreate(configPath);
if (args.Contains("--init", StringComparer.OrdinalIgnoreCase))
{
    Console.WriteLine($"Config: {configPath}");
    Console.WriteLine($"Loopback API token: {config.LoopbackApi.BearerToken}");
    return;
}

config.Validate();
var logger = new FileLogger("operator");
using var stopping = new CancellationTokenSource();
var client = new OperatorBrokerClient(config, logger);
var brokerTask = client.RunReconnectLoopAsync(stopping.Token);
Task? apiTask = null;
WebApplication? api = null;

if (config.LoopbackApi.Enabled)
{
    var builder = WebApplication.CreateBuilder();
    builder.WebHost.UseUrls($"http://127.0.0.1:{config.LoopbackApi.Port}");
    api = builder.Build();
    api.MapGet("/operator/clients", async context =>
    {
        if (!ApiAuthorization.IsAllowed(context, config.LoopbackApi.BearerToken)) return;
        await context.Response.WriteAsJsonAsync(client.ListDevices());
    });
    api.MapPost("/operator/execute", async context =>
    {
        if (!ApiAuthorization.IsAllowed(context, config.LoopbackApi.BearerToken)) return;
        var request = await context.Request.ReadFromJsonAsync<OperatorCommandRequest>();
        if (request is null || string.IsNullOrWhiteSpace(request.DeviceId) || string.IsNullOrWhiteSpace(request.Command))
        {
            context.Response.StatusCode = StatusCodes.Status400BadRequest;
            return;
        }

        try
        {
            var result = await client.ExecuteAsync(
                request.DeviceId, request.Command,
                request.TimeoutSeconds is > 0 ? request.TimeoutSeconds.Value : config.CommandTimeoutSeconds,
                context.RequestAborted);
            await context.Response.WriteAsJsonAsync(result);
        }
        catch (KeyNotFoundException)
        {
            context.Response.StatusCode = StatusCodes.Status404NotFound;
        }
        catch (TimeoutException)
        {
            context.Response.StatusCode = StatusCodes.Status504GatewayTimeout;
        }
    });
    apiTask = api.RunAsync();
    Console.WriteLine($"Loopback API: http://127.0.0.1:{config.LoopbackApi.Port} (Bearer token in config)");
}

if (args.Contains("--headless", StringComparer.OrdinalIgnoreCase))
{
    await brokerTask;
}
else
{
    Console.WriteLine($"RWC operator connecting outbound to CRC broker as {config.OperatorId}.");
    Console.WriteLine("Commands: clients | use <deviceId> | exit");
    await InteractiveShell.RunAsync(client, config, logger, stopping.Token);
    stopping.Cancel();
}

if (api is not null)
{
    try { await api.StopAsync(); } catch { }
}
try { await brokerTask; } catch (OperationCanceledException) { }
if (apiTask is not null)
{
    try { await apiTask; } catch (OperationCanceledException) { }
}

internal static class InteractiveShell
{
    public static async Task RunAsync(
        OperatorBrokerClient client,
        ServerConfig config,
        FileLogger logger,
        CancellationToken cancellationToken)
    {
        string? selected = null;
        while (!cancellationToken.IsCancellationRequested)
        {
            Console.Write(selected is null ? "operator> " : $"{selected}> ");
            var line = Console.ReadLine()?.Trim();
            if (line is null || line.Equals("exit", StringComparison.OrdinalIgnoreCase) ||
                line.Equals("quit", StringComparison.OrdinalIgnoreCase)) return;
            if (line.Length == 0) continue;

            if (line.Equals("clients", StringComparison.OrdinalIgnoreCase))
            {
                foreach (var device in client.ListDevices())
                    Console.WriteLine($"{device.DeviceId}\tconnected={DateTimeOffset.FromUnixTimeMilliseconds(device.ConnectedAt):O}");
                continue;
            }

            if (line.StartsWith("use ", StringComparison.OrdinalIgnoreCase))
            {
                var id = line[4..].Trim();
                selected = config.Devices.ContainsKey(id) ? id : null;
                Console.WriteLine(selected is null ? $"Device is not configured: {id}" : $"Selected {selected}");
                continue;
            }

            if (selected is null)
            {
                Console.WriteLine("Select a device first: use <deviceId>");
                continue;
            }

            try
            {
                var result = await client.ExecuteAsync(selected, line, config.CommandTimeoutSeconds, cancellationToken);
                if (result.Stdout.Length > 0) Console.Write(result.Stdout);
                if (result.Stderr.Length > 0) Console.Error.Write(result.Stderr);
                Console.WriteLine($"[complete] exit={result.Complete.ExitCode} cancelled={result.Complete.Cancelled} " +
                                  $"winrm={result.Complete.UsedWinRm} durationMs={result.Complete.DurationMs}");
            }
            catch (Exception ex)
            {
                logger.Error("Encrypted command dispatch failed.", ex);
                Console.WriteLine($"[error] {ex.Message}");
            }
        }
    }
}

internal static class ApiAuthorization
{
    public static bool IsAllowed(HttpContext context, string expectedToken)
    {
        if (context.Connection.RemoteIpAddress is not { } address || !IPAddress.IsLoopback(address))
        {
            context.Response.StatusCode = StatusCodes.Status403Forbidden;
            return false;
        }

        var supplied = context.Request.Headers.Authorization.ToString();
        const string prefix = "Bearer ";
        if (!supplied.StartsWith(prefix, StringComparison.OrdinalIgnoreCase) ||
            !FixedEquals(supplied[prefix.Length..], expectedToken))
        {
            context.Response.StatusCode = StatusCodes.Status401Unauthorized;
            context.Response.Headers.WWWAuthenticate = "Bearer";
            return false;
        }
        return true;
    }

    private static bool FixedEquals(string left, string right)
    {
        var a = SHA256.HashData(Encoding.UTF8.GetBytes(left));
        var b = SHA256.HashData(Encoding.UTF8.GetBytes(right));
        return CryptographicOperations.FixedTimeEquals(a, b);
    }
}

internal sealed class OperatorBrokerClient
{
    private readonly ServerConfig _config;
    private readonly FileLogger _logger;
    private readonly ConcurrentDictionary<string, PresenceDevice> _devices = new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<string, OperatorSession> _sessions = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, TaskCompletionSource<OperatorSession>> _opening =
        new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<string, PendingCommand> _pending = new(StringComparer.Ordinal);
    private readonly object _socketGate = new();
    private ClientWebSocket? _socket;
    private SemaphoreSlim? _sendLock;

    public OperatorBrokerClient(ServerConfig config, FileLogger logger)
    {
        _config = config;
        _logger = logger;
    }

    public IReadOnlyCollection<PresenceDevice> ListDevices() =>
        _devices.Values.OrderBy(device => device.DeviceId).ToArray();

    public async Task RunReconnectLoopAsync(CancellationToken cancellationToken)
    {
        var delay = Math.Max(1, _config.ReconnectDelaySeconds);
        while (!cancellationToken.IsCancellationRequested)
        {
            try
            {
                await RunOnceAsync(cancellationToken);
                delay = Math.Max(1, _config.ReconnectDelaySeconds);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                _logger.Error("Operator broker connection failed.", ex);
            }
            finally
            {
                DisconnectPending();
            }

            var jitter = Random.Shared.NextDouble() * Math.Min(1, delay * 0.2);
            await Task.Delay(TimeSpan.FromSeconds(delay + jitter), cancellationToken);
            delay = Math.Min(Math.Max(delay + 1, delay * 2), Math.Max(delay, _config.MaxReconnectDelaySeconds));
        }
    }

    public async Task<OperatorCommandResult> ExecuteAsync(
        string deviceId,
        string command,
        int timeoutSeconds,
        CancellationToken cancellationToken)
    {
        if (!_config.Devices.ContainsKey(deviceId)) throw new KeyNotFoundException("Device is not configured.");
        timeoutSeconds = Math.Clamp(timeoutSeconds, 1, 3600);
        var session = await GetOrOpenSessionAsync(deviceId, cancellationToken);
        var requestId = BrokerCrypto.RandomId();
        var pending = new PendingCommand();
        if (!_pending.TryAdd(requestId, pending)) throw new InvalidOperationException("Request id collision.");
        try
        {
            var inner = InnerMessage.Create("command.execute", requestId,
                new CommandPayload(command, timeoutSeconds), TimeSpan.FromSeconds(timeoutSeconds + 30));
            await SendAsync(session.CreateOutbound(inner), cancellationToken);
            _logger.Info($"Sent encrypted command. deviceId={deviceId} sessionId={session.SessionId} requestId={requestId}");
            return await pending.Completion.Task.WaitAsync(TimeSpan.FromSeconds(timeoutSeconds + 30), cancellationToken);
        }
        catch (TimeoutException)
        {
            throw;
        }
        finally
        {
            _pending.TryRemove(requestId, out _);
        }
    }

    private async Task RunOnceAsync(CancellationToken cancellationToken)
    {
        using var socket = new ClientWebSocket();
        socket.Options.KeepAliveInterval = TimeSpan.FromSeconds(20);
        var sendLock = new SemaphoreSlim(1, 1);
        await socket.ConnectAsync(new Uri(_config.BrokerUrl), cancellationToken);
        var auth = await BrokerConnection.AuthenticateAsync(
            socket, sendLock, "operator", _config.OperatorId, _config.KeyId, _config.BrokerAuthKey,
            new { adapter = "windows-rwc" }, cancellationToken);
        lock (_socketGate)
        {
            _socket = socket;
            _sendLock = sendLock;
        }
        _logger.Info($"Operator authenticated to CRC broker. connectionId={auth.ConnectionId}");

        try
        {
            while (socket.State == WebSocketState.Open && !cancellationToken.IsCancellationRequested)
            {
                var message = await WebSocketJson.ReceiveAsync(socket, cancellationToken);
                if (message is null) break;
                await HandleMessageAsync(socket, sendLock, message, cancellationToken);
            }
        }
        finally
        {
            lock (_socketGate)
            {
                if (ReferenceEquals(_socket, socket))
                {
                    _socket = null;
                    _sendLock = null;
                }
            }
        }
    }

    private async Task HandleMessageAsync(
        ClientWebSocket socket,
        SemaphoreSlim sendLock,
        ProtocolEnvelope message,
        CancellationToken cancellationToken)
    {
        if (message.Type == "heartbeat.ping")
        {
            await BrokerConnection.ReplyHeartbeatAsync(socket, sendLock, message, cancellationToken);
            return;
        }
        if (message.Type == "presence.snapshot")
        {
            var snapshot = message.BodyAs<PresenceSnapshotBody>();
            _devices.Clear();
            foreach (var device in snapshot.Devices) _devices[device.DeviceId] = device;
            return;
        }
        if (message.Type == "session.ready" && message.SessionId is not null)
        {
            var ready = message.BodyAs<SessionReadyBody>();
            if (!_config.Devices.TryGetValue(ready.DeviceId, out var deviceConfig) ||
                ready.OperatorId != _config.OperatorId)
                throw new InvalidDataException("Broker returned an unauthorized session.");
            var readySession = new OperatorSession(
                message.SessionId, ready.OperatorId, ready.DeviceId,
                BrokerCrypto.DeriveSessionKey(deviceConfig.E2eeKey, message.SessionId, ready.OperatorId, ready.DeviceId),
                ready.ExpiresAt);
            _sessions[message.SessionId] = readySession;
            if (_opening.TryRemove(ready.DeviceId, out var opening)) opening.TrySetResult(readySession);
            return;
        }
        if (message.Type == "session.denied")
        {
            var deviceId = message.Body?["deviceId"]?.GetValue<string>();
            if (deviceId is not null && _opening.TryRemove(deviceId, out var opening))
                opening.TrySetException(new InvalidOperationException("Broker denied the session."));
            return;
        }
        if (message.Type == "session.closed" && message.SessionId is not null)
        {
            _sessions.TryRemove(message.SessionId, out _);
            return;
        }
        if (message.Type != "relay.data" || message.SessionId is null ||
            !_sessions.TryGetValue(message.SessionId, out var session)) return;

        session.ValidateInbound(message);
        var inner = BrokerCrypto.Decrypt(session.Key, message);
        ValidateInner(inner);
        if (!_pending.TryGetValue(inner.RequestId, out var pending)) return;
        if (inner.Type == "command.output")
        {
            var output = inner.BodyAs<OutputPayload>();
            pending.Append(output);
        }
        else if (inner.Type == "command.complete")
        {
            var complete = inner.BodyAs<CompletePayload>();
            pending.Complete(complete);
        }
    }

    private async Task<OperatorSession> GetOrOpenSessionAsync(string deviceId, CancellationToken cancellationToken)
    {
        var existing = _sessions.Values.FirstOrDefault(session =>
            string.Equals(session.DeviceId, deviceId, StringComparison.OrdinalIgnoreCase));
        if (existing is not null) return existing;

        var created = new TaskCompletionSource<OperatorSession>(TaskCreationOptions.RunContinuationsAsynchronously);
        var opening = _opening.GetOrAdd(deviceId, created);
        if (ReferenceEquals(created, opening))
            await SendAsync(ProtocolEnvelope.Create("session.open", new SessionOpenBody(deviceId)), cancellationToken);
        try
        {
            return await opening.Task.WaitAsync(TimeSpan.FromSeconds(20), cancellationToken);
        }
        finally
        {
            _opening.TryRemove(new KeyValuePair<string, TaskCompletionSource<OperatorSession>>(deviceId, opening));
        }
    }

    private async Task SendAsync(ProtocolEnvelope message, CancellationToken cancellationToken)
    {
        ClientWebSocket? socket;
        SemaphoreSlim? sendLock;
        lock (_socketGate)
        {
            socket = _socket;
            sendLock = _sendLock;
        }
        if (socket?.State != WebSocketState.Open || sendLock is null)
            throw new InvalidOperationException("Operator is not connected to the broker.");
        await WebSocketJson.SendAsync(socket, message, sendLock, cancellationToken);
    }

    private void DisconnectPending()
    {
        var error = new IOException("Broker connection was lost.");
        foreach (var opening in _opening.Values) opening.TrySetException(error);
        _opening.Clear();
        foreach (var pending in _pending.Values) pending.Fail(error);
        _pending.Clear();
        _sessions.Clear();
        _devices.Clear();
    }

    private static void ValidateInner(InnerMessage inner)
    {
        var now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        if (string.IsNullOrWhiteSpace(inner.RequestId) || inner.IssuedAt > now + 30_000 ||
            inner.ExpiresAt < now || inner.ExpiresAt <= inner.IssuedAt)
            throw new InvalidDataException("Encrypted response is expired or invalid.");
    }
}

internal sealed class OperatorSession
{
    private long _sendSeq;
    private long _receiveSeq;
    private readonly HashSet<string> _receivedMessageIds = new(StringComparer.Ordinal);

    public OperatorSession(string sessionId, string operatorId, string deviceId, byte[] key, long expiresAt)
    {
        SessionId = sessionId;
        OperatorId = operatorId;
        DeviceId = deviceId;
        Key = key;
        ExpiresAt = expiresAt;
    }

    public string SessionId { get; }
    public string OperatorId { get; }
    public string DeviceId { get; }
    public byte[] Key { get; }
    public long ExpiresAt { get; }

    public void ValidateInbound(ProtocolEnvelope relay)
    {
        var now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        if (now >= ExpiresAt || Math.Abs(now - relay.Ts) > 30_000 ||
            relay.From != $"device:{DeviceId}" || relay.To != $"operator:{OperatorId}" ||
            !long.TryParse(relay.Seq, System.Globalization.NumberStyles.None,
                System.Globalization.CultureInfo.InvariantCulture, out var seq) ||
            seq <= 0 || seq.ToString(System.Globalization.CultureInfo.InvariantCulture) != relay.Seq ||
            seq != Interlocked.Read(ref _receiveSeq) + 1 || !_receivedMessageIds.Add(relay.MessageId))
            throw new InvalidDataException("Relay sequence, identity, or message id is invalid.");
        Interlocked.Exchange(ref _receiveSeq, seq);
        if (_receivedMessageIds.Count > 10_000) throw new InvalidDataException("Session replay window exceeded.");
    }

    public ProtocolEnvelope CreateOutbound(InnerMessage inner)
    {
        var seq = Interlocked.Increment(ref _sendSeq).ToString(System.Globalization.CultureInfo.InvariantCulture);
        var relay = ProtocolEnvelope.Create("relay.data");
        relay.SessionId = SessionId;
        relay.Seq = seq;
        relay.From = $"operator:{OperatorId}";
        relay.To = $"device:{DeviceId}";
        relay.Body = System.Text.Json.JsonSerializer.SerializeToNode(
            BrokerCrypto.Encrypt(Key, inner, SessionId, relay.From, relay.To, seq, relay.MessageId, relay.Ts),
            Serializer.Options);
        return relay;
    }
}

internal sealed class PendingCommand
{
    private readonly object _gate = new();
    private readonly StringBuilder _stdout = new();
    private readonly StringBuilder _stderr = new();
    public TaskCompletionSource<OperatorCommandResult> Completion { get; } =
        new(TaskCreationOptions.RunContinuationsAsynchronously);

    public void Append(OutputPayload output)
    {
        lock (_gate)
        {
            var target = output.Stream.Equals("stderr", StringComparison.OrdinalIgnoreCase) ? _stderr : _stdout;
            if (target.Length + output.Text.Length > BrokerCrypto.MaxPlaintextBytes)
                throw new InvalidDataException("Accumulated command output exceeds size limit.");
            target.Append(output.Text);
        }
    }

    public void Complete(CompletePayload complete)
    {
        lock (_gate)
            Completion.TrySetResult(new OperatorCommandResult(complete, _stdout.ToString(), _stderr.ToString()));
    }

    public void Fail(Exception exception) => Completion.TrySetException(exception);
}

internal sealed record OperatorCommandRequest(string DeviceId, string Command, int? TimeoutSeconds);
internal sealed record OperatorCommandResult(CompletePayload Complete, string Stdout, string Stderr);
