using System.Text;
using Phyphox.Core;
using Phyphox.Storage;

if (args.FirstOrDefault() == "--input-names")
{
    foreach (var (file, expected) in new[] { ("accelerometer", "加速度传感器（含重力 g）"), ("linear_accelerometer", "线性加速度传感器（不含重力 g）"), ("magnetometer", "磁场传感器（磁力计）") })
    {
        var inputXml = System.Xml.Linq.XElement.Load($"../official-reference/phyphox-android/app/src/main/assets/experiments/{file}.phyphox");
        var input = inputXml.Element("input")!.Elements().Single();
        if (Phyphox.Server.SessionService.GetInputDisplayName(input) != expected) throw new Exception($"Incorrect sensor display name: {file}");
    }
    Console.WriteLine("PASS official input names: acceleration with g / linear acceleration without g / magnetic field");
    return;
}

if (args.FirstOrDefault() == "--browser-media") { await BrowserMediaIntegration.Run(); return; }

void Check(bool condition, string label)
{
    if (!condition)
        throw new Exception(label);
}

void Reject(Action action, string label)
{
    try
    {
        action();
    }
    catch (Exception e)when (e is FormatException or NotSupportedException or ArgumentException or System.Text.Json.JsonException)
    {
        return;
    }

    throw new Exception(label);
}

var csv = DelimitedImporter.Import("\uFEFF\"time\",\"sensor, value\"\r\n\"0\",\"1.5\"\r\n\"0.1\",\"2.5\"\r\n", new() { Columns = [new(1, "value", "V")], TimeColumn = 0 });
Check(csv.SampleCount == 2 && csv.Buffers["value"].SequenceEqual([1.5, 2.5]) && csv.Buffers["time"][1] == 0.1, "CSV quoted import");
var tsv = DelimitedImporter.Import("1\t2\n3\t4", new() { Delimiter = '\t', HasHeader = false, Columns = [new(1, "signal")], SampleRate = 20 });
Check(tsv.Buffers["time"][1] == 0.05, "Explicit rate time");
var untimed = DelimitedImporter.Import("x\n1", new() { Columns = [new(0, "signal")] });
Check(untimed.TimeSource == "none" && !untimed.Buffers.ContainsKey("time"), "No invented timestamp");
Reject(() => DelimitedImporter.Import("x,y\n1", new() { Columns = [new(0, "signal")] }), "Ragged CSV accepted");
Reject(() => DelimitedImporter.Import("x\n\"1", new() { Columns = [new(0, "signal")] }), "Unclosed quote accepted");
Reject(() => DelimitedImporter.Import("x\nbad", new() { Columns = [new(0, "signal")] }), "Malformed number accepted");
using var wave = new MemoryStream();
using (var writer = new BinaryWriter(wave, Encoding.ASCII, true))
{
    writer.Write("RIFF"u8);
    writer.Write(40);
    writer.Write("WAVEfmt "u8);
    writer.Write(16);
    writer.Write((short)1);
    writer.Write((short)1);
    writer.Write(8000);
    writer.Write(16000);
    writer.Write((short)2);
    writer.Write((short)16);
    writer.Write("data"u8);
    writer.Write(4);
    writer.Write(short.MinValue);
    writer.Write(short.MaxValue);
}

