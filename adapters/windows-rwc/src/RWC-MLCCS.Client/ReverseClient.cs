using System.Collections.Concurrent;
using System.Net.WebSockets;
using RWC_MLCCS.Common;

namespace RWC_MLCCS.Client;

internal static class ReverseClient
{
    public static async Task RunReconnectLoopAsync(ClientConfig config, FileLogger logger, CancellationToken cancellationToken)
    {
        config.Validate();
        var delay = Math.Max(1, config.ReconnectDelaySeconds);
        while (!cancellationToken.IsCancellationRequested)
        {
            try
            {
                await RunOnceAsync(config, logger, cancellationToken);
                delay = Math.Max(1, config.ReconnectDelaySeconds);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                logger.Error("Broker connection failed.", ex);
            }

            try
            {
                var jitter = Random.Shared.NextDouble() * Math.Min(1, delay * 0.2);
                await Task.Delay(TimeSpan.FromSeconds(delay + jitter), cancellationToken);
                delay = Math.Min(Math.Max(delay + 1, delay * 2), Math.Max(delay, config.MaxReconnectDelaySeconds));
            }
            catch (OperationCanceledException)
            {
                break;
            }
        }
    }

    private static async Task RunOnceAsync(ClientConfig config, FileLogger logger, CancellationToken cancellationToken)
    {
        using var socket = new ClientWebSocket();
        socket.Options.KeepAliveInterval = TimeSpan.FromSeconds(20);
        var sendLock = new SemaphoreSlim(1, 1);
        logger.Info($"Connecting to CRC broker as device {config.DeviceId}.");
        await socket.ConnectAsync(new Uri(config.BrokerUrl), cancellationToken);
        var auth = await BrokerConnection.AuthenticateAsync(
            socket, sendLock, "device", config.DeviceId, config.KeyId, config.BrokerAuthKey,
            new { platform = "windows", machineName = Environment.MachineName, userName = Environment.UserName },
            cancellationToken);
        logger.Info($"Authenticated to CRC broker. connectionId={auth.ConnectionId}");

        var executor = new CommandExecutor(logger, TimeSpan.FromSeconds(Math.Max(1, config.CommandCancelGraceSeconds)));
        var commandLock = new SemaphoreSlim(1, 1);
        var sessions = new ConcurrentDictionary<string, DeviceSession>(StringComparer.Ordinal);
        var sessionCancellation = new ConcurrentDictionary<string, CancellationTokenSource>(StringComparer.Ordinal);
        var executionTasks = new ConcurrentDictionary<string, Task>(StringComparer.Ordinal);

        try
        {
            while (socket.State == WebSocketState.Open && !cancellationToken.IsCancellationRequested)
            {
                var message = await WebSocketJson.ReceiveAsync(socket, cancellationToken);
                if (message is null) break;

                if (message.Type == "heartbeat.ping")
                {
                    await BrokerConnection.ReplyHeartbeatAsync(socket, sendLock, message, cancellationToken);
                    continue;
                }

                if (message.Type == "session.ready" && message.SessionId is not null)
                {
                    var ready = message.BodyAs<SessionReadyBody>();
                    if (!string.Equals(ready.DeviceId, config.DeviceId, StringComparison.Ordinal))
                        throw new InvalidDataException("Session targets another device.");
                    sessions[message.SessionId] = new DeviceSession(
                        message.SessionId, ready.OperatorId, config.DeviceId,
                        BrokerCrypto.DeriveSessionKey(config.E2eeKey, message.SessionId, ready.OperatorId, config.DeviceId),
                        ready.ExpiresAt);
                    if (sessionCancellation.TryRemove(message.SessionId, out var previousCts))
                    {
                        previousCts.Cancel();
                        previousCts.Dispose();
                    }
                    sessionCancellation[message.SessionId] = new CancellationTokenSource();
                    logger.Info($"Authorized encrypted session opened. sessionId={message.SessionId}");
                    continue;
                }

                if (message.Type == "session.closed" && message.SessionId is not null)
                {
                    sessions.TryRemove(message.SessionId, out _);
                    if (sessionCancellation.TryRemove(message.SessionId, out var sessionCts))
                    {
                        sessionCts.Cancel();
                        sessionCts.Dispose();
                    }
                    logger.Info($"Session closed. sessionId={message.SessionId}");
                    continue;
                }

                if (message.Type != "relay.data" || message.SessionId is null ||
                    !sessions.TryGetValue(message.SessionId, out var session))
                    continue;

                session.ValidateInbound(message);
                var inner = BrokerCrypto.Decrypt(session.Key, message);
                ValidateInner(inner);
                if (inner.Type != "command.execute") continue;

                if (!sessionCancellation.TryGetValue(message.SessionId, out var commandSessionCts))
                    continue;
                var taskKey = message.SessionId + ":" + inner.RequestId;
                var task = RunQueuedCommandAsync(
                    socket, sendLock, executor, config, session, inner, logger, commandLock,
                    sessions, commandSessionCts.Token, cancellationToken);
                executionTasks[taskKey] = task;
                _ = task.ContinueWith(
                    completedTask => executionTasks.TryRemove(taskKey, out var ignoredTask),
                    CancellationToken.None,
                    TaskContinuationOptions.ExecuteSynchronously,
                    TaskScheduler.Default);
            }
        }
        finally
        {
            foreach (var cts in sessionCancellation.Values) cts.Cancel();
            await executor.StopCurrentProcessAsync();
            try
            {
                await Task.WhenAll(executionTasks.Values);
            }
            catch
            {
            }
            foreach (var cts in sessionCancellation.Values) cts.Dispose();
        }
    }

