using System.Diagnostics;
using System.Text;

namespace RWC_MLCCS.Common;

public sealed record CommandResult(int ExitCode, bool Cancelled, bool UsedWinRm, long DurationMs);

public sealed class CommandExecutor
{
    private readonly FileLogger _logger;
    private readonly TimeSpan _cancelGrace;
    private readonly Func<CancellationToken, Task<bool>> _winRmProbe;
    private readonly SemaphoreSlim _executionGate = new(1, 1);
    private readonly object _processGate = new();
    private Process? _currentProcess;

    public CommandExecutor(
        FileLogger logger,
        TimeSpan cancelGrace,
        Func<CancellationToken, Task<bool>>? winRmProbe = null)
    {
        _logger = logger;
        _cancelGrace = cancelGrace > TimeSpan.Zero ? cancelGrace : TimeSpan.FromSeconds(1);
        _winRmProbe = winRmProbe ?? WinRmProbe.IsLocalWinRmAvailableAsync;
    }

    public async Task<CommandResult> ExecuteAsync(
        string command,
        bool allowLocalPowerShellFallback,
        int timeoutSeconds,
        Func<string, string, Task> onOutput,
        CancellationToken cancellationToken)
    {
        if (!await _executionGate.WaitAsync(0, cancellationToken).ConfigureAwait(false))
        {
            throw new InvalidOperationException("A command is already executing.");
        }

        try
        {
            return await ExecuteCoreAsync(
                command,
                allowLocalPowerShellFallback,
                timeoutSeconds,
                onOutput,
                cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _executionGate.Release();
        }
    }

    private async Task<CommandResult> ExecuteCoreAsync(
        string command,
        bool allowLocalPowerShellFallback,
        int timeoutSeconds,
        Func<string, string, Task> onOutput,
        CancellationToken cancellationToken)
    {
        var useWinRm = await _winRmProbe(cancellationToken).ConfigureAwait(false);
        if (!useWinRm && !allowLocalPowerShellFallback)
        {
            throw new InvalidOperationException(
                "Local WinRM is unavailable and local PowerShell fallback is disabled.");
        }

        var preparedCommand = PowerShellScripts.PrepareCommand(command);
        var script = useWinRm
            ? PowerShellScripts.WrapForLocalWinRm(preparedCommand)
            : preparedCommand;
        var stopwatch = Stopwatch.StartNew();
        var timeout = TimeSpan.FromSeconds(Math.Max(1, timeoutSeconds));
        using var timeoutCts = new CancellationTokenSource(timeout);
        using var linkedCts =
            CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, timeoutCts.Token);
        using var pumpCts =
            CancellationTokenSource.CreateLinkedTokenSource(linkedCts.Token);

        var startInfo = PowerShellScripts.CreateStartInfo(script);
        using var process = new Process { StartInfo = startInfo, EnableRaisingEvents = true };
        Task? stdoutTask = null;
        Task? stderrTask = null;
        var started = false;
        var cancelled = false;

        try
        {
            SetCurrentProcess(process);
            _logger.Info($"Starting command. usedWinRm={useWinRm} timeout={timeoutSeconds}s");
            process.Start();
            started = true;

            stdoutTask = PumpAsync(
                process.StandardOutput, "stdout", onOutput, pumpCts.Token);
            stderrTask = PumpAsync(
                process.StandardError, "stderr", onOutput, pumpCts.Token);

            try
            {
                await process.WaitForExitAsync(linkedCts.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                cancelled = true;
                await TerminateProcessTreeAsync(process, _cancelGrace).ConfigureAwait(false);
            }

            await DrainOutputAsync(
                process, stdoutTask, stderrTask, pumpCts, _cancelGrace).ConfigureAwait(false);

            stopwatch.Stop();
            var exitCode = TryGetExitCode(process);
            _logger.Info(
                $"Command completed. exitCode={exitCode} cancelled={cancelled} " +
                $"usedWinRm={useWinRm} durationMs={stopwatch.ElapsedMilliseconds}");
            return new CommandResult(
                exitCode, cancelled, useWinRm, stopwatch.ElapsedMilliseconds);
        }
        finally
        {
            if (started && !HasExited(process))
            {
                await TerminateProcessTreeAsync(process, _cancelGrace).ConfigureAwait(false);
            }

            pumpCts.Cancel();
            if (started)
            {
                CloseReader(process.StandardOutput);
                CloseReader(process.StandardError);
                await AwaitPumpShutdownAsync(stdoutTask, stderrTask, _cancelGrace)
                    .ConfigureAwait(false);
            }
            ClearCurrentProcess(process);
        }
    }

    public async Task StopCurrentProcessAsync()
    {
        Process? process;
        lock (_processGate)
        {
            process = _currentProcess;
        }

        if (process is null)
        {
            return;
        }

        await TerminateProcessTreeAsync(process, _cancelGrace).ConfigureAwait(false);
    }

    private void SetCurrentProcess(Process process)
    {
        lock (_processGate)
        {
            _currentProcess = process;
        }
    }

    private void ClearCurrentProcess(Process process)
    {
        lock (_processGate)
        {
            if (ReferenceEquals(_currentProcess, process))
            {
                _currentProcess = null;
            }
        }
    }

    private static async Task PumpAsync(
        StreamReader reader,
        string stream,
        Func<string, string, Task> onOutput,
        CancellationToken cancellationToken)
    {
        while (true)
        {
            string? line;
            try
            {
                line = await reader.ReadLineAsync(cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                break;
            }
            catch (ObjectDisposedException) when (cancellationToken.IsCancellationRequested)
            {
                break;
            }
            catch (IOException) when (cancellationToken.IsCancellationRequested)
            {
                break;
            }

            if (line is null)
            {
                break;
            }

            await onOutput(stream, line + Environment.NewLine).ConfigureAwait(false);
        }
    }

    private static async Task DrainOutputAsync(
        Process process,
        Task stdoutTask,
        Task stderrTask,
        CancellationTokenSource pumpCts,
        TimeSpan grace)
    {
        var pumps = Task.WhenAll(stdoutTask, stderrTask);
        try
        {
            await pumps.WaitAsync(grace).ConfigureAwait(false);
            return;
        }
        catch (OperationCanceledException) when (pumpCts.IsCancellationRequested)
        {
            return;
        }
        catch (TimeoutException)
        {
            // A child may have inherited the redirected handles after the direct
            // PowerShell process exited. Do not let it hold the CRC command lane.
            pumpCts.Cancel();
            CloseReader(process.StandardOutput);
            CloseReader(process.StandardError);
        }

        try
        {
            await pumps.WaitAsync(grace).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (pumpCts.IsCancellationRequested)
        {
        }
        catch (ObjectDisposedException) when (pumpCts.IsCancellationRequested)
        {
        }
        catch (IOException) when (pumpCts.IsCancellationRequested)
        {
        }
    }

    private static async Task AwaitPumpShutdownAsync(
        Task? stdoutTask,
        Task? stderrTask,
        TimeSpan grace)
    {
        if (stdoutTask is null || stderrTask is null)
        {
            return;
        }

        try
        {
            await Task.WhenAll(stdoutTask, stderrTask).WaitAsync(grace).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
        }
        catch (ObjectDisposedException)
        {
        }
        catch (IOException)
        {
        }
        catch (TimeoutException)
        {
        }
        catch
        {
            // A pump callback failure is surfaced by the main execution path.
            // Cleanup must still finish without masking or retaining the process.
        }
    }

    private static async Task TerminateProcessTreeAsync(Process process, TimeSpan grace)
    {
        if (HasExited(process))
        {
            return;
        }

        try
        {
            process.CloseMainWindow();
            using var gracefulCts = new CancellationTokenSource(grace);
            await process.WaitForExitAsync(gracefulCts.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
        }
        catch (InvalidOperationException)
        {
        }

        if (!HasExited(process))
        {
            try
            {
                process.Kill(entireProcessTree: true);
            }
            catch (InvalidOperationException)
            {
            }
            catch (System.ComponentModel.Win32Exception)
            {
                if (!HasExited(process))
                {
                    throw;
                }
            }
        }

        if (!HasExited(process))
        {
            await process.WaitForExitAsync(CancellationToken.None)
                .WaitAsync(TimeSpan.FromSeconds(10)).ConfigureAwait(false);
        }
    }

    private static bool HasExited(Process process)
    {
        try
        {
            return process.HasExited;
        }
        catch (InvalidOperationException)
        {
            return false;
        }
    }

    private static int TryGetExitCode(Process process)
    {
        try
        {
            return process.HasExited ? process.ExitCode : -1;
        }
        catch (InvalidOperationException)
        {
            return -1;
        }
    }

    private static void CloseReader(StreamReader reader)
    {
        try
        {
            reader.Close();
        }
        catch
        {
        }
    }
}

public static class PowerShellScripts
{
    public const string PowerShellExe = "powershell.exe";

    public static string PrepareCommand(string command)
    {
        return "$ProgressPreference = 'SilentlyContinue'" + Environment.NewLine
               + "[Console]::OutputEncoding = [System.Text.UTF8Encoding]::new($false)"
               + Environment.NewLine
               + "$OutputEncoding = [Console]::OutputEncoding" + Environment.NewLine
               + command;
    }

    public static string WrapForLocalWinRm(string command)
    {
        return "Invoke-Command -ComputerName localhost -ScriptBlock {" + Environment.NewLine
               + command + Environment.NewLine
               + "} -ErrorAction Stop";
    }

    public static string EncodeCommand(string script)
    {
        return Convert.ToBase64String(Encoding.Unicode.GetBytes(script));
    }

    public static ProcessStartInfo CreateStartInfo(string script)
    {
        var startInfo = new ProcessStartInfo
        {
            FileName = PowerShellExe,
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8
        };
        startInfo.ArgumentList.Add("-NoLogo");
        startInfo.ArgumentList.Add("-NoProfile");
        startInfo.ArgumentList.Add("-NonInteractive");
        startInfo.ArgumentList.Add("-ExecutionPolicy");
        startInfo.ArgumentList.Add("Bypass");
        startInfo.ArgumentList.Add("-EncodedCommand");
        startInfo.ArgumentList.Add(EncodeCommand(script));
        return startInfo;
    }
}

public static class WinRmProbe
{
    public static async Task<bool> IsLocalWinRmAvailableAsync(CancellationToken cancellationToken)
    {
        var probe = PowerShellScripts.PrepareCommand(
            "Invoke-Command -ComputerName localhost -ScriptBlock { $null = 1 } -ErrorAction Stop");
        var startInfo = PowerShellScripts.CreateStartInfo(probe);

        try
        {
            using var process = Process.Start(startInfo);
            if (process is null)
            {
                return false;
            }

            using var timeoutCts =
                CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeoutCts.CancelAfter(TimeSpan.FromSeconds(3));
            try
            {
                await process.WaitForExitAsync(timeoutCts.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                await KillProbeAsync(process).ConfigureAwait(false);
                return false;
            }

            return process.ExitCode == 0;
        }
        catch
        {
            return false;
        }
    }

    private static async Task KillProbeAsync(Process process)
    {
        try
        {
            if (!process.HasExited)
            {
                process.Kill(entireProcessTree: true);
            }
        }
        catch (InvalidOperationException)
        {
        }
        catch (System.ComponentModel.Win32Exception)
        {
            if (!process.HasExited)
            {
                throw;
            }
        }

        try
        {
            if (!process.HasExited)
            {
                await process.WaitForExitAsync(CancellationToken.None)
                    .WaitAsync(TimeSpan.FromSeconds(10)).ConfigureAwait(false);
            }
        }
        catch
        {
        }
    }
}
