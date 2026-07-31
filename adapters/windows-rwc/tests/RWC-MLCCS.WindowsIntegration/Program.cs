using System.Collections.Concurrent;
using System.Diagnostics;
using System.Text.Json;
using RWC_MLCCS.Common;

if (!OperatingSystem.IsWindows())
{
    Console.Error.WriteLine("This integration harness must run on Windows.");
    return 2;
}

var root = Path.Combine(
    Path.GetTempPath(), "rwc-windows-integration", Guid.NewGuid().ToString("N"));
Directory.CreateDirectory(root);
var results = new List<object>();

try
{
    TestLogger(root, results);
    await TestUnicodeAndProgressAsync(root, results);
    await TestActualWinRmSelectionAsync(root, results);
    await TestTimeoutRecoveryAsync(root, results);
    await TestInheritedHandleRecoveryAsync(root, results);
    await TestConcurrentRejectionAsync(root, results);
    await TestOutputCallbackFailureRecoveryAsync(root, results);

    Console.WriteLine(JsonSerializer.Serialize(new
    {
        ok = true,
        platform = Environment.OSVersion.VersionString,
        checks = results
    }));
    return 0;
}
catch (Exception ex)
{
    Console.Error.WriteLine(JsonSerializer.Serialize(new
    {
        ok = false,
        error = ex.Message,
        type = ex.GetType().FullName,
        checks = results
    }));
    return 1;
}
finally
{
    try
    {
        if (Directory.Exists(root))
        {
            Directory.Delete(root, recursive: true);
        }
    }
    catch
    {
    }
}

static void TestLogger(string root, List<object> results)
{
    var logDirectory = Path.Combine(root, "logger", "logs");
    var logger = new FileLogger("integration", logDirectory);
    logger.Info("first");
    Directory.Delete(logDirectory, recursive: true);
    logger.Info("second");
    Check(Directory.GetFiles(logDirectory, "integration-*.log").Length == 1,
        "Logger did not recreate its directory.");

    var occupied = Path.Combine(root, "logger", "occupied");
    File.WriteAllText(occupied, "file");
    var failingLogger = new FileLogger("integration", occupied);
    failingLogger.Error("expected write failure");
    results.Add(new { name = "logger-recovery", ok = true });
}

static async Task TestUnicodeAndProgressAsync(string root, List<object> results)
{
    var executor = CreateFallbackExecutor(root, "unicode");
    var capture = await RunCaptureAsync(
        executor,
        "Write-Output '中文 CRC 输出'; Write-Output 'FINITE-BEGIN'; " +
        "Get-Process | Select-Object -First 20 Name,Id | ConvertTo-Csv -NoTypeInformation; " +
        "Write-Output 'FINITE-END'",
        10);
    Check(capture.Result.ExitCode == 0 && !capture.Result.Cancelled,
        "Unicode command did not complete successfully.");
    Check(capture.Stdout.Contains("中文 CRC 输出", StringComparison.Ordinal),
        "Unicode output was not preserved.");
    Check(!capture.Stderr.Contains("#< CLIXML", StringComparison.OrdinalIgnoreCase),
        "Progress CLIXML leaked to stderr.");
    Check(capture.Stdout.Contains("FINITE-BEGIN", StringComparison.Ordinal) &&
          capture.Stdout.Contains("FINITE-END", StringComparison.Ordinal) &&
          capture.Stdout.Split(Environment.NewLine, StringSplitOptions.RemoveEmptyEntries).Length >= 5,
        "Finite process output was incomplete.");
    results.Add(new
    {
        name = "unicode-progress-finite-output",
        ok = true,
        capture.Result.DurationMs
    });
}

static async Task TestActualWinRmSelectionAsync(string root, List<object> results)
{
    var probeStopwatch = Stopwatch.StartNew();
    var available = await WinRmProbe.IsLocalWinRmAvailableAsync(CancellationToken.None);
    probeStopwatch.Stop();
    Check(probeStopwatch.Elapsed < TimeSpan.FromSeconds(5),
        "WinRM probe exceeded its bounded window.");

    var executor = new CommandExecutor(
        new FileLogger("actual-probe", Path.Combine(root, "actual-probe")),
        TimeSpan.FromMilliseconds(500));
    var capture = await RunCaptureAsync(executor, "Write-Output 'PROBE-PATH-OK'", 10);
    Check(capture.Result.ExitCode == 0 && !capture.Result.Cancelled,
        "Command failed after actual WinRM selection.");
    Check(capture.Stdout.Contains("PROBE-PATH-OK", StringComparison.Ordinal),
        "Actual WinRM selection command output was missing.");
    Check(capture.Result.UsedWinRm == available,
        "Command path disagreed with the real WinRM probe.");
    results.Add(new
    {
        name = "actual-winrm-probe",
        ok = true,
        available,
        probeMs = probeStopwatch.ElapsedMilliseconds,
        usedWinRm = capture.Result.UsedWinRm
    });
}

