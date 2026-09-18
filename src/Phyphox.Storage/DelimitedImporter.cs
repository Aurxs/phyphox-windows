// SPDX-License-Identifier: GPL-3.0-or-later
using System.Globalization;
using System.Text;

namespace Phyphox.Storage;
public sealed record ColumnMapping(int Index, string Buffer, string? Unit = null, double Scale = 1, double Offset = 0);
public sealed record DelimitedImportOptions
{
    public char Delimiter { get; init; } = ',';
    public bool HasHeader { get; init; } = true;
    public required IReadOnlyList<ColumnMapping> Columns { get; init; }
    public int? TimeColumn { get; init; }
    public double? SampleRate { get; init; }
    public string TimeBuffer { get; init; } = "time";
}

public sealed record ImportedData(IReadOnlyDictionary<string, double[]> Buffers, int SampleCount, string TimeSource, IReadOnlyDictionary<string, string?> Units);
public static class DelimitedImporter
{
    public static ImportedData Import(string text, DelimitedImportOptions options)
    {
        if (options.Delimiter is not (',' or ';' or '\t'))
            throw new ArgumentException("Delimiter must be comma, semicolon, or tab.");
        if (options.Columns.Count == 0 || options.Columns.Any(c => c.Index < 0 || string.IsNullOrWhiteSpace(c.Buffer) || !double.IsFinite(c.Scale) || !double.IsFinite(c.Offset)) || options.Columns.Select(c => c.Buffer).Distinct().Count() != options.Columns.Count)
            throw new ArgumentException("Explicit, distinct valid column mappings are required.");
        if (options.TimeColumn < 0 || options.TimeColumn.HasValue && options.SampleRate.HasValue)
            throw new ArgumentException("Choose a time column or supplied sample rate, not both.");
        if (options.SampleRate.HasValue && (!double.IsFinite(options.SampleRate.Value) || options.SampleRate.Value <= 0))
            throw new ArgumentException("Sample rate must be positive and finite.");
        bool timed = options.TimeColumn.HasValue || options.SampleRate.HasValue;
        if (timed && (string.IsNullOrWhiteSpace(options.TimeBuffer) || options.Columns.Any(c => c.Buffer == options.TimeBuffer)))
            throw new ArgumentException("Time buffer must be distinct from mapped data buffers.");
        var rows = ReadRows(text.TrimStart('\uFEFF'), options.Delimiter);
        var values = options.Columns.ToDictionary(c => c.Buffer, _ => new List<double>());
        var times = new List<double>();
        int rowIndex = 0, count = 0, columns = -1;
        foreach (var row in rows)
        {
            rowIndex++;
            if (columns < 0)
                columns = row.Length;
            if (row.Length != columns)
                throw new FormatException($"Row {rowIndex} has {row.Length} columns; expected {columns}.");
            if (rowIndex == 1 && options.HasHeader)
                continue;
            foreach (var mapping in options.Columns)
            {
                if (mapping.Index >= row.Length)
                    throw new FormatException($"Column {mapping.Index} is absent at row {rowIndex}.");
                values[mapping.Buffer].Add(Parse(row[mapping.Index], rowIndex, mapping.Index) * mapping.Scale + mapping.Offset);
            }

            if (options.TimeColumn is int timeColumn)
            {
                if (timeColumn >= row.Length)
                    throw new FormatException($"Time column missing at row {rowIndex}.");
                double time = Parse(row[timeColumn], rowIndex, timeColumn);
                if (!double.IsFinite(time) || times.Count > 0 && time < times[^1])
                    throw new FormatException($"Time must be finite and nondecreasing at row {rowIndex}.");
                times.Add(time);
            }
            else if (options.SampleRate is double rate)
                times.Add(count / rate);
            count++;
        }

        if (count == 0)
            throw new FormatException("No data rows were supplied.");
        var output = values.ToDictionary(k => k.Key, k => k.Value.ToArray());
        var units = options.Columns.ToDictionary(c => c.Buffer, c => c.Unit);
        if (timed)
        {
            output.Add(options.TimeBuffer, times.ToArray());
            units.Add(options.TimeBuffer, "s");
        }

        return new(output, count, options.TimeColumn.HasValue ? "explicit-column" : options.SampleRate.HasValue ? "user-supplied-sample-rate" : "none", units);
    }

    static double Parse(string value, int row, int column)
    {
        if (!double.TryParse(value.Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out var number))
            throw new FormatException($"Invalid number at row {row}, column {column}: '{value}'.");
        return number;
    }

    static IEnumerable<string[]> ReadRows(string text, char delimiter)
    {
        var row = new List<string>();
        var field = new StringBuilder();
        bool quoted = false, closed = false, started = false;
        for (int i = 0; i < text.Length; i++)
        {
            char c = text[i];
            started = true;
            if (quoted)
            {
                if (c == '"')
                {
                    if (i + 1 < text.Length && text[i + 1] == '"')
                    {
                        field.Append('"');
                        i++;
                    }
                    else
                    {
                        quoted = false;
                        closed = true;
                    }
                }
                else
                    field.Append(c);
                continue;
            }

            if (c == '"')
            {
                if (field.Length > 0 || closed)
                    throw new FormatException("Quote in unquoted field.");
                quoted = true;
                continue;
            }

            if (c == delimiter)
            {
                row.Add(field.ToString());
                field.Clear();
                closed = false;
                continue;
            }

            if (c is '\r' or '\n')
            {
                if (c == '\r' && i + 1 < text.Length && text[i + 1] == '\n')
                    i++;
                row.Add(field.ToString());
                yield return row.ToArray();
                row.Clear();
                field.Clear();
                closed = false;
                started = false;
                continue;
            }

            if (closed)
                throw new FormatException("Unexpected characters after closing quote.");
            field.Append(c);
        }

        if (quoted)
            throw new FormatException("Unterminated quoted field.");
        if (started || row.Count > 0 || field.Length > 0)
        {
            row.Add(field.ToString());
            yield return row.ToArray();
        }
    }
}