wave.Position = 0;
var wav = WaveImporter.Import(wave);
Check(wav.SampleRate == 8000 && wav.Channels == 1 && wav.BitsPerSample == 16 && wav.Data.Buffers["audio1"][0] == -1 && wav.Data.Buffers["time"][1] == 1 / 8000.0, "WAV sample/metadata mismatch");
const string xml = """
<phyphox version="1.20"><title>Replay test</title><category>Tests</category><data-containers><container size="0">in</container><container size="0">out</container><container size="1">t</container></data-containers><analysis><multiply><input>in</input><input type="value">2</input><output append="true">out</output></multiply><timer><output>t</output></timer></analysis></phyphox>
""";
var journal = new ReplayJournal(new() { SourceXml = xml, CompleteReplay = true }, [new() { Sequence = 1, Kind = "command", Command = new("start") }, new() { Sequence = 2, Kind = "input", Buffers = new() { ["in"] = [1, 2] } }, new() { Sequence = 3, Kind = "cycle", Cycle = 0, ExperimentTime = 0.25, LinearTime = 0.25, Offset1970 = 1000, LinearOffset1970 = 1000 }, new() { Sequence = 4, Kind = "input", Buffers = new() { ["in"] = [3] } }, new() { Sequence = 5, Kind = "cycle", Cycle = 1, ExperimentTime = 0.5, LinearTime = 0.5, Offset1970 = 1000, LinearOffset1970 = 1000 }, new() { Sequence = 6, Kind = "command", Command = new("pause") }, new() { Sequence = 7, Kind = "end" }]);
var replay = ReplayEngine.Replay(journal);
Check(replay.Runtime.Buffers["out"].Values.SequenceEqual([2, 4, 6]) && replay.Runtime.Buffers["t"].Last == 0.5 && !replay.HardwareOutputEnabled, "Deterministic replay mismatch");
using var text = new StringWriter();
await ReplayEngine.WriteJsonLinesAsync(text, journal);
var reread = ReplayEngine.ReadJsonLines(new StringReader(text.ToString()));
Check(ReplayEngine.Replay(reread).Runtime.Buffers["out"].Values.SequenceEqual([2, 4, 6]), "Replay JSONL roundtrip");
Reject(() => ReplayEngine.ReadJsonLines(new StringReader("{\"schema\":1,\"sourceXml\":\"x\",\"completeReplay\":false}")), "Incomplete old journal accepted");
Reject(() => ReplayEngine.Replay(journal with { Events = [new() { Sequence = 2, Kind = "input", Buffers = new() { ["in"] = [1] } }, new() { Sequence = 3, Kind = "end" }] }), "Sequence gap accepted");
Reject(() => ReplayEngine.Replay(journal with { Events = [new() { Sequence = 1, Kind = "command", Command = new("device-write") }, new() { Sequence = 2, Kind = "end" }] }), "Hardware command accepted");
string dir = Path.Combine(Path.GetTempPath(), "phyphox-storage-test-" + Guid.NewGuid().ToString("N"));
try
{
    var store = new SnapshotStore(dir);
    var snapshot = SessionSnapshot.Capture(replay.Runtime);
    await store.SaveAtomicAsync("test", snapshot);
    var restored = (await store.LoadAsync("test")).Restore();
    Check(restored.State == "paused" && restored.Buffers["out"].Values.SequenceEqual([2, 4, 6]) && restored.ExperimentTime == 0.5, "Snapshot restore mismatch");
    Reject(() => store.LoadAsync("../escape").GetAwaiter().GetResult(), "Snapshot traversal accepted");
}
finally
{
    if (Directory.Exists(dir))
        Directory.Delete(dir, true);
}

var liveSnapshotRuntime = new ExperimentRuntime(ExperimentParser.Parse(xml));
liveSnapshotRuntime.Start();
var liveSnapshot = SessionSnapshot.Capture(liveSnapshotRuntime);
Check(liveSnapshotRuntime.State == "running" && liveSnapshotRuntime.TimeMappings[^1].Event == "START" && liveSnapshot.TimeMappings[^1].Event == "PAUSE" && liveSnapshot.TimeMappings[^1].ExperimentTime == liveSnapshot.ExperimentTime, "Running snapshot mutated live timeline or failed to seal copy");
var liveRestored = liveSnapshot.Restore();
Check(liveRestored.State == "paused" && liveRestored.ExperimentTime == liveSnapshot.ExperimentTime, "Running snapshot failed paused restore");
liveRestored.Start();
Check(liveRestored.TimeMappings[^2].Event == "PAUSE" && liveRestored.TimeMappings[^1].ExperimentTime == liveSnapshot.ExperimentTime, "Resuming restored snapshot omitted pause boundary");
liveRestored.Stop();
liveSnapshotRuntime.Stop();
var oldOpenSnapshot = liveSnapshot with
{
    TimeMappings = liveSnapshot.TimeMappings[..^1]
};
Check(oldOpenSnapshot.Restore().TimeMappings[^1].Event == "PAUSE", "Legacy explicit-time snapshot not normalized");
Console.WriteLine("Storage focused tests passed: CSV/TSV quoting and errors, explicit time, PCM WAV metadata, deterministic replay and refusal of incomplete/device events, atomic snapshot roundtrip and path containment.");
await ServerIntegration.Run();
