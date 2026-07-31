using System.Diagnostics;

namespace RWC_MLCCS.Common;

public sealed class FileLogger
{
    private readonly object _gate = new();
    private readonly string _directory;
    private readonly string _path;

    public FileLogger(string name, string? logDirectory = null)
    {
        _directory = logDirectory ?? AppPaths.LogDirectory;
        _path = Path.Combine(_directory, $"{name}-{DateTimeOffset.Now:yyyyMMdd}.log");
    }

    public void Info(string message) => Write("INFO", message);

    public void Error(string message, Exception? exception = null)
    {
        Write("ERROR", exception is null ? message : $"{message}{Environment.NewLine}{exception}");
    }

    public void Write(string level, string message)
    {
        var line = $"{DateTimeOffset.Now:O} [{level}] {message}{Environment.NewLine}";
        lock (_gate)
        {
            try
            {
                Directory.CreateDirectory(_directory);
                File.AppendAllText(_path, line);
            }
            catch (Exception ex) when (
                ex is IOException or
                UnauthorizedAccessException or
                NotSupportedException or
                System.Security.SecurityException)
            {
                // Logging is diagnostic. A deleted/unwritable artifact log
                // directory must not terminate the broker control path.
                Trace.WriteLine($"RWC FileLogger write failed: {ex.GetType().Name}");
            }
        }
    }
}
