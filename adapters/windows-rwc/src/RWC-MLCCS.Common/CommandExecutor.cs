using System.Diagnostics;
using System.Text;

namespace RWC_MLCCS.Common;

public sealed record CommandResult(int ExitCode, bool Cancelled, bool UsedWinRm, long DurationMs);

public sealed class CommandExecutor
{
    private readonly FileLogger _logger;
    private readonly TimeSpan _cancelGrace;
    private Process? _currentProcess;

    public CommandExecutor(FileLogger logger, TimeSpan cancelGrace)
    {
        _logger = logger;
        _cancelGrace = cancelGrace;
    }

    public async Task<CommandResult> ExecuteAsync(
        string command,
        bool allowLocalPowerShellFallback,
        int timeoutSeconds,
        Func<string, string, Task> onOutput,
        CancellationToken cancellationToken)
    {
        var useWinRm = await WinRmProbe.IsLocalWinRmAvailableAsync(cancellationToken).ConfigureAwait(false);
        if (!useWinRm && !allowLocalPowerShellFallback)
        {
            throw new InvalidOperationException("Local WinRM is unavailable and local PowerShell fallback is disabled.");
        }

        var script = useWinRm ? PowerShellScripts.WrapForLocalWinRm(command) : command;
        var stopwatch = Stopwatch.StartNew();
        var timeout = TimeSpan.FromSeconds(Math.Max(1, timeoutSeconds));
        using var timeoutCts = new CancellationTokenSource(timeout);
        using var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, timeoutCts.Token);

        var startInfo = new ProcessStartInfo
        {
            FileName = PowerShellScripts.PowerShellExe,
            UseShellExecute = false,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8
        };
        startInfo.ArgumentList.Add("-NoProfile");
        startInfo.ArgumentList.Add("-ExecutionPolicy");
        startInfo.ArgumentList.Add("Bypass");
        startInfo.ArgumentList.Add("-Command");
        startInfo.ArgumentList.Add("-");

        using var process = new Process { StartInfo = startInfo, EnableRaisingEvents = true };
        _currentProcess = process;

        _logger.Info($"Starting command. usedWinRm={useWinRm} timeout={timeoutSeconds}s command={command}");
        process.Start();
        await process.StandardInput.WriteLineAsync(script).ConfigureAwait(false);
        process.StandardInput.Close();

        var stdoutTask = PumpAsync(process.StandardOutput, "stdout", onOutput, linkedCts.Token);
        var stderrTask = PumpAsync(process.StandardError, "stderr", onOutput, linkedCts.Token);
        var cancelled = false;

        try
        {
            await process.WaitForExitAsync(linkedCts.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            cancelled = true;
            await StopCurrentProcessAsync().ConfigureAwait(false);
        }

        await Task.WhenAll(Swallow(stdoutTask), Swallow(stderrTask)).ConfigureAwait(false);
        stopwatch.Stop();

        var exitCode = process.HasExited ? process.ExitCode : -1;
        _logger.Info($"Command completed. exitCode={exitCode} cancelled={cancelled} usedWinRm={useWinRm} durationMs={stopwatch.ElapsedMilliseconds}");
        _currentProcess = null;
        return new CommandResult(exitCode, cancelled, useWinRm, stopwatch.ElapsedMilliseconds);
    }

    public async Task StopCurrentProcessAsync()
    {
        var process = _currentProcess;
        if (process is null || process.HasExited)
        {
            return;
        }

        try
        {
            process.CloseMainWindow();
            using var graceCts = new CancellationTokenSource(_cancelGrace);
            await process.WaitForExitAsync(graceCts.Token).ConfigureAwait(false);
        }
        catch
        {
            // Console PowerShell usually has no main window; fall through to tree kill.
        }

        if (!process.HasExited)
        {
            try
            {
                process.Kill(entireProcessTree: true);
            }
            catch (InvalidOperationException)
            {
            }
        }
    }

    private static async Task PumpAsync(
        StreamReader reader,
        string stream,
        Func<string, string, Task> onOutput,
        CancellationToken cancellationToken)
    {
        while (!reader.EndOfStream && !cancellationToken.IsCancellationRequested)
        {
            var line = await reader.ReadLineAsync(cancellationToken).ConfigureAwait(false);
            if (line is not null)
            {
                await onOutput(stream, line + Environment.NewLine).ConfigureAwait(false);
            }
        }
    }

    private static async Task Swallow(Task task)
    {
        try
        {
            await task.ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
        }
    }
}

public static class PowerShellScripts
{
    public const string PowerShellExe = "powershell.exe";

    public static string WrapForLocalWinRm(string command)
    {
        return "Invoke-Command -ComputerName localhost -ScriptBlock {" + Environment.NewLine
               + command + Environment.NewLine
               + "}";
    }
}

public static class WinRmProbe
{
    public static async Task<bool> IsLocalWinRmAvailableAsync(CancellationToken cancellationToken)
    {
        var startInfo = new ProcessStartInfo
        {
            FileName = PowerShellScripts.PowerShellExe,
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true
        };
        startInfo.ArgumentList.Add("-NoProfile");
        startInfo.ArgumentList.Add("-Command");
        startInfo.ArgumentList.Add("Test-WSMan -ComputerName localhost | Out-Null");

        try
        {
            using var process = Process.Start(startInfo);
            if (process is null)
            {
                return false;
            }

            using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeoutCts.CancelAfter(TimeSpan.FromSeconds(3));
            await process.WaitForExitAsync(timeoutCts.Token).ConfigureAwait(false);
            return process.ExitCode == 0;
        }
        catch
        {
            return false;
        }
    }
}
