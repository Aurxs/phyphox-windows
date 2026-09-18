// SPDX-License-Identifier: GPL-3.0-or-later
// Rules and slot tables derived from the official phyphox-docs spec at 3dcfdae.
using System.Globalization;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Xml.Linq;

namespace Phyphox.Core;
internal static class FormatValidation
{
    static readonly JsonElement[] Schemas = Load();
    static JsonElement[] Load()
    {
        using var stream = typeof(FormatValidation).Assembly.GetManifestResourceStream("Phyphox.Core.FormatSchema.json")!;
        using var doc = JsonDocument.Parse(stream);
        return doc.RootElement.EnumerateArray().Select(e => e.Clone()).ToArray();
    }

    static string Str(JsonElement e, string key, string fallback = "") => e.TryGetProperty(key, out var p) && p.ValueKind == JsonValueKind.String ? p.GetString()! : fallback;
    static bool Bool(JsonElement e, string key) => e.TryGetProperty(key, out var p) && p.ValueKind == JsonValueKind.True;
    static JsonElement[] Array(JsonElement e, string key) => e.TryGetProperty(key, out var p) && p.ValueKind == JsonValueKind.Array ? p.EnumerateArray().ToArray() : [];
    static bool ParentMatches(JsonElement s, string parent) => s.TryGetProperty("parent", out var p) ? p.ValueKind == JsonValueKind.String ? p.GetString() == parent : p.ValueKind == JsonValueKind.Array && p.EnumerateArray().Any(v => v.GetString() == parent) : parent == "";
    static bool Match(JsonElement s, XElement e)
    {
        if (Str(s, "name") != e.Name.LocalName)
            return false;
        string parent = e.Parent?.Name.LocalName ?? "";
        if (e.Parent?.Parent?.Name.LocalName == "analysis" && (e.Name.LocalName is "input" or "output"))
            parent = "@analysis-module";
        if (!ParentMatches(s, parent))
            return false;
        var block = Str(s, "block");
        if (parent == "bluetooth")
            return e.Ancestors().Any(a => a.Parent?.Name.LocalName == "phyphox" && a.Name.LocalName == block);
        return true;
    }

    public static double Number(string value)
    {
        if (!Regex.IsMatch(value, @"\A(?:[+-]?(?:(?:[0-9]+(?:\.[0-9]*)?|\.[0-9]+)(?:[eE][+-]?[0-9]+)?|Infinity)|NaN)\z", RegexOptions.IgnoreCase))
            throw new FormatException($"Invalid numeric value: {value}");
        return XmlUtil.Number(value);
    }

