using System.Net.WebSockets;
using RWC_MLCCS.Common;

namespace RWC_MLCCS.Client;

internal static class ReverseClient
{
    public static async Task RunReconnectLoopAsync(ClientConfig config, FileLogger logger, CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            try
            {
                await RunOnceAsync(config, logger, cancellationToken);
            }
            catch (OperationCanceledException)
            {
                break;
            }
            catch (Exception ex)
            {
                logger.Error("Client connection failed.", ex);
            }

            try
            {
                await Task.Delay(TimeSpan.FromSeconds(Math.Max(1, config.ReconnectDelaySeconds)), cancellationToken);
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
        if (config.AllowInvalidServerCertificate)
        {
            socket.Options.RemoteCertificateValidationCallback = (_, _, _, _) => true;
        }

        logger.Info($"Connecting to {config.ServerUrl} as {config.ClientId}.");
        await socket.ConnectAsync(new Uri(config.ServerUrl), cancellationToken);

        var challengeMessage = await WebSocketJson.ReceiveAsync(socket, cancellationToken);
        if (challengeMessage is null || !challengeMessage.Type.Equals("challenge", StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidDataException("Server did not send a challenge.");
        }

        var challenge = challengeMessage.PayloadAs<ChallengePayload>();
        var auth = new AuthPayload(
            config.ClientId,
            Environment.UserName,
            Environment.MachineName,
            PskAuthenticator.CreateResponse(config.SharedSecret, challenge.Nonce, config.ClientId));
        await WebSocketJson.SendAsync(socket, ProtocolEnvelope.Create("auth", payload: auth), cancellationToken);

        var hello = await WebSocketJson.ReceiveAsync(socket, cancellationToken);
        if (hello is null || !hello.Type.Equals("hello", StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidDataException("Server did not accept authentication.");
        }

        logger.Info("Client authenticated and connected.");
        var executor = new CommandExecutor(logger, TimeSpan.FromSeconds(Math.Max(1, config.CommandCancelGraceSeconds)));
        var commandLock = new SemaphoreSlim(1, 1);

        try
        {
            while (socket.State == WebSocketState.Open && !cancellationToken.IsCancellationRequested)
            {
                var message = await WebSocketJson.ReceiveAsync(socket, cancellationToken);
                if (message is null)
                {
                    break;
                }

                if (!message.Type.Equals("command", StringComparison.OrdinalIgnoreCase) || message.RequestId is null)
                {
                    continue;
                }

                await commandLock.WaitAsync(cancellationToken);
                _ = Task.Run(async () =>
                {
                    try
                    {
                        await ExecuteCommandAsync(socket, executor, config, message, logger, cancellationToken);
                    }
                    finally
                    {
                        commandLock.Release();
                    }
                }, cancellationToken);
            }
        }
        finally
        {
            await executor.StopCurrentProcessAsync();
            if (socket.State == WebSocketState.Open || socket.State == WebSocketState.CloseReceived)
            {
                try
                {
                    await WebSocketJson.SendAsync(socket, ProtocolEnvelope.Create("disconnect"), CancellationToken.None);
                    await socket.CloseAsync(WebSocketCloseStatus.NormalClosure, "Client exiting", CancellationToken.None);
                }
                catch
                {
                }
            }
        }
    }

    private static async Task ExecuteCommandAsync(
        ClientWebSocket socket,
        CommandExecutor executor,
        ClientConfig config,
        ProtocolEnvelope message,
        FileLogger logger,
        CancellationToken cancellationToken)
    {
        var requestId = message.RequestId!;
        var command = message.PayloadAs<CommandPayload>();
        logger.Info($"Received command. requestId={requestId} command={command.Command}");

        try
        {
            var result = await executor.ExecuteAsync(
                command.Command,
                config.AllowLocalPowerShellFallback,
                command.TimeoutSeconds,
                async (stream, text) =>
                {
                    await WebSocketJson.SendAsync(
                        socket,
                        ProtocolEnvelope.Create("output", requestId, new OutputPayload(stream, text)),
                        cancellationToken);
                },
                cancellationToken);

            await WebSocketJson.SendAsync(
                socket,
                ProtocolEnvelope.Create(
                    "complete",
                    requestId,
                    new CompletePayload(result.ExitCode, result.Cancelled, result.UsedWinRm, result.DurationMs)),
                cancellationToken);
        }
        catch (Exception ex)
        {
            logger.Error($"Command failed before completion. requestId={requestId}", ex);
            await WebSocketJson.SendAsync(
                socket,
                ProtocolEnvelope.Create("output", requestId, new OutputPayload("stderr", ex.Message + Environment.NewLine)),
                cancellationToken);
            await WebSocketJson.SendAsync(
                socket,
                ProtocolEnvelope.Create("complete", requestId, new CompletePayload(-1, false, false, 0)),
                cancellationToken);
        }
    }
}
