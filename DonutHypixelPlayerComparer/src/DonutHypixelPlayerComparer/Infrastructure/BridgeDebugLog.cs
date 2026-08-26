using System.Text;

namespace DonutHypixelPlayerComparer.Infrastructure;

/// <summary>Appends bridge traffic to a rolling file so client payload problems can be diagnosed after a scan.</summary>
public static class BridgeDebugLog
{
    private const long MaxBytes = 2_000_000;
    private static readonly object Gate = new();

    public static void Write(string message, string? detail = null)
    {
        try
        {
            lock (Gate)
            {
                AppPaths.EnsureCreated();
                var file = new FileInfo(AppPaths.BridgeLogFile);
                if (file.Exists && file.Length > MaxBytes) file.Delete();
                var text = new StringBuilder()
                    .Append('[').Append(DateTimeOffset.Now.ToString("HH:mm:ss.fff")).Append("] ")
                    .Append(message);
                if (!string.IsNullOrWhiteSpace(detail))
                    text.AppendLine().Append("    ").Append(detail.ReplaceLineEndings(" "));
                File.AppendAllText(AppPaths.BridgeLogFile, text.AppendLine().ToString());
            }
        }
        catch
        {
            // Diagnostics must never break a scan.
        }
    }

    public static void StartSession(string header)
    {
        Write(new string('-', 60));
        Write(header);
    }

    public static string Preview(string? body, int maxLength = 600)
    {
        if (string.IsNullOrEmpty(body)) return "<empty>";
        var single = body.ReplaceLineEndings(" ");
        return single.Length <= maxLength ? single : single[..maxLength] + "… (truncated)";
    }
}
