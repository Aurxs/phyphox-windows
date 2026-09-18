// SPDX-License-Identifier: GPL-3.0-or-later
// Semantics derived from phyphox Android (RWTH Aachen University), commit 45fa55a.
using System.Globalization;
using System.Xml;
using System.Xml.Linq;

namespace Phyphox.Core;
public sealed record ContainerDefinition(string Name, int Size, bool Static, double[] Initial, string? ClearGroup);
public sealed class ExperimentDefinition
{
    public required string SourceXml { get; init; }
    public required string Title { get; init; }
    public string Category { get; init; } = "";
    public string Description { get; init; } = "";
    public required Version Version { get; init; }
    public required IReadOnlyList<ContainerDefinition> Containers { get; init; }
    public required IReadOnlyList<XElement> Views { get; init; }
    public required IReadOnlyList<XElement> Analysis { get; init; }
    public required IReadOnlyList<XElement> Inputs { get; init; }
    public required IReadOnlyList<XElement> Outputs { get; init; }
    public IReadOnlyList<TimeMapping> RestoredTimeMappings { get; init; } = [];
    public required XElement Root { get; init; }
    public IReadOnlyList<string> UnsupportedModules => Analysis.Where(m => !AnalysisKernel.Supported.Contains(m.Name.LocalName)).Select(m => m.Name.LocalName).Distinct().ToArray();
    public required IReadOnlyList<string> CapabilityProblems { get; init; }
}

public static class XmlUtil
{
    public static string Attr(this XElement e, string name, string fallback = "") => e.Attribute(name)?.Value ?? fallback;
    public static bool Flag(this XElement e, string name, bool fallback = false) => bool.TryParse(e.Attr(name), out var v) ? v : fallback;
    public static double Number(string s) => s.Trim().ToLowerInvariant() switch
    {
        "nan" => double.NaN,
        "inf" or "infinity" or "+infinity" => double.PositiveInfinity,
        "-inf" or "-infinity" => double.NegativeInfinity,
        _ => double.Parse(s, CultureInfo.InvariantCulture)};
    public static IEnumerable<XElement> Children(this XElement e, string name) => e.Elements().Where(x => x.Name.LocalName == name && x.Name.Namespace == e.Name.Namespace);
    public static XElement? Child(this XElement e, string name) => e.Children(name).FirstOrDefault();
}

