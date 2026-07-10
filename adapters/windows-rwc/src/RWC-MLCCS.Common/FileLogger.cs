namespace RWC_MLCCS.Common;

public sealed class FileLogger
{
    private readonly object _gate = new();
    private readonly string _path;

    public FileLogger(string name)
    {
        _path = Path.Combine(AppPaths.LogDirectory, $"{name}-{DateTimeOffset.Now:yyyyMMdd}.log");
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
            File.AppendAllText(_path, line);
        }
    }
}
