using Phyphox.Core;
using System.Text.RegularExpressions;

internal static class CorpusAudit
{
    public static int Run(string root)
    {
        int loaded = 0, rejected = 0, mismatches = 0, newer = 0;
        var expected = new Dictionary<string, bool>();
        string? key = null;
        foreach (var line in File.ReadLines(Path.Combine(root, "invalid", "expected.yml")))
        {
            if (line.Length > 0 && !char.IsWhiteSpace(line[0]) && line.Contains(".phyphox:"))
                key = line[..line.IndexOf(':')];
            if (key != null && Regex.IsMatch(line, @"^\s+parser:"))
                expected[key] = line.Contains("accepts");
        }

        foreach (var dir in new[]
        {
            "valid",
            "generated",
            "invalid"
        }

        )
            foreach (var file in Directory.GetFiles(Path.Combine(root, dir), "*.phyphox", SearchOption.AllDirectories).Order())
            {
                bool shouldLoad = dir != "invalid" || expected.GetValueOrDefault(Path.GetFileName(file));
                try
                {
                    ExperimentParser.Parse(File.ReadAllText(file));
                    loaded++;
                    if (!shouldLoad)
                    {
                        mismatches++;
                        Console.WriteLine("UNEXPECTED ACCEPT " + Path.GetRelativePath(root, file));
                    }
                }
                catch (Exception e)
                {
                    if (e.Message.Contains("maximum 1.20"))
                    {
                        newer++;
                        continue;
                    }

                    rejected++;
                    if (shouldLoad)
                    {
                        mismatches++;
                        Console.WriteLine("UNEXPECTED REJECT " + Path.GetRelativePath(root, file) + ": " + e.Message);
                    }
                }
            }

        Console.WriteLine($"Corpus parse audit (not execution/hardware): loaded={loaded}, rejected={rejected}, newer-format-skipped={newer}, expectation-mismatches={mismatches}");
        return mismatches == 0 ? 0 : 1;
    }
}
