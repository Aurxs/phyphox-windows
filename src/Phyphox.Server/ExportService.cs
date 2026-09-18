// SPDX-License-Identifier: GPL-3.0-only
using System.Globalization;
using System.IO.Compression;
using System.Text;
using System.Xml;
using System.Xml.Linq;
using Phyphox.Core;

namespace Phyphox.Server;
public sealed record ExportFile(byte[] Bytes, string ContentType, string FileName);
public static class ExportService
{
    public static ExportFile Create(ExportSnapshot snapshot, string format)
    {
        var root = ExperimentParser.Parse(snapshot.SourceXml).Root;
        var sets = root.Child("export")?.Children("set").Select(set => new ExportSet(set.Attr("name", "Data"),
            set.Children("data").Select(e => (e.Attr("name", e.Value.Trim()), e.Value.Trim())).ToArray())).ToArray() ?? [];
        if (sets.Length == 0) sets = [new("Data", snapshot.Buffers.Keys.Select(k => (k, k)).ToArray())];
        var stem = string.Concat(snapshot.Title.Where(c => !Path.GetInvalidFileNameChars().Contains(c) && !"<>:\"/\\|?*".Contains(c)));
        if (string.IsNullOrWhiteSpace(stem)) stem = "experiment";
        stem = stem[..Math.Min(80, stem.Length)] + "-" + DateTime.UtcNow.ToString("yyyyMMdd-HHmmss");
        return format.ToLowerInvariant() switch
        {
            "state" => new(State(snapshot), "application/xml", stem + ".phyphox"),
            "csv" => Csv(snapshot, sets, stem),
            "xlsx" => new(Xlsx(snapshot, sets), "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet", stem + ".xlsx"),
            _ => throw new InvalidOperationException("未知导出格式。")
        };
    }
    static byte[] State(ExportSnapshot snapshot)
    {
        var doc = new XDocument(new XElement(ExperimentParser.Parse(snapshot.SourceXml).Root));
        var root = doc.Root!; XNamespace ns = root.Name.Namespace;
        root.Elements().Where(e => e.Name.LocalName is "state-title" or "color" or "events").Remove();
        root.Add(new XElement(ns + "state-title", snapshot.Title + " — saved"));
        root.Add(new XElement(ns + "color", "blue"));
        var events = new XElement(ns + "events");
        foreach (var mapping in snapshot.TimeMappings) events.Add(new XElement(ns + mapping.Event.ToLowerInvariant(),
            new XAttribute("experimentTime", Number(mapping.ExperimentTime)), new XAttribute("systemTime", mapping.SystemTime.ToUnixTimeMilliseconds())));
        root.Add(events);
        foreach (var container in root.Child("data-containers")?.Children("container") ?? [])
            if (snapshot.Buffers.TryGetValue(container.Value.Trim(), out var values)) container.SetAttributeValue("init", string.Join(',', values.Select(Number)));
        return Encoding.UTF8.GetBytes(doc.ToString());
    }
    static ExportFile Csv(ExportSnapshot snapshot, ExportSet[] sets, string stem)
    {
        using var bytes = new MemoryStream();
        using (var zip = new ZipArchive(bytes, ZipArchiveMode.Create, true))
        {
            for (int i = 0; i < sets.Length; i++) WriteEntry(zip, $"{i + 1:D2}-{SafeName(sets[i].Name)}.csv", CsvSet(snapshot, sets[i]));
            WriteEntry(zip, "meta/time.csv", "event,experiment time,system time\n" + string.Join('\n', snapshot.TimeMappings.Select(t => $"{t.Event},{Number(t.ExperimentTime)},{Number(t.SystemTime.ToUnixTimeMilliseconds() / 1000d)}")));
            WriteEntry(zip, "meta/session.csv", "property,value\nplatform,Windows port\nsnapshot revision," + snapshot.Revision + "\nhardware verification,not performed\n");
        }
        return new(bytes.ToArray(), "application/zip", stem + ".zip");
    }
    static string CsvSet(ExportSnapshot snapshot, ExportSet set)
    {
        var columns = set.Columns.Select(c => snapshot.Buffers.GetValueOrDefault(c.Buffer, [])).ToArray();
        var sb = new StringBuilder(string.Join(',', set.Columns.Select(c => Quote(c.Name))) + "\r\n");
        int length = columns.Select(c => c.Length).DefaultIfEmpty(0).Max();
        for (int row = 0; row < length; row++) sb.AppendLine(string.Join(',', columns.Select(c => row < c.Length ? Number(c[row]) : "NaN")));
        return sb.ToString();
    }
    static string Quote(string value)
    {
        if (value.Length > 0 && "=+-@\t\r".Contains(value[0])) value = "'" + value;
        return "\"" + value.Replace("\"", "\"\"") + "\"";
    }
    internal static string Number(double value) => double.IsNaN(value) ? "NaN" : double.IsPositiveInfinity(value) ? "Infinity" : double.IsNegativeInfinity(value) ? "-Infinity" : value.ToString("R", CultureInfo.InvariantCulture);
    static string SafeName(string s) => string.Concat(s.Select(c => char.IsLetterOrDigit(c) || c is '-' or '_' or ' ' ? c : '_'))[..Math.Min(s.Length, 80)];
    static void WriteEntry(ZipArchive zip, string path, string text)
    {
        using var writer = new StreamWriter(zip.CreateEntry(path).Open(), new UTF8Encoding(false)); writer.Write(text);
    }
    static byte[] Xlsx(ExportSnapshot snapshot, ExportSet[] sets)
    {
        XNamespace x = "http://schemas.openxmlformats.org/spreadsheetml/2006/main";
        XNamespace relationships = "http://schemas.openxmlformats.org/officeDocument/2006/relationships";
        XNamespace package = "http://schemas.openxmlformats.org/package/2006/relationships";
        XNamespace types = "http://schemas.openxmlformats.org/package/2006/content-types";
        using var bytes = new MemoryStream();
        using (var zip = new ZipArchive(bytes, ZipArchiveMode.Create, true))
        {
            var sheets = new XElement(x + "sheets");
            var links = new XElement(package + "Relationships");
            var content = new XElement(types + "Types",
                new XElement(types + "Default", new XAttribute("Extension", "rels"), new XAttribute("ContentType", "application/vnd.openxmlformats-package.relationships+xml")),
                new XElement(types + "Default", new XAttribute("Extension", "xml"), new XAttribute("ContentType", "application/xml")),
                new XElement(types + "Override", new XAttribute("PartName", "/xl/workbook.xml"), new XAttribute("ContentType", "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet.main+xml")));
            var all = sets.Append(new ExportSet("Metadata Time", [("experiment time", "$time"), ("system time (Unix s)", "$utc")])).ToArray();
            var data = new Dictionary<string, double[]>(snapshot.Buffers) { ["$time"] = snapshot.TimeMappings.Select(t => t.ExperimentTime).ToArray(), ["$utc"] = snapshot.TimeMappings.Select(t => t.SystemTime.ToUnixTimeMilliseconds() / 1000d).ToArray() };
            int index = 0;
            foreach (var set in all)
            {
                var columns = set.Columns.Select(c => data.GetValueOrDefault(c.Buffer, [])).ToArray();
                var rows = columns.Select(c => c.Length).DefaultIfEmpty(0).Max();
                if (set.Columns.Length > 16384) throw new InvalidOperationException("导出列数超过 XLSX 限制。");
                int parts = Math.Max(1, (rows + 1_048_574) / 1_048_575);
                for (int part = 0; part < parts; part++)
                {
                    index++; var rid = "rId" + index;
                    var label = index + " " + SafeName(set.Name) + (parts > 1 ? " " + (part + 1) : "");
                    sheets.Add(new XElement(x + "sheet", new XAttribute("name", label[..Math.Min(31, label.Length)]), new XAttribute("sheetId", index), new XAttribute(relationships + "id", rid)));
                    links.Add(new XElement(package + "Relationship", new XAttribute("Id", rid), new XAttribute("Type", relationships.NamespaceName + "/worksheet"), new XAttribute("Target", $"worksheets/sheet{index}.xml")));
                    content.Add(new XElement(types + "Override", new XAttribute("PartName", $"/xl/worksheets/sheet{index}.xml"), new XAttribute("ContentType", "application/vnd.openxmlformats-officedocument.spreadsheetml.worksheet+xml")));
                    using var stream = zip.CreateEntry($"xl/worksheets/sheet{index}.xml").Open();
                    using var writer = XmlWriter.Create(stream, new XmlWriterSettings { Encoding = new UTF8Encoding(false), CloseOutput = false });
                    writer.WriteStartElement("worksheet", x.NamespaceName); writer.WriteStartElement("sheetData");
                    writer.WriteStartElement("row");
                    foreach (var column in set.Columns) StringCell(writer, column.Name);
                    writer.WriteEndElement();
                    for (int row = part * 1_048_575; row < Math.Min(rows, (part + 1) * 1_048_575); row++)
                    {
                        writer.WriteStartElement("row");
                        foreach (var column in columns)
                        {
                            var value = row < column.Length ? column[row] : double.NaN;
                            if (double.IsFinite(value)) { writer.WriteStartElement("c"); writer.WriteElementString("v", Number(value)); writer.WriteEndElement(); }
                            else StringCell(writer, Number(value));
                        }
                        writer.WriteEndElement();
                    }
                    writer.WriteEndElement(); writer.WriteEndElement();
                }
            }
            WriteEntry(zip, "xl/workbook.xml", new XElement(x + "workbook", new XAttribute(XNamespace.Xmlns + "r", relationships), sheets).ToString());
            WriteEntry(zip, "xl/_rels/workbook.xml.rels", links.ToString());
            WriteEntry(zip, "[Content_Types].xml", content.ToString());
            WriteEntry(zip, "_rels/.rels", new XElement(package + "Relationships", new XElement(package + "Relationship", new XAttribute("Id", "rId1"), new XAttribute("Type", relationships.NamespaceName + "/officeDocument"), new XAttribute("Target", "xl/workbook.xml"))).ToString());
        }
        return bytes.ToArray();
    }
    static void StringCell(XmlWriter writer, string text)
    {
        writer.WriteStartElement("c"); writer.WriteAttributeString("t", "inlineStr"); writer.WriteStartElement("is"); writer.WriteElementString("t", text); writer.WriteEndElement(); writer.WriteEndElement();
    }
    sealed record ExportSet(string Name, (string Name, string Buffer)[] Columns);
}
