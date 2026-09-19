// SPDX-License-Identifier: GPL-3.0-or-later
using System.Text.Json;
using Phyphox.Core;

namespace Phyphox.Storage;
public sealed record ReplayHeader
{
    public int Schema { get; init; } = 2;
    public string Kind { get; init; } = "phyphox-input-cycle-journal";
    public required string SourceXml { get; init; }
    public bool CompleteReplay { get; init; }
    public string EngineVersion { get; init; } = ReplayEngine.EngineVersion;
}

public sealed record ReplayCommand(string Command, string? Buffer = null, double[]? Values = null, string[]? Groups = null);
public sealed record ReplayEvent
{
    public required long Sequence { get; init; }
    public required string Kind { get; init; }
    public Dictionary<string, double[]>? Buffers { get; init; }
    public string[] ReplaceBuffers { get; init; } = [];
    public ReplayCommand? Command { get; init; }
    public long? Cycle { get; init; }
    public double? ExperimentTime { get; init; }
    public double? LinearTime { get; init; }
    public double? Offset1970 { get; init; }
    public double? LinearOffset1970 { get; init; }
    public Dictionary<string, string>? SourceMetadata { get; init; }
    public Dictionary<string, double>? Info { get; init; }
}

public sealed record ReplayJournal(ReplayHeader Header, IReadOnlyList<ReplayEvent> Events);
public sealed record ReplayResult(ExperimentRuntime Runtime, long EventsProcessed, long CyclesExecuted, string Source = "recorded-input-recomputation", bool HardwareOutputEnabled = false);
public static class ReplayEngine
{
    public const string EngineVersion = "phyphox-windows-core-v1";
    public static ReplayJournal ReadJsonLines(TextReader reader)
    {
        string first = reader.ReadLine() ?? throw new FormatException("Empty replay journal.");
        var header = JsonSerializer.Deserialize<ReplayHeader>(first, StorageJson.Options) ?? throw new FormatException("Missing replay header.");
        ValidateHeader(header);
        var events = new List<ReplayEvent>();
        string? line;
        while ((line = reader.ReadLine()) != null)
        {
            if (string.IsNullOrWhiteSpace(line))
                continue;
            if (events.Count >= 1_000_000)
                throw new InvalidOperationException("Replay journal exceeds event budget.");
            events.Add(JsonSerializer.Deserialize<ReplayEvent>(line, StorageJson.Options) ?? throw new FormatException("Invalid replay event."));
        }

        return new(header, events);
    }

    public static async Task WriteJsonLinesAsync(TextWriter writer, ReplayJournal journal, CancellationToken cancellationToken = default)
    {
        ValidateHeader(journal.Header);
        await writer.WriteLineAsync(JsonSerializer.Serialize(journal.Header, StorageJson.Options).AsMemory(), cancellationToken);
        foreach (var e in journal.Events)
            await writer.WriteLineAsync(JsonSerializer.Serialize(e, StorageJson.Options).AsMemory(), cancellationToken);
    }

    public static ReplayResult Replay(ReplayJournal journal, CancellationToken cancellationToken = default)
    {
        ValidateHeader(journal.Header);
        if (journal.Events.Count == 0 || journal.Events[^1].Kind != "end")
            throw new FormatException("Incomplete replay: final end marker is missing.");
        var info = new RecordedInfo();
        var runtime = new ExperimentRuntime(ExperimentParser.Parse(journal.Header.SourceXml), info);
        long expected = 1, cycles = 0;
        foreach (var e in journal.Events)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (e.Sequence != expected++)
                throw new FormatException("Replay event sequence has a gap, duplicate, or reordering.");
            if (e.Info != null)
                info.Values = e.Info;
            switch (e.Kind)
            {
                case "input":
                    if (e.Buffers == null)
                        throw new FormatException("Input event requires exact submitted buffers.");
                    foreach (var pair in e.Buffers)
                    {
                        if (pair.Value == null || !runtime.Buffers.ContainsKey(pair.Key))
                            throw new FormatException("Input event references an invalid buffer.");
                        runtime.ReceiveSamples(pair.Key, pair.Value, !e.ReplaceBuffers.Contains(pair.Key));
                    }

                    break;
                case "command":
                    var c = e.Command ?? throw new FormatException("Command event is missing its command.");
                    switch (c.Command)
                    {
                        case "start":
                            runtime.Start();
                            break;
                        case "pause":
                            runtime.Pause();
                            break;
                        case "stop":
                            runtime.Stop();
                            break;
                        case "clear":
                            if (c.Groups is { Length: > 0 })
                                runtime.ClearGroups(c.Groups);
                            else
                                runtime.Clear();
                            break;
                        case "set":
                            if (c.Buffer == null || c.Values == null || !runtime.Buffers.ContainsKey(c.Buffer))
                                throw new FormatException("Invalid set command.");
                            runtime.SetBuffer(c.Buffer, c.Values);
                            break;
                        case "append":
                            if (c.Buffer == null || c.Values == null || !runtime.Buffers.ContainsKey(c.Buffer))
                                throw new FormatException("Invalid append command.");
                            runtime.SetBuffer(c.Buffer, c.Values, true);
                            break;
                        default:
                            throw new NotSupportedException($"Replay command cannot execute: {c.Command}. Device/output operations are deliberately unavailable.");
                    }

                    break;
                case "end":
                    if (e.Sequence != journal.Events.Count)
                        throw new FormatException("Replay end marker must be last.");
                    break;
                case "cycle":
                    if (e.Cycle == null || e.ExperimentTime == null || e.LinearTime == null || e.Offset1970 == null || e.LinearOffset1970 == null)
                        throw new FormatException("Cycle event requires cycle index and all four time values.");
                    if (!runtime.RunCycleAt(e.Cycle.Value, e.ExperimentTime.Value, e.LinearTime.Value, e.Offset1970.Value, e.LinearOffset1970.Value))
                        throw new FormatException("Recorded analysis execution conflicts with requireFill state.");
                    cycles++;
                    break;
                default:
                    throw new NotSupportedException($"Unknown replay event: {e.Kind}");
            }
        }

        return new(runtime, journal.Events.Count, cycles);
    }

    static void ValidateHeader(ReplayHeader header)
    {
        if (header.Schema != 2 || header.Kind != "phyphox-input-cycle-journal" || !header.CompleteReplay || header.EngineVersion != EngineVersion || string.IsNullOrWhiteSpace(header.SourceXml))
            throw new NotSupportedException("Journal is incomplete or uses an unsupported schema/engine. Version 1 command logs cannot reconstruct missing samples or scheduling.");
    }

    sealed class RecordedInfo : IExperimentInfoProvider
    {
        public Dictionary<string, double> Values { get; set; } = [];

        public bool TryGetValue(string key, out double value) => Values.TryGetValue(key, out value);
    }
}