static async Task TestTimeoutRecoveryAsync(string root, List<object> results)
{
    var executor = CreateFallbackExecutor(root, "timeout");
    var marker = Path.Combine(root, "orphan-marker.txt");
    var childScript = "Start-Sleep -Seconds 4; " +
                      $"[IO.File]::WriteAllText('{PsEscape(marker)}','orphan')";
    var childEncoded = PowerShellScripts.EncodeCommand(childScript);
    var command =
        "$child = Start-Process powershell.exe " +
        $"-ArgumentList '-NoLogo','-NoProfile','-EncodedCommand','{childEncoded}' " +
        "-PassThru; Wait-Process -Id $child.Id";

    var timedOut = await RunCaptureAsync(executor, command, 1);
    Check(timedOut.Result.Cancelled, "Timeout did not return a cancelled completion.");

    var recovered = await RunCaptureAsync(
        executor, "Write-Output 'AFTER-TIMEOUT'", 10);
    Check(recovered.Result.ExitCode == 0 && !recovered.Result.Cancelled,
        "Command lane did not recover after timeout.");
    Check(recovered.Stdout.Contains("AFTER-TIMEOUT", StringComparison.Ordinal),
        "Recovery command output was missing.");
    await Task.Delay(TimeSpan.FromSeconds(5));
    Check(!File.Exists(marker), "A timed-out child process survived tree termination.");
    results.Add(new
    {
        name = "timeout-tree-kill-recovery",
        ok = true,
        timeoutMs = timedOut.Result.DurationMs,
        recoveryMs = recovered.Result.DurationMs
    });
}

static async Task TestInheritedHandleRecoveryAsync(string root, List<object> results)
{
    var executor = CreateFallbackExecutor(root, "inherited-handle");
    var childEncoded = PowerShellScripts.EncodeCommand("Start-Sleep -Seconds 5");
    var command =
        "Start-Process powershell.exe " +
        $"-ArgumentList '-NoLogo','-NoProfile','-EncodedCommand','{childEncoded}' " +
        "| Out-Null; Write-Output 'PARENT-RETURNED'";
    var stopwatch = Stopwatch.StartNew();
    var launched = await RunCaptureAsync(executor, command, 10);
    stopwatch.Stop();
    Check(launched.Result.ExitCode == 0 && !launched.Result.Cancelled,
        "Detached-child launch command failed.");
    Check(launched.Stdout.Contains("PARENT-RETURNED", StringComparison.Ordinal),
        "Detached-child parent output was missing.");
    Check(stopwatch.Elapsed < TimeSpan.FromSeconds(4),
        "Inherited output handles held the command lane open.");

    var recovered = await RunCaptureAsync(
        executor, "Write-Output 'AFTER-DETACH'", 10);
    Check(recovered.Stdout.Contains("AFTER-DETACH", StringComparison.Ordinal),
        "Command lane did not recover after a detached child.");
    await Task.Delay(TimeSpan.FromSeconds(5));
    results.Add(new
    {
        name = "inherited-handle-recovery",
        ok = true,
        elapsedMs = stopwatch.ElapsedMilliseconds
    });
}

static async Task TestConcurrentRejectionAsync(string root, List<object> results)
{
    var executor = CreateFallbackExecutor(root, "concurrency");
    using var cancellation = new CancellationTokenSource();
    var first = executor.ExecuteAsync(
        "Start-Sleep -Seconds 10",
        true,
        30,
        IgnoreOutput,
        cancellation.Token);
    await Task.Delay(500);

    var rejected = false;
    try
    {
        await executor.ExecuteAsync(
            "Write-Output 'must-not-run'",
            true,
            5,
            IgnoreOutput,
            CancellationToken.None);
    }
    catch (InvalidOperationException)
    {
        rejected = true;
    }
    Check(rejected, "Concurrent command was not rejected.");
    cancellation.Cancel();
    var cancelled = await first;
    Check(cancelled.Cancelled, "Cancelled primary command did not complete as cancelled.");
    results.Add(new { name = "concurrent-command-rejection", ok = true });
}

static async Task TestOutputCallbackFailureRecoveryAsync(
    string root, List<object> results)
{
    var executor = CreateFallbackExecutor(root, "callback-failure");
    var failed = false;
    try
    {
        await executor.ExecuteAsync(
            "Write-Output 'trigger-callback'",
            true,
            10,
            (_, _) => throw new InvalidOperationException("synthetic callback failure"),
            CancellationToken.None);
    }
    catch (InvalidOperationException ex) when (
        ex.Message.Contains("synthetic callback failure", StringComparison.Ordinal))
    {
        failed = true;
    }
    Check(failed, "Synthetic output callback failure was not surfaced.");

    var recovered = await RunCaptureAsync(
        executor, "Write-Output 'AFTER-CALLBACK-FAILURE'", 10);
    Check(recovered.Stdout.Contains(
            "AFTER-CALLBACK-FAILURE", StringComparison.Ordinal),
        "Executor remained poisoned after output callback failure.");
    results.Add(new { name = "callback-failure-recovery", ok = true });
}

static CommandExecutor CreateFallbackExecutor(string root, string name)
{
    return new CommandExecutor(
        new FileLogger(name, Path.Combine(root, name)),
        TimeSpan.FromMilliseconds(500),
        _ => Task.FromResult(false));
}

static async Task<Capture> RunCaptureAsync(
    CommandExecutor executor, string command, int timeoutSeconds)
{
    var stdout = new ConcurrentQueue<string>();
    var stderr = new ConcurrentQueue<string>();
    var result = await executor.ExecuteAsync(
        command,
        true,
        timeoutSeconds,
        (stream, text) =>
        {
            (stream == "stdout" ? stdout : stderr).Enqueue(text);
            return Task.CompletedTask;
        },
        CancellationToken.None);
    return new Capture(result, string.Concat(stdout), string.Concat(stderr));
}

static Task IgnoreOutput(string stream, string text)
{
    return Task.CompletedTask;
}

static string PsEscape(string value)
{
    return value.Replace("'", "''", StringComparison.Ordinal);
}

static void Check(bool condition, string message)
{
    if (!condition)
    {
        throw new InvalidOperationException(message);
    }
}

internal sealed record Capture(CommandResult Result, string Stdout, string Stderr);