public static class ExperimentParser
{
    public static ExperimentDefinition Parse(string xml, string locale = "zh-CN")
    {
        using var sr = new StringReader(xml);
        using var reader = XmlReader.Create(sr, new XmlReaderSettings { DtdProcessing = DtdProcessing.Prohibit, XmlResolver = null, MaxCharactersInDocument = 32_000_000 });
        var root = XDocument.Load(reader).Root ?? throw new FormatException("Missing experiment root.");
        if (!root.Name.LocalName.Equals("phyphox", StringComparison.OrdinalIgnoreCase) || (root.Name.NamespaceName != "" && root.Name.NamespaceName != "http://phyphox.org/xml" && root.Name.NamespaceName != "https://phyphox.org/xml"))
            throw new FormatException("Expected phyphox root.");
        if (!System.Version.TryParse(root.Attr("version", "1.0"), out var version) || version > new Version(1, 20))
            throw new FormatException("Unsupported experiment version; maximum 1.20.");
        FormatValidation.Validate(root, locale);
        var containers = (root.Child("data-containers")?.Children("container") ?? []).Select(e => new ContainerDefinition(e.Value.Trim(), int.Parse(e.Attr("size", "1"), CultureInfo.InvariantCulture), e.Flag("static"), e.Attr("init").Length == 0 ? Array.Empty<double>() : e.Attr("init").Split(',').Select(v => FormatValidation.Number(v.Trim())).ToArray(), e.Attribute("clearGroup")?.Value)).ToArray();
        if (containers.Any(c => c.Size < 0 || c.Name.Length == 0) || containers.Select(c => c.Name).Distinct().Count() != containers.Length)
            throw new FormatException("Invalid or duplicate container.");
        var names = containers.Select(c => c.Name).ToHashSet();
        var analysis = root.Child("analysis")?.Elements().Where(e => e.Name.Namespace == root.Name.Namespace).ToArray() ?? [];
        foreach (var m in analysis)
            foreach (var p in m.Elements().Where(e => e.Name.LocalName is "input" or "output"))
                if (p.Attr("type", "buffer") == "buffer" && !names.Contains(p.Value.Trim()))
                    throw new FormatException($"Unknown container '{p.Value}'.");
        foreach (var key in new[]
        {
            "requireFill",
            "requireFillDynamic",
            "dynamicSleep"
        }

        )
        {
            var target = root.Child("analysis")?.Attr(key);
            if (!string.IsNullOrEmpty(target) && !names.Contains(target))
                throw new FormatException($"Unknown {key} buffer: {target}");
        }

        foreach (var m in analysis.Where(m => m.Name.LocalName == "formula"))
            _ = new Formula(m.Attr("formula"));
        var restoredMappings = new List<TimeMapping>();
        foreach (var e in root.Children("events").SelectMany(block => block.Elements()))
        {
            var rawExperiment = e.Attribute("experimentTime")?.Value ?? throw new FormatException("Saved event requires experimentTime.");
            var experimentTime = FormatValidation.Number(rawExperiment);
            var rawSystem = e.Attribute("systemTime")?.Value ?? throw new FormatException("Saved event requires systemTime.");
            if (!double.IsFinite(experimentTime) || experimentTime < 0 || !System.Text.RegularExpressions.Regex.IsMatch(rawSystem, @"\A[+-]?[0-9]+\z") || !long.TryParse(rawSystem, NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out var systemTime) || systemTime < 0)
                throw new FormatException("Saved event timestamps must be finite nonnegative experiment seconds and integer Unix milliseconds.");
            DateTimeOffset system;
            try
            {
                system = DateTimeOffset.FromUnixTimeMilliseconds(systemTime);
            }
            catch (ArgumentOutOfRangeException)
            {
                throw new FormatException("Saved event timestamp is outside the supported calendar range.");
            }

            restoredMappings.Add(new(e.Name.LocalName.ToUpperInvariant(), experimentTime, system));
        }

        var problems = analysis.Where(m => !AnalysisKernel.Supported.Contains(m.Name.LocalName)).Select(m => $"Unsupported analysis module: {m.Name.LocalName}").Distinct().ToList();
        var localization = new ExperimentLocalization(root, locale);
        string Text(string key) => localization.Text(key);
        return new()
        {
            SourceXml = xml,
            Root = root,
            Title = Text("title"),
            Category = Text("category"),
            Description = Text("description"),
            Version = version,
            RestoredTimeMappings = restoredMappings,
            Containers = containers,
            Views = root.Child("views")?.Children("view").Select(localization.TranslateView).ToArray() ?? [],
            Analysis = analysis,
            Inputs = root.Child("input")?.Elements().ToArray() ?? [],
            Outputs = root.Child("output")?.Elements().ToArray() ?? [],
            CapabilityProblems = problems
        };
    }
}

public sealed class DataBuffer
{
    readonly List<double> values = [];
    public ContainerDefinition Definition { get; }
    public double[] Values => values.ToArray();

    public double[] Tail(int maxCount)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(maxCount);
        int count = Math.Min(maxCount, values.Count);
        if (count == 0)
            return[];
        var tail = new double[count];
        values.CopyTo(values.Count - count, tail, 0, count);
        return tail;
    }

    public int Count => values.Count;
    public double Last => values.Count == 0 ? double.NaN : values[^1];
    public bool Written { get; internal set; }

    public DataBuffer(ContainerDefinition definition)
    {
        Definition = definition;
        Reset();
        Written = definition.Static && definition.Initial.Length > 0;
    }

    public void Append(IEnumerable<double> data)
    {
        if (Definition.Static && Written)
            return;
        values.AddRange(data);
        if (Definition.Size > 0 && values.Count > Definition.Size)
            values.RemoveRange(0, values.Count - Definition.Size);
    }

    public void Clear()
    {
        if (!Definition.Static)
            values.Clear();
    }

    public void Restore(IEnumerable<double> data)
    {
        values.Clear();
        Written = false;
        Append(data);
        Written = Definition.Static;
    }

    public void Reset()
    {
        values.Clear();
        Written = false;
        Append(Definition.Initial);
    }
}
