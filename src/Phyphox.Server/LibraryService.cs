// SPDX-License-Identifier: GPL-3.0-only
using System.IO.Compression;
using Phyphox.Core;
using System.Security.Cryptography;
using System.Xml;
using System.Xml.Linq;

namespace Phyphox.Server;

public record LibraryItem(string Id, string Title, string Category, string Description,
    string Source, string FormatVersion, string[] Requirements, bool IsLink, string? LoadError);
public record LibraryEntry(LibraryItem Item, string Path, string ResourceRoot);

public sealed class LibraryService
{
    public const long MaximumImportBytes = 32 * 1024 * 1024;
    private readonly string assets;
    private readonly string custom;
    private readonly object gate = new();
    private Dictionary<string, LibraryEntry> entries = [];
    public LibraryService(string assets, string dataRoot)
    {
        this.assets = assets;
        custom = Path.Combine(dataRoot, "experiments");
        Directory.CreateDirectory(custom);
        Refresh();
    }
    public LibraryItem[] List() { lock (gate) return entries.Values.Select(e => e.Item).OrderBy(e => e.Category).ThenBy(e => e.Title).ToArray(); }
    public LibraryEntry Get(string id) { lock (gate) return entries.TryGetValue(id, out var entry) ? entry : throw new KeyNotFoundException("找不到实验。"); }
    public void Refresh()
    {
        var next = new Dictionary<string, LibraryEntry>();
        foreach (var (folder, source) in new[] { (Path.Combine(assets, "experiments"), "official"), (Path.Combine(assets, "samples"), "sample"), (custom, "user") })
        {
            if (!Directory.Exists(folder)) continue;
            foreach (var path in Directory.EnumerateFiles(folder, "*.phyphox", SearchOption.AllDirectories))
            {
                var relative = Path.GetRelativePath(folder, path);
                if (relative.Split(Path.DirectorySeparatorChar).Any(p => p.StartsWith(".import-", StringComparison.Ordinal))) continue;
                var id = source + "-" + Convert.ToHexString(SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(relative))).ToLowerInvariant()[..20];
                try
                {
                    using var stream = File.OpenRead(path);
                    var xml = ReadXml(stream);
                    var root = xml.Root ?? throw new InvalidDataException("XML 缺少根元素。");
                    var localization = new ExperimentLocalization(root, "zh-CN");
                    string Text(string name) => localization.Text(name);
                    var requirements = root.Elements().Where(e => Name(e, "input") || Name(e, "output") || Name(e, "network"))
                        .SelectMany(e => e.Elements().Select(child => e.Name.LocalName.ToLowerInvariant() + ":" + child.Name.LocalName.ToLowerInvariant())).Distinct().ToArray();
                    var title = Text("state-title");
                    if (string.IsNullOrWhiteSpace(title)) title = Text("title");
                    if (string.IsNullOrWhiteSpace(title)) title = Path.GetFileNameWithoutExtension(path);
                    var item = new LibraryItem(id, title, Text("category"), Text("description"), source,
                        (string?)root.Attribute("version") ?? "1.0", requirements,
                        string.Equals((string?)root.Attribute("isLink"), "true", StringComparison.OrdinalIgnoreCase), null);
                    next[id] = new(item, path, Path.GetDirectoryName(path)!);
                }
                catch (Exception ex) when (ex is XmlException or IOException or InvalidDataException)
                {
                    next[id] = new(new(id, Path.GetFileName(path), "无法读取", "", source, "", [], false, ex.Message), path, Path.GetDirectoryName(path)!);
                }
            }
        }
        lock (gate) entries = next;
    }
    public async Task<LibraryItem[]> ImportAsync(Stream input, string fileName, CancellationToken ct)
    {
        var staging = Path.Combine(custom, ".import-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(staging);
        try
        {
            using var bytes = new MemoryStream();
            var buffer = new byte[64 * 1024];
            int count;
            while ((count = await input.ReadAsync(buffer, ct)) > 0)
            {
                if (bytes.Length + count > MaximumImportBytes) throw new InvalidDataException("导入文件超过 32 MiB 限制。");
                await bytes.WriteAsync(buffer.AsMemory(0, count), ct);
            }
            bytes.Position = 0;
            if (Path.GetExtension(fileName).Equals(".zip", StringComparison.OrdinalIgnoreCase))
            {
                using var archive = new ZipArchive(bytes, ZipArchiveMode.Read, true);
                if (archive.Entries.Count > 256) throw new InvalidDataException("ZIP 文件条目过多。");
                long size = 0;
                foreach (var entry in archive.Entries)
                {
                    if (entry.FullName.Contains('\\') || entry.FullName.Contains(':') || entry.FullName.StartsWith('/') || entry.FullName.Split('/').Any(p => p is ".." or "."))
                        throw new InvalidDataException("ZIP 包含不安全路径。");
                    if (((entry.ExternalAttributes >> 16) & 0xF000) == 0xA000) throw new InvalidDataException("ZIP 不允许符号链接。");
                    size += entry.Length;
                    if (size > MaximumImportBytes) throw new InvalidDataException("ZIP 展开后超过 32 MiB 限制。");
                    if (entry.FullName.EndsWith('/')) continue;
                    var destination = Path.GetFullPath(Path.Combine(staging, entry.FullName));
                    if (!destination.StartsWith(staging + Path.DirectorySeparatorChar, StringComparison.Ordinal)) throw new InvalidDataException("ZIP 路径越界。");
                    Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
                    await using var output = new FileStream(destination, FileMode.CreateNew);
                    await using var source = entry.Open();
                    await source.CopyToAsync(output, ct);
                }
            }
            else
            {
                if (!Path.GetExtension(fileName).Equals(".phyphox", StringComparison.OrdinalIgnoreCase)) throw new InvalidDataException("只接受 .phyphox 或实验 ZIP。");
                await File.WriteAllBytesAsync(Path.Combine(staging, "experiment.phyphox"), bytes.ToArray(), ct);
            }
            var experiments = Directory.GetFiles(staging, "*.phyphox", SearchOption.AllDirectories);
            if (experiments.Length == 0) throw new InvalidDataException("文件中没有 .phyphox 实验。");
            foreach (var experiment in experiments)
            {
                using var stream = File.OpenRead(experiment);
                var xml = ReadXml(stream);
                if (xml.Root is null || !Name(xml.Root, "phyphox")) throw new InvalidDataException("文件不是 phyphox 实验。");
            }
            var folder = Path.Combine(custom, Guid.NewGuid().ToString("N"));
            Directory.Move(staging, folder);
            Refresh();
            lock (gate) return entries.Values.Where(e => e.Path.StartsWith(folder + Path.DirectorySeparatorChar, StringComparison.Ordinal)).Select(e => e.Item).ToArray();
        }
        finally { if (Directory.Exists(staging)) Directory.Delete(staging, true); }
    }
    public void Delete(string id)
    {
        var entry = Get(id);
        if (entry.Item.Source != "user") throw new InvalidOperationException("内置实验不能删除。");
        File.Delete(entry.Path);
        Refresh();
    }
    public string? Resource(string id, string name)
    {
        if (string.IsNullOrWhiteSpace(name) || Path.IsPathRooted(name) || name.Contains('\\') || name.Contains(':') || name.Split('/').Any(p => p is ".." or ".")) return null;
        var entry = Get(id);
        using var input = File.OpenRead(entry.Path);
        var doc = ReadXml(input);
        // Only resources explicitly declared in the loaded experiment may be served.
        if (!doc.Descendants().Attributes().Any(a => a.Name.LocalName is "src" && a.Value == name)) return null;
        var candidates = new[] { Path.Combine(entry.ResourceRoot, name), Path.Combine(entry.ResourceRoot, "res", name), Path.Combine(assets, "experiments", "res", name) };
        return candidates.FirstOrDefault(File.Exists);
    }
    public string? CertificateResource(string id, string name)
    {
        if (string.IsNullOrWhiteSpace(name) || name.Contains('/') || name.Contains('\\') || name.Contains(':') || name is "." or "..") return null;
        var entry = Get(id);
        using var input = File.OpenRead(entry.Path);
        var document = ReadXml(input);
        if (!document.Descendants().Where(e => Name(e, "connection")).Attributes().Any(a => a.Name.LocalName.Equals("certificate", StringComparison.OrdinalIgnoreCase) && a.Value == name)) return null;
        return new[] { Path.Combine(entry.ResourceRoot, "res", name), Path.Combine(entry.ResourceRoot, name), Path.Combine(assets, "experiments", "res", name) }.FirstOrDefault(File.Exists);
    }
    internal static XDocument ReadXml(Stream stream) => XDocument.Load(XmlReader.Create(stream, new XmlReaderSettings
    {
        DtdProcessing = DtdProcessing.Prohibit, XmlResolver = null, MaxCharactersInDocument = MaximumImportBytes,
        IgnoreComments = true
    }), LoadOptions.SetLineInfo);
    internal static bool Name(XElement e, string name) => (e.Name.NamespaceName.Length == 0 || e.Name.NamespaceName is "http://phyphox.org/xml" or "https://phyphox.org/xml") && e.Name.LocalName.Equals(name, StringComparison.OrdinalIgnoreCase);
}
