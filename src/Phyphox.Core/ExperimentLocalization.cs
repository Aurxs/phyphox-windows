// SPDX-License-Identifier: GPL-3.0-only
// Locale scoring follows phyphox Android Helper.getLanguageRating, commit 45fa55a.
using System.Xml.Linq;
using System.Text.Json;
using System.Text.RegularExpressions;
namespace Phyphox.Core;

public sealed class ExperimentLocalization
{
    static readonly JsonDocument displayResources = JsonDocument.Parse(typeof(ExperimentLocalization).Assembly.GetManifestResourceStream("Phyphox.Core.DisplayResources.json")!);
    readonly string resourceLocale;
    readonly XElement root;
    readonly XElement? selected;
    readonly Dictionary<string, string> strings;
    static IEnumerable<XElement> Children(XElement element, string name) => element.Elements().Where(e => e.Name.Namespace == element.Name.Namespace && e.Name.LocalName.Equals(name, StringComparison.OrdinalIgnoreCase));
    public ExperimentLocalization(XElement root, string locale = "zh-CN")
    {
        this.root = root;
        var language = Parts(locale);
        resourceLocale = language.Language != "zh" ? "en" : language.Script == "hant" || language.Region is "tw" or "hk" or "mo" ? "zh-Hant" : "zh-Hans";
        int best = Score((string?)root.Attribute("locale"), locale);
        foreach (var block in Children(root, "translations").SelectMany(t => Children(t, "translation")))
        {
            int score = Score((string?)block.Attribute("locale"), locale);
            if (score > best) { best = score; selected = block; }
        }
        strings = new(StringComparer.Ordinal);
        if (selected != null)
            foreach (var entry in Children(selected, "string"))
                if (entry.Attribute("original") is { } original) strings[original.Value] = entry.Value;
    }
    public string Text(string name) => Expand((selected == null ? null : Children(selected, name).LastOrDefault()?.Value)
        ?? Children(root, name).LastOrDefault()?.Value ?? "");
    public string Translate(string text) => Expand(strings.GetValueOrDefault(text.Trim(), text));
    string Expand(string text) => Regex.Replace(text, @"\[\[([A-Za-z0-9_]+)\]\]", match =>
    {
        var dictionaries = displayResources.RootElement.GetProperty("resources");
        string key = match.Groups[1].Value;
        if (dictionaries.GetProperty(resourceLocale).TryGetProperty(key, out var localized) || dictionaries.GetProperty("en").TryGetProperty(key, out localized))
            return localized.GetString()!;
        return match.Value;
    });
    public XElement TranslateView(XElement view)
    {
        var copy = new XElement(view);
        // Only presentation attributes: never translate buffer references, formulas, resource paths or IDs.
        string[] displayAttributes = ["label", "unit", "labelX", "labelY", "labelZ", "unitX", "unitY", "unitZ", "positiveUnit", "negativeUnit", "pickLabel", "unitYperX"];
        foreach (var node in copy.DescendantsAndSelf().Where(e => e.Name.Namespace == copy.Name.Namespace))
        {
            foreach (var attribute in node.Attributes().Where(a => displayAttributes.Contains(a.Name.LocalName, StringComparer.OrdinalIgnoreCase)))
                attribute.Value = Translate(attribute.Value);
            if (node.Name.LocalName.Equals("map", StringComparison.OrdinalIgnoreCase) && !node.HasElements)
                node.Value = Translate(node.Value);
        }
        return copy;
    }
    static (string Language, string Region, string Script) Parts(string locale)
    {
        var p = locale.Replace("-r", "-", StringComparison.OrdinalIgnoreCase).ToLowerInvariant().Split(['-', '_']);
        return (p[0], p.Length > 1 ? p[^1] : "", p.Length > 2 ? p[1] : p.Length > 1 ? p[1] : "");
    }
    static int Score(string? candidate, string locale)
    {
        if (string.IsNullOrEmpty(candidate)) return 1;
        var c = Parts(candidate); var target = Parts(locale);
        int score = c.Language == target.Language ? 100 : 0;
        if (c.Region == target.Region) score += 20;
        if (target.Language == "zh")
        {
            if ((target.Region is "hk" or "mo" or "tw" || target.Script == "hant") && c.Script == "hant") score += 10;
            if ((target.Region is "cn" or "mo" or "sg" || target.Script == "hans") && c.Script == "hans") score += 10;
        }
        if (c.Language == "en") score += 2;
        return score;
    }
}
