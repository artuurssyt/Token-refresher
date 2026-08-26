using System.Globalization;
using System.Text;
using System.Text.Json;
using DonutHypixelPlayerComparer.Models;

namespace DonutHypixelPlayerComparer.Services;

public static partial class ExportService
{
    public static Task ExportCsvAsync(string path, IReadOnlyList<PlayerScanResult> results, CancellationToken token) =>
        File.WriteAllTextAsync(path, BuildCsv(results), new UTF8Encoding(true), token);

    public static Task ExportJsonAsync(string path, IReadOnlyList<PlayerScanResult> results, CancellationToken token) =>
        File.WriteAllTextAsync(path, JsonSerializer.Serialize(results,
            new JsonSerializerOptions { WriteIndented = true }), new UTF8Encoding(false), token);

    public static Task ExportExcelAsync(string path, IReadOnlyList<PlayerScanResult> results, CancellationToken token) =>
        Task.Run(() => BuildExcel(path, results), token);

    private static string BuildCsv(IReadOnlyList<PlayerScanResult> results)
    {
        var builder = new StringBuilder();
        builder.AppendLine(string.Join(',', Columns.Select(column => Escape(column.Header))));
        foreach (var row in results)
            builder.AppendLine(string.Join(',', Columns.Select(column => Escape(Format(column.Value(row))))));
        return builder.ToString();
    }

    private static string Escape(string value)
    {
        if (value.IndexOfAny([',', (char)34, '\r', '\n']) < 0) return value;
        var quote = ((char)34).ToString();
        return quote + value.Replace(quote, quote + quote) + quote;
    }

    private static string Format(object? value) => value switch
    {
        null => string.Empty,
        IFormattable formattable => formattable.ToString(null, CultureInfo.InvariantCulture) ?? string.Empty,
        _ => value.ToString() ?? string.Empty
    };
}