    public static void Validate(XElement root, string locale = "zh-CN")
    {
        foreach (var node in root.DescendantsAndSelf().Where(e => e.Name.Namespace == root.Name.Namespace).ToArray())
            node.Name = root.Name.Namespace + node.Name.LocalName.ToLowerInvariant();
        foreach (var foreign in root.Descendants().Where(e => e.Name.Namespace != root.Name.Namespace && e.Parent?.Name.Namespace == root.Name.Namespace).ToArray())
            foreign.Remove();
        if (root.Attribute("version") == null)
            throw new FormatException("Missing version.");
        foreach (var e in root.DescendantsAndSelf())
        {
            var schema = Schemas.FirstOrDefault(s => Match(s, e));
            if (schema.ValueKind == JsonValueKind.Undefined)
                continue;
            foreach (var a in Array(schema, "attributes"))
            {
                string name = Str(a, "name");
                if (name == "mapColor[N]")
                {
                    foreach (var color in e.Attributes().Where(v => Regex.IsMatch(v.Name.LocalName, @"^mapColor[0-9]+$")))
                        Color(color.Value);
                    continue;
                }

                var attr = e.Attribute(name);
                if (attr == null)
                {
                    if (Bool(a, "required"))
                        throw new FormatException($"Missing {name} on {e.Name.LocalName}.");
                    continue;
                }

                string value = attr.Value;
                switch (Str(a, "type"))
                {
                    case "boolean":
                        if (!value.Equals("true", StringComparison.OrdinalIgnoreCase) && !value.Equals("false", StringComparison.OrdinalIgnoreCase))
                            throw new FormatException($"Invalid boolean {name}: {value}");
                        break;
                    case "integer":
                        if (!Regex.IsMatch(value, @"\A[+-]?[0-9]+\z") || !int.TryParse(value, NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out _))
                            throw new FormatException($"Invalid integer {name}: {value}");
                        break;
                    case "float":
                        Number(value);
                        break;
                    case "float-list":
                        if (value.Length > 0)
                            foreach (var v in value.Split(','))
                                Number(v.Trim());
                        break;
                    case "enum":
                        var values = Array(a, "values");
                        if (values.Length > 0)
                        {
                            var canonical = values.Select(v => v.ValueKind == JsonValueKind.String ? v.GetString()! : v.ToString()).FirstOrDefault(v => v.Equals(value, StringComparison.OrdinalIgnoreCase));
                            if (canonical == null)
                                throw new FormatException($"Invalid {name} on {e.Name.LocalName}: {value}");
                            attr.Value = canonical;
                        }

                        break;
                    case "color":
                        Color(value);
                        break;
                }
            }

            var children = Array(schema, "children").Select(v => v.GetString()).ToHashSet();
            if (children.Count > 0)
                foreach (var child in e.Elements())
                    if (!children.Contains(child.Name.LocalName))
                        throw new FormatException($"Unknown element {child.Name.LocalName} inside {e.Name.LocalName}.");
            if (e.Parent?.Name.LocalName == "analysis")
            {
                Slots(e, schema, "input");
                Slots(e, schema, "output");
            }

            if (schema.TryGetProperty("outputs", out var outputs) && outputs.ValueKind == JsonValueKind.Object)
            {
                string attribute = Str(outputs, "attribute", "component");
                var components = Array(outputs, "components").Select(c => Str(c, "name")).ToArray();
                foreach (var o in e.Children("output"))
                {
                    var a = o.Attribute(attribute);
                    if (a == null)
                    {
                        if (Bool(outputs, "required_component"))
                            throw new FormatException($"Missing output {attribute} on {e.Name.LocalName}.");
                        continue;
                    }

                    var canonical = components.FirstOrDefault(c => c.Equals(a.Value, StringComparison.OrdinalIgnoreCase));
                    if (canonical == null && components.Length > 0)
                        throw new FormatException($"Unknown output {attribute}: {a.Value}");
                    if (canonical != null)
                        a.Value = canonical;
                }
            }

            if (e.Name.LocalName == "gausssmooth" && e.Attribute("sigma") != null && !(Number(e.Attr("sigma")) > 0))
                throw new FormatException("Gaussian sigma must be positive.");
            if (e.Name.LocalName == "audio" && e.Parent?.Name.LocalName == "input" && e.Attribute("rate") != null && Number(e.Attr("rate")) < 0)
                throw new FormatException("Audio rate cannot be negative.");
            if (e.Name.LocalName == "graph")
                Graph(e);
            if (e.Name.LocalName == "color")
                Color(e.Value.Trim());
        }

        foreach (var key in new[]
        {
            "title",
            "category"
        }

        )
            if (new ExperimentLocalization(root, locale).Text(key).Length == 0 && root.Children(key).LastOrDefault() == null)
                throw new FormatException($"Missing root or selected translation {key}.");
        foreach (var owner in root.Children("translations").SelectMany(t => t.Children("translation")).Prepend(root))
        {
            var links = owner.Children("link").ToArray();
            var labels = links.Select(l => l.Attr("label")).ToArray();
            if (labels.Any(string.IsNullOrWhiteSpace) || labels.Distinct().Count() != labels.Length)
                throw new FormatException("Link labels must be present and unique.");
            foreach (var link in links)
            {
                if (owner == root)
                {
                    if (string.IsNullOrWhiteSpace(link.Value) || link.Attribute("translation") != null)
                        throw new FormatException("Invalid root link.");
                }
                else if (!root.Children("link").Any(l => l.Attr("label") == link.Attr("label")) && string.IsNullOrWhiteSpace(link.Value))
                    throw new FormatException("Unmatched translated link requires URL.");
            }
        }
    }

