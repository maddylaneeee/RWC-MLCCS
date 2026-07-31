using RWC_MLCCS.Common;

namespace RWC_MLCCS.Tests;

public sealed class FileLoggerTests
{
    [Fact]
    public void LoggerRecreatesDeletedDirectory()
    {
        var root = Path.Combine(
            Path.GetTempPath(), "rwc-logger-tests", Guid.NewGuid().ToString("N"));
        var logDirectory = Path.Combine(root, "logs");
        try
        {
            var logger = new FileLogger("operator", logDirectory);
            logger.Info("first");
            Directory.Delete(logDirectory, recursive: true);

            logger.Info("second");

            var log = Assert.Single(Directory.GetFiles(logDirectory, "operator-*.log"));
            Assert.Contains("second", File.ReadAllText(log));
        }
        finally
        {
            if (Directory.Exists(root))
            {
                Directory.Delete(root, recursive: true);
            }
        }
    }

    [Fact]
    public void LoggerWriteFailureDoesNotEscape()
    {
        var root = Path.Combine(
            Path.GetTempPath(), "rwc-logger-tests", Guid.NewGuid().ToString("N"));
        try
        {
            Directory.CreateDirectory(root);
            var fileInsteadOfDirectory = Path.Combine(root, "not-a-directory");
            File.WriteAllText(fileInsteadOfDirectory, "occupied");
            var logger = new FileLogger("operator", fileInsteadOfDirectory);

            var exception = Record.Exception(() => logger.Error("cannot append"));

            Assert.Null(exception);
        }
        finally
        {
            if (Directory.Exists(root))
            {
                Directory.Delete(root, recursive: true);
            }
        }
    }
}
