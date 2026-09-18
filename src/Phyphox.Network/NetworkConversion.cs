// SPDX-License-Identifier: GPL-3.0-only
using System.Globalization;
using System.Text.Json;
using System.Text.RegularExpressions;
namespace Phyphox.Network;
public static class NetworkConversion
{
    public static double[] Read(string body, string conversion, string id, JsonDocument? prepared = null)
    {
        if (conversion == "none") return [];
        if (conversion == "csv")
        {
            var index = int.TryParse(id, out var parsed) ? parsed : -1; var result = new List<double>();
            foreach (var line in Regex.Split(body, "\\r?\\n"))
            {
                var columns = line.Split([',', ';']);
                foreach (var text in index < 0 ? columns : index < columns.Length ? [columns[index]] : Array.Empty<string>()) result.Add(double.TryParse(text.Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out var v) ? v : double.NaN);
            }
            return result.ToArray();
        }
        if (conversion != "json") throw new NotSupportedException($"Unsupported conversion: {conversion}");
        var owned = prepared is null ? JsonDocument.Parse(body) : null;
        try
        {
            var current = (prepared ?? owned!).RootElement;
            foreach (var part in id.Split('.'))
            {
                if (current.ValueKind != JsonValueKind.Object) throw new InvalidDataException($"JSON path {id} traverses a non-object.");
                if (!current.TryGetProperty(part, out current)) return [];
            }
            if (current.ValueKind == JsonValueKind.Number) return [current.GetDouble()];
            if (current.ValueKind != JsonValueKind.Array) return [];
            var result = new List<double>();
            foreach (var item in current.EnumerateArray())
            {
                if (item.ValueKind == JsonValueKind.Number) result.Add(item.GetDouble());
                else if (item.ValueKind == JsonValueKind.String && double.TryParse(item.GetString(), NumberStyles.Float, CultureInfo.InvariantCulture, out var v)) result.Add(v);
                else break; // Android JSONArray.getDouble aborts this payload at the first non-numeric item.
            }
            return result.ToArray();
        }
        finally { owned?.Dispose(); }
    }
}
