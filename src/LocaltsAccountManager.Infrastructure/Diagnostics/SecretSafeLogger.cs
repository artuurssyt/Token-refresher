using LocaltsAccountManager.Core.Configuration;
using LocaltsAccountManager.Core.Enums;
using LocaltsAccountManager.Core.Interfaces;
using LocaltsAccountManager.Core.Models;

namespace LocaltsAccountManager.Infrastructure.Diagnostics;

public sealed class SecretSafeLogger
{
    private readonly object _sync = new();
    private readonly string _logPath;

    public SecretSafeLogger()
    {
        var root = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "LocaltsAccountManager",
            "logs");
        Directory.CreateDirectory(root);
        _logPath = Path.Combine(root, "application.log");
    }

    public void LogOperation(string service, string operation, int status, TimeSpan? retryAfter, Guid? recordId)
    {
        var retry = retryAfter.HasValue ? $"{retryAfter.Value.TotalSeconds:0} seconds" : "none";
        var record = recordId.HasValue ? recordId.Value.ToString() : "n/a";
        var line = $"[{DateTimeOffset.UtcNow:O}] Service: {service}; Operation: {operation}; Status: {status}; Retry-After: {retry}; Record: {record}";
        lock (_sync)
        {
            File.AppendAllText(_logPath, line + Environment.NewLine);
        }
    }

    public void LogInfo(string message)
    {
        lock (_sync)
        {
            File.AppendAllText(_logPath, $"[{DateTimeOffset.UtcNow:O}] INFO: {message}{Environment.NewLine}");
        }
    }
}
