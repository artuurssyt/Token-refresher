using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Xml;
using DonutHypixelPlayerComparer.Models;

namespace DonutHypixelPlayerComparer.Services;

public static partial class ExportService
{
    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };

    public static Task ExportCsvAsync(string path, IReadOnlyList<PlayerScanResult> results, CancellationToken token) =>
        WriteAtomicAsync(path, (temporary, cancellation) =>
            File.WriteAllTextAsync(temporary, BuildCsv(results), new UTF8Encoding(true), cancellation), token);

    public static Task ExportJsonAsync(string path, IReadOnlyList<PlayerScanResult> results, CancellationToken token) =>
        WriteAtomicAsync(path, (temporary, cancellation) =>
            File.WriteAllTextAsync(temporary, JsonSerializer.Serialize(results, JsonOptions),
                new UTF8Encoding(false), cancellation), token);

    public static Task ExportExcelAsync(string path, IReadOnlyList<PlayerScanResult> results, CancellationToken token) =>
        WriteAtomicAsync(path, (temporary, cancellation) =>
            Task.Run(() => BuildExcel(temporary, results), cancellation), token);

    /// <summary>
    /// Builds into a sibling temporary file and moves it into place, so a failed or cancelled export
    /// cannot truncate a previous export, and a target still locked by Excel fails before any data is lost.
    /// </summary>
    private static async Task WriteAtomicAsync(string path, Func<string, CancellationToken, Task> build,
        CancellationToken token)
    {
        var directory = Path.GetDirectoryName(Path.GetFullPath(path));
        if (!string.IsNullOrEmpty(directory)) Directory.CreateDirectory(directory);
        var temporary = path + "." + Guid.NewGuid().ToString("N")[..8] + ".tmp";
        try
        {
            await build(temporary, token).ConfigureAwait(false);
            File.Move(temporary, path, true);
        }
        finally
        {
            try { if (File.Exists(temporary)) File.Delete(temporary); }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
        }
    }

    private static string BuildCsv(IReadOnlyList<PlayerScanResult> results)
    {
        var builder = new StringBuilder();
        builder.AppendLine(string.Join(',', Columns.Select(column => Escape(column.Header))));
        foreach (var row in results)
            builder.AppendLine(string.Join(',', Columns.Select(column => Escape(Neutralize(Format(column.Value(row)))))));
        return builder.ToString();
    }

    private static string Escape(string value)
    {
        if (value.IndexOfAny([',', (char)34, '\r', '\n']) < 0) return value;
        var quote = ((char)34).ToString();
        return quote + value.Replace(quote, quote + quote) + quote;
    }

    /// <summary>
    /// API error text lands in the Error column verbatim, and a spreadsheet treats a leading
    /// =, +, - or @ as a formula, so those cells are prefixed to keep them inert text.
    /// </summary>
    internal static string Neutralize(string value) =>
        value.Length > 0 && value[0] is '=' or '+' or '-' or '@' or '\t' or '\r' ? "'" + value : value;

    private static string Format(object? value) => value switch
    {
        null => string.Empty,
        IFormattable formattable => formattable.ToString(null, CultureInfo.InvariantCulture) ?? string.Empty,
        _ => value.ToString() ?? string.Empty
    };

    /// <summary>
    /// XmlWriter rejects control characters outright, so a stray one in an upstream error message
    /// would fail the whole workbook instead of one cell. Valid surrogate pairs are kept intact.
    /// </summary>
    internal static string StripInvalidXml(string value)
    {
        if (value.Length == 0) return value;
        StringBuilder? builder = null;
        for (var index = 0; index < value.Length; index++)
        {
            var character = value[index];
            var pairLength = 1;
            bool keep;
            if (char.IsHighSurrogate(character) && index + 1 < value.Length
                && char.IsLowSurrogate(value[index + 1]))
            {
                keep = XmlConvert.IsXmlSurrogatePair(value[index + 1], character);
                pairLength = 2;
            }
            else
            {
                keep = XmlConvert.IsXmlChar(character);
            }

            if (keep) builder?.Append(value, index, pairLength);
            else
            {
                builder ??= new StringBuilder(value.Length).Append(value, 0, index);
            }
            index += pairLength - 1;
        }
        return builder?.ToString() ?? value;
    }
}
