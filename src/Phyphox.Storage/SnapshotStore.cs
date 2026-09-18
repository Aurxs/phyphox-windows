// SPDX-License-Identifier: GPL-3.0-or-later
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using Phyphox.Core;

namespace Phyphox.Storage;
public sealed record SessionSnapshot
{
    public int Schema { get; init; } = 1;
    public required string SourceXml { get; init; }
    public required Dictionary<string, double[]> Buffers { get; init; }
    public double ExperimentTime { get; init; }
    public required TimeMapping[] TimeMappings { get; init; }
    public DateTimeOffset SavedAt { get; init; } = DateTimeOffset.UtcNow;
    public string Kind { get; init; } = "paused-data-snapshot";

    public static SessionSnapshot Capture(ExperimentRuntime runtime)
    {
        // The host holds its session lock. Seal only the copied timeline at the
        // captured instant; acquisition in the original runtime continues.
        var savedAt = DateTimeOffset.UtcNow;
        var experimentTime = runtime.ExperimentTime;
        var mappings = runtime.TimeMappings.ToList();
        if (mappings.LastOrDefault()?.Event == "START")
            mappings.Add(new("PAUSE", experimentTime, savedAt));
        return new()
        {
            SourceXml = runtime.Definition.SourceXml,
            Buffers = runtime.Buffers.ToDictionary(p => p.Key, p => p.Value.Values),
            ExperimentTime = experimentTime,
            TimeMappings = mappings.ToArray(),
            SavedAt = savedAt
        };
    }

    public ExperimentRuntime Restore(IExperimentInfoProvider? info = null)
    {
        Validate();
        var definition = ExperimentParser.Parse(SourceXml);
        foreach (var c in definition.Containers)
            if (!Buffers.TryGetValue(c.Name, out var data) || c.Size > 0 && data.Length > c.Size)
                throw new FormatException($"Snapshot buffer size does not match {c.Name}.");
        var runtime = new ExperimentRuntime(definition, info);
        var mappings = TimeMappings.ToList();
        // Older schema-1 captures can have an open final interval, but unlike
        // bare .phyphox events they explicitly store the capture's elapsed time.
        if (mappings.LastOrDefault()?.Event == "START")
            mappings.Add(new("PAUSE", ExperimentTime, SavedAt));
        runtime.RestoreDataSnapshot(Buffers, ExperimentTime, mappings);
        return runtime;
    }

    internal void Validate()
    {
        if (Schema != 1 || Kind != "paused-data-snapshot" || string.IsNullOrWhiteSpace(SourceXml) || Buffers == null || TimeMappings == null || Buffers.Any(p => p.Value == null) || !double.IsFinite(ExperimentTime) || ExperimentTime < 0)
            throw new FormatException("Invalid or unsupported snapshot.");
        if (TimeMappings.Any(t => t.Event is not ("START" or "PAUSE") || !double.IsFinite(t.ExperimentTime) || t.ExperimentTime < 0))
            throw new FormatException("Invalid snapshot time mapping.");
    }
}

public static class StorageJson
{
    public static JsonSerializerOptions Options { get; } = new(JsonSerializerDefaults.Web)
    {
        NumberHandling = JsonNumberHandling.AllowNamedFloatingPointLiterals
    };
}

public sealed class SnapshotStore
{
    readonly string directory;
    public SnapshotStore(string directory)
    {
        this.directory = Path.GetFullPath(directory);
    }

    string FilePath(string id)
    {
        if (!Regex.IsMatch(id, @"\A[A-Za-z0-9_-]{1,80}\z"))
            throw new ArgumentException("Snapshot id must be a simple identifier.");
        return Path.Combine(directory, id + ".snapshot.json");
    }

    public async Task SaveAtomicAsync(string id, SessionSnapshot snapshot, CancellationToken cancellationToken = default)
    {
        snapshot.Validate();
        string target = FilePath(id);
        Directory.CreateDirectory(directory);
        string temporary = target + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            await using (var stream = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None, 65536, FileOptions.Asynchronous | FileOptions.WriteThrough))
            {
                await JsonSerializer.SerializeAsync(stream, snapshot, StorageJson.Options, cancellationToken);
                await stream.FlushAsync(cancellationToken);
                stream.Flush(true);
            }

            cancellationToken.ThrowIfCancellationRequested();
            File.Move(temporary, target, true);
        }
        finally
        {
            if (File.Exists(temporary))
                File.Delete(temporary);
        }
    }

    public async Task<SessionSnapshot> LoadAsync(string id, CancellationToken cancellationToken = default)
    {
        string path = FilePath(id);
        await using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 65536, FileOptions.Asynchronous);
        if (stream.Length > 256_000_000)
            throw new InvalidOperationException("Snapshot exceeds load memory budget.");
        var snapshot = await JsonSerializer.DeserializeAsync<SessionSnapshot>(stream, StorageJson.Options, cancellationToken) ?? throw new FormatException("Empty snapshot.");
        snapshot.Validate();
        return snapshot;
    }
}