    static void Slots(XElement module, JsonElement schema, string kind)
    {
        var slots = Array(schema, kind + "s");
        var counts = new int[slots.Length];
        var assigned = new List<(XElement element, int slot)>();
        foreach (var tag in module.Children(kind))
        {
            string alias = tag.Attr("as");
            int index = -1;
            if (alias.Length > 0)
                index = System.Array.FindIndex(slots, s => Str(s, "name").Equals(alias, StringComparison.OrdinalIgnoreCase));
            else
                for (int i = 0; i < slots.Length; i++)
                {
                    var slot = slots[i];
                    bool unlimited = Str(slot, "max") == "unlimited";
                    int max = slot.TryGetProperty("max", out var p) && p.ValueKind == JsonValueKind.Number ? p.GetInt32() : 0;
                    if (!Bool(slot, "as_required") && (unlimited || counts[i] < max))
                    {
                        index = i;
                        break;
                    }
                }

            if (index < 0)
                throw new FormatException($"Unknown or excess {kind} slot '{alias}' on {module.Name.LocalName}.");
            counts[index]++;
            var rule = slots[index];
            if (alias.Length > 0)
                tag.SetAttributeValue("as", Str(rule, "name"));
            if (rule.TryGetProperty("max", out var maximum) && maximum.ValueKind == JsonValueKind.Number && counts[index] > maximum.GetInt32())
                throw new FormatException($"Duplicate slot {Str(rule, "name")}.");
            if (kind == "input")
            {
                var type = tag.Attr("type", "buffer").ToLowerInvariant();
                if (tag.Attribute("type") != null)
                    tag.SetAttributeValue("type", type);
                if (type == "value" && !Bool(rule, "allows_value") || type == "empty" && !Bool(rule, "allows_empty"))
                    throw new FormatException($"Input type {type} not allowed for {module.Name.LocalName}/{Str(rule, "name")}.");
                if (type == "value")
                    Number(tag.Value.Trim());
            }

            assigned.Add((tag, index));
        }

        for (int i = 0; i < slots.Length; i++)
            if (slots[i].TryGetProperty("min", out var min) && counts[i] < min.GetInt32())
                throw new FormatException($"Missing required {kind} slot {Str(slots[i], "name")} on {module.Name.LocalName}.");
        // Only simple slot groups are reordered; repeating rangefilter groups preserve source grouping.
        if (kind == "input" && module.Name.LocalName != "rangefilter")
        {
            foreach (var item in assigned)
                item.element.Remove();
            module.AddFirst(assigned.OrderBy(p => p.slot).Select(p => p.element));
        }
    }

    static void Graph(XElement graph)
    {
        var inputs = graph.Children("input").ToArray();
        int xs = inputs.Count(e => e.Attr("axis", "y") == "x"), ys = inputs.Count(e => e.Attr("axis", "y") == "y");
        if (xs == ys)
            return;
        bool pending = false;
        foreach (var e in inputs)
        {
            var axis = e.Attr("axis", "y");
            if (axis == "x")
            {
                if (pending)
                    throw new FormatException("Graph x input shadowed before use.");
                pending = true;
            }
            else if (axis == "y")
                pending = false;
        }

        if (pending)
            throw new FormatException("Graph has unused trailing x input.");
    }

    static void Color(string value)
    {
        var names = new[]
        {
            "orange",
            "red",
            "magenta",
            "blue",
            "green",
            "yellow",
            "white",
            "weakorange",
            "weakred",
            "weakmagenta",
            "weakblue",
            "weakgreen",
            "weakyellow",
            "weakwhite"
        };
        if (!names.Contains(value.ToLowerInvariant()) && !Regex.IsMatch(value, @"\A#?[0-9a-fA-F]{6}\z"))
            throw new FormatException($"Invalid color: {value}");
    }
}
