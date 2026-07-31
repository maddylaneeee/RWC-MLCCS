using System.Collections.Concurrent;
using System.Diagnostics;
using RWC_MLCCS.Common;

namespace RWC_MLCCS.Tests;

public sealed class CommandExecutorTests
{
    [Fact]
    public void PreparedCommandForcesUtf8AndSuppressesProgress()
    {
        var script = PowerShellScripts.PrepareCommand("Write-Output 'ok'");

        Assert.Contains("$ProgressPreference = 'SilentlyContinue'", script);
        Assert.Contains("[Console]::OutputEncoding", script);
        Assert.Contains("$OutputEncoding = [Console]::OutputEncoding", script);
        Assert.EndsWith("Write-Output 'ok'", script);
    }

    [Fact]
    public void StartInfoIsNonInteractiveAndUsesEncodedCommand()
    {
        const string script = "Write-Output '中文'";
        var startInfo = PowerShellScripts.CreateStartInfo(script);
        var arguments = startInfo.ArgumentList.ToArray();

        Assert.False(startInfo.UseShellExecute);
        Assert.True(startInfo.RedirectStandardOutput);
        Assert.True(startInfo.RedirectStandardError);
        Assert.Contains("-NonInteractive", arguments);
        var encodedIndex = Array.IndexOf(arguments, "-EncodedCommand");
        Assert.True(encodedIndex >= 0);
        Assert.Equal(script,
            System.Text.Encoding.Unicode.GetString(
                Convert.FromBase64String(arguments[encodedIndex + 1])));
    }

    [Fact]
    public async Task WindowsFallbackPreservesUnicodeAndSuppressesProgressCliXml()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        var directory = CreateTestDirectory();
        try
        {
            var logger = new FileLogger("unicode", directory);
            var executor = CreateFallbackExecutor(logger);
            var output = new ConcurrentQueue<(string Stream, string Text)>();

            var result = await executor.ExecuteAsync(
                "Write-Output '中文 CRC 输出'",
                true,
                10,
                (stream, text) =>
                {
                    output.Enqueue((stream, text));
                    return Task.CompletedTask;
                },
                CancellationToken.None);

            Assert.Equal(0, result.ExitCode);
            Assert.False(result.Cancelled);
            Assert.False(result.UsedWinRm);
            Assert.Contains("中文 CRC 输出",
                string.Concat(output.Where(x => x.Stream == "stdout").Select(x => x.Text)));
            Assert.DoesNotContain("#< CLIXML",
                string.Concat(output.Where(x => x.Stream == "stderr").Select(x => x.Text)));
        }
        finally
        {
            DeleteTestDirectory(directory);
        }
    }

    [Fact]
    public async Task WindowsTimeoutKillsChildTreeAndNextCommandRecovers()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        var directory = CreateTestDirectory();
        var marker = Path.Combine(directory, "orphan-marker.txt");
        try
        {
            var logger = new FileLogger("timeout", directory);
            var executor = CreateFallbackExecutor(logger);
            var inner = "Start-Sleep -Seconds 4; " +
                        $"[IO.File]::WriteAllText('{EscapePowerShell(marker)}','orphan')";
            var encodedInner = PowerShellScripts.EncodeCommand(inner);
            var command =
                "$child = Start-Process powershell.exe " +
                $"-ArgumentList '-NoLogo','-NoProfile','-EncodedCommand','{encodedInner}' " +
                "-PassThru; Wait-Process -Id $child.Id";

            var timedOut = await executor.ExecuteAsync(
                command, true, 1, IgnoreOutput, CancellationToken.None);
            Assert.True(timedOut.Cancelled);

            var output = new ConcurrentQueue<string>();
            var recovered = await executor.ExecuteAsync(
                "Write-Output 'AFTER-TIMEOUT'",
                true,
                10,
                (_, text) =>
                {
                    output.Enqueue(text);
                    return Task.CompletedTask;
                },
                CancellationToken.None);

            Assert.Equal(0, recovered.ExitCode);
            Assert.False(recovered.Cancelled);
            Assert.Contains("AFTER-TIMEOUT", string.Concat(output));

            await Task.Delay(TimeSpan.FromSeconds(5));
            Assert.False(File.Exists(marker));
        }
        finally
        {
            DeleteTestDirectory(directory);
        }
    }

    [Fact]
    public async Task WindowsExecutorRejectsConcurrentCommand()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        var directory = CreateTestDirectory();
        try
        {
            var logger = new FileLogger("concurrent", directory);
            var executor = CreateFallbackExecutor(logger);
            using var cancellation = new CancellationTokenSource();
            var first = executor.ExecuteAsync(
                "Start-Sleep -Seconds 10",
                true,
                30,
                IgnoreOutput,
                cancellation.Token);
            await Task.Delay(500);

            await Assert.ThrowsAsync<InvalidOperationException>(() =>
                executor.ExecuteAsync(
                    "Write-Output 'must-not-run'",
                    true,
                    5,
                    IgnoreOutput,
                    CancellationToken.None));

            cancellation.Cancel();
            var cancelled = await first;
            Assert.True(cancelled.Cancelled);
        }
        finally
        {
            DeleteTestDirectory(directory);
        }
    }

    private static CommandExecutor CreateFallbackExecutor(FileLogger logger)
    {
        return new CommandExecutor(
            logger,
            TimeSpan.FromMilliseconds(300),
            _ => Task.FromResult(false));
    }

    private static Task IgnoreOutput(string stream, string text)
    {
        return Task.CompletedTask;
    }

    private static string EscapePowerShell(string value)
    {
        return value.Replace("'", "''", StringComparison.Ordinal);
    }

    private static string CreateTestDirectory()
    {
        var path = Path.Combine(
            Path.GetTempPath(), "rwc-command-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(path);
        return path;
    }

    private static void DeleteTestDirectory(string path)
    {
        try
        {
            if (Directory.Exists(path))
            {
                Directory.Delete(path, recursive: true);
            }
        }
        catch
        {
        }
    }
}
