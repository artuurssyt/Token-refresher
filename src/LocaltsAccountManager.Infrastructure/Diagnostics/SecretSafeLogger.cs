using System.Globalization;
using LocaltsAccountManager.Infrastructure.Paths;

namespace LocaltsAccountManager.Infrastructure.Diagnostics;

public sealed class SecretSafeLogger
{
    /// <summary>Roll the log once it passes this size so it cannot grow without bound.</summary>
    private const long MaxLogBytes = 5 * 1024 * 1024;

    private readonly object _sync = new();
    private readonly string _logPath;

    public SecretSafeLogger()
    {
        _logPath = Path.Combine(ApplicationPaths.LocalAppDataRoot, "logs", "application.log");
    }

    public void LogOperation(string service, string operation, int status, TimeSpan? retryAfter, Guid? recordId)
    {
        var retry = retryAfter.HasValue
            ? string.Create(CultureInfo.InvariantCulture, $"{retryAfter.Value.TotalSeconds:0} seconds")
            : "none";
        var record = recordId.HasValue ? recordId.Value.ToString() : "n/a";
        Write($"Service: {service}; Operation: {operation}; Status: {status}; Retry-After: {retry}; Record: {record}");
    }

    public void LogInfo(string message) => Write("INFO: " + message);

    /// <summary>
    /// Appends a line, swallowing I/O problems. This logger is called from catch blocks in the
    /// processors, so throwing here would replace the real failure with a logging failure.
    /// </summary>
    private void Write(string message)
    {
        var line = string.Create(
            CultureInfo.InvariantCulture,
            $"[{DateTimeOffset.UtcNow:O}] {message}{Environment.NewLine}");

        lock (_sync)
        {
            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(_logPath)!);
                RollIfTooLarge();
                File.AppendAllText(_logPath, line);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or NotSupportedException)
            {
            }
        }
    }

    private void RollIfTooLarge()
    {
        var info = new FileInfo(_logPath);
        if (!info.Exists || info.Length < MaxLogBytes)
        {
            return;
        }

        File.Move(_logPath, _logPath + ".1", overwrite: true);
    }
}