    private static async Task RunQueuedCommandAsync(
        ClientWebSocket socket,
        SemaphoreSlim sendLock,
        CommandExecutor executor,
        ClientConfig config,
        DeviceSession session,
        InnerMessage inner,
        FileLogger logger,
        SemaphoreSlim commandLock,
        ConcurrentDictionary<string, DeviceSession> sessions,
        CancellationToken sessionCancellationToken,
        CancellationToken connectionCancellationToken)
    {
        var lockAcquired = false;
        using var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(
            sessionCancellationToken, connectionCancellationToken);
        try
        {
            await commandLock.WaitAsync(linkedCts.Token);
            lockAcquired = true;
            if (!sessions.TryGetValue(session.SessionId, out var activeSession) ||
                !ReferenceEquals(activeSession, session))
                throw new OperationCanceledException("Session is no longer active.", linkedCts.Token);
            session.ValidateActive();
            ValidateInner(inner);

            var command = inner.BodyAs<CommandPayload>();
            logger.Info($"Received encrypted command. sessionId={session.SessionId} requestId={inner.RequestId}");
            var commandText = command.Command;
            if (ClientToolRunner.TryBuildScript(command.Command, out var toolScript, out var toolName))
            {
                commandText = toolScript;
                logger.Info($"Translated client tool. requestId={inner.RequestId} tool={toolName}");
            }

            var result = await executor.ExecuteAsync(
                commandText,
                config.AllowLocalPowerShellFallback,
                Math.Clamp(command.TimeoutSeconds, 1, 3600),
                async (stream, text) =>
                {
                    foreach (var chunk in Chunk(text, 64 * 1024))
                    {
                        await SendInnerAsync(socket, sendLock, session,
                            InnerMessage.Create("command.output", inner.RequestId, new OutputPayload(stream, chunk),
                                TimeSpan.FromMinutes(5)), linkedCts.Token);
                    }
                },
                linkedCts.Token);

            await SendInnerAsync(socket, sendLock, session,
                InnerMessage.Create("command.complete", inner.RequestId,
                    new CompletePayload(result.ExitCode, result.Cancelled, result.UsedWinRm, result.DurationMs),
                    TimeSpan.FromMinutes(5)), linkedCts.Token);
        }
        catch (OperationCanceledException)
        {
            logger.Info($"Command cancelled before completion. sessionId={session.SessionId} requestId={inner.RequestId}");
        }
        catch (Exception ex)
        {
            logger.Error($"Command failed. requestId={inner.RequestId}", ex);
            try
            {
                await SendInnerAsync(socket, sendLock, session,
                    InnerMessage.Create("command.output", inner.RequestId,
                        new OutputPayload("stderr", "Command execution failed." + Environment.NewLine),
                        TimeSpan.FromMinutes(5)), linkedCts.Token);
                await SendInnerAsync(socket, sendLock, session,
                    InnerMessage.Create("command.complete", inner.RequestId,
                        new CompletePayload(-1, false, false, 0), TimeSpan.FromMinutes(5)), linkedCts.Token);
            }
            catch
            {
            }
        }
        finally
        {
            if (lockAcquired) commandLock.Release();
        }
    }

    private static async Task SendInnerAsync(
        WebSocket socket,
        SemaphoreSlim sendLock,
        DeviceSession session,
        InnerMessage inner,
        CancellationToken cancellationToken)
    {
        var relay = session.CreateOutbound(inner);
        await WebSocketJson.SendAsync(socket, relay, sendLock, cancellationToken);
    }

    private static void ValidateInner(InnerMessage inner)
    {
        var now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        if (string.IsNullOrWhiteSpace(inner.RequestId) || inner.IssuedAt > now + 30_000 ||
            inner.ExpiresAt < now || inner.ExpiresAt <= inner.IssuedAt)
            throw new InvalidDataException("Encrypted command is expired or invalid.");
    }

    private static IEnumerable<string> Chunk(string value, int maxChars)
    {
        for (var offset = 0; offset < value.Length; offset += maxChars)
            yield return value.Substring(offset, Math.Min(maxChars, value.Length - offset));
        if (value.Length == 0) yield return "";
    }
}

internal sealed class DeviceSession
{
    private long _sendSeq;
    private long _receiveSeq;
    private readonly HashSet<string> _receivedMessageIds = new(StringComparer.Ordinal);

    public DeviceSession(string sessionId, string operatorId, string deviceId, byte[] key, long expiresAt)
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
            relay.From != $"operator:{OperatorId}" || relay.To != $"device:{DeviceId}" ||
            !long.TryParse(relay.Seq, System.Globalization.NumberStyles.None,
                System.Globalization.CultureInfo.InvariantCulture, out var seq) ||
            seq <= 0 || seq.ToString(System.Globalization.CultureInfo.InvariantCulture) != relay.Seq ||
            seq != Interlocked.Read(ref _receiveSeq) + 1 ||
            !_receivedMessageIds.Add(relay.MessageId))
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
        relay.From = $"device:{DeviceId}";
        relay.To = $"operator:{OperatorId}";
        relay.Body = System.Text.Json.JsonSerializer.SerializeToNode(
            BrokerCrypto.Encrypt(Key, inner, SessionId, relay.From, relay.To, seq, relay.MessageId, relay.Ts),
            Serializer.Options);
        return relay;
    }

    public void ValidateActive()
    {
        if (DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() >= ExpiresAt)
            throw new InvalidDataException("Session has expired.");
    }
}
