using System.Globalization;
using System.IO.Compression;
using System.Text;
using System.Xml;
using DonutComparer.Core.Models;

namespace DonutComparer.Core.Services;

public static partial class ExportService
{
    private static void BuildExcel(string path, IReadOnlyList<PlayerScanResult> results)
    {
        using var file = File.Open(path, FileMode.Create, FileAccess.ReadWrite, FileShare.None);
        using var archive = new ZipArchive(file, ZipArchiveMode.Create);
        AddText(archive, "[Content_Types].xml", ContentTypes);
        AddText(archive, "_rels/.rels", PackageRelationships);
        AddText(archive, "xl/workbook.xml", Workbook);
        AddText(archive, "xl/_rels/workbook.xml.rels", WorkbookRelationships);
        AddText(archive, "xl/styles.xml", Styles);
        var entry = archive.CreateEntry("xl/worksheets/sheet1.xml", CompressionLevel.Optimal);
        using var stream = entry.Open();
        using var writer = XmlWriter.Create(stream, new XmlWriterSettings { Encoding = new UTF8Encoding(false) });
        writer.WriteStartDocument();
        writer.WriteStartElement("worksheet", "http://schemas.openxmlformats.org/spreadsheetml/2006/main");
        writer.WriteStartElement("sheetData");
        WriteRow(writer, 1, Columns.Select(column => (object?)column.Header).ToArray(), true);
        for (var index = 0; index < results.Count; index++)
            WriteRow(writer, index + 2, Columns.Select(column => column.Value(results[index])).ToArray(), false);
        writer.WriteEndElement();
        writer.WriteEndElement();
        writer.WriteEndDocument();
    }

    private static void WriteRow(XmlWriter writer, int rowNumber, object?[] values, bool header)
    {
        writer.WriteStartElement("row");
        writer.WriteAttributeString("r", rowNumber.ToString(CultureInfo.InvariantCulture));
        for (var index = 0; index < values.Length; index++)
        {
            writer.WriteStartElement("c");
            writer.WriteAttributeString("r", ColumnName(index + 1) + rowNumber);
            if (header) writer.WriteAttributeString("s", "1");
            var value = values[index];
            if (!header && value is byte or short or int or long or float or double or decimal)
                writer.WriteElementString("v", Format(value));
            else
            {
                writer.WriteAttributeString("t", "inlineStr");
                writer.WriteStartElement("is");
                writer.WriteStartElement("t");
                writer.WriteAttributeString("xml", "space", null, "preserve");
                writer.WriteString(Format(value));
                writer.WriteEndElement();
                writer.WriteEndElement();
            }
            writer.WriteEndElement();
        }
        writer.WriteEndElement();
    }

    private static string ColumnName(int number)
    {
        var result = string.Empty;
        while (number > 0)
        {
            number--;
            result = (char)('A' + number % 26) + result;
            number /= 26;
        }
        return result;
    }

    private static void AddText(ZipArchive archive, string name, string content)
    {
        var entry = archive.CreateEntry(name, CompressionLevel.Optimal);
        using var writer = new StreamWriter(entry.Open(), new UTF8Encoding(false));
        writer.Write(content);
    }
}
