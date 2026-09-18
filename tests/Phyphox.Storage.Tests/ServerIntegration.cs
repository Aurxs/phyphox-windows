using System.Text;
using System.Text.Json;
using Phyphox.Server;
using Phyphox.Storage;

internal static class ServerIntegration
{
    public static async Task Run()
    {
        string root = Path.Combine(Path.GetTempPath(), "phyphox-session-test-" + Guid.NewGuid().ToString("N"));
        var assets = Path.Combine(root, "assets");
        Directory.CreateDirectory(Path.Combine(assets, "samples"));
        const string source = """
<phyphox version="1.20"><title>Recorder integration</title><category>Tests</category><data-containers><container>signal</container><container size="0">out</container></data-containers><views><view label="Data"><edit label="Input" default="3"><output>signal</output></edit><value label="Output"><input>out</input></value></view></views><analysis><multiply><input keep="true">signal</input><input type="value">2</input><output append="true">out</output></multiply></analysis></phyphox>
""";
        await File.WriteAllTextAsync(Path.Combine(assets, "samples", "test.phyphox"), source);
        var library = new LibraryService(assets, Path.Combine(root, "data"));
        var devices = new DeviceManager();
        var session = new SessionService(library, Path.Combine(root, "data"), devices);
        await session.StartAsync(CancellationToken.None);
        try
        {
            session.Load(library.List().Single().Id);
            session.Command(new("set", "signal", [4]));
            session.Command(new("stop"));
            var listing = JsonSerializer.SerializeToElement(session.ListRecordings());
            var id = listing.GetProperty("items").EnumerateArray().Single(i => i.GetProperty("complete").GetBoolean()).GetProperty("id").GetString()!;
            var replay = JsonSerializer.SerializeToElement(session.ReplayRecording(id, CancellationToken.None));
            if (!replay.GetProperty("buffers").GetProperty("out").EnumerateArray().Select(v => v.GetDouble()).SequenceEqual([6, 8]))
                throw new Exception("Server journal omitted defaults/pre-run or command analysis.");
            await session.ImportDataAsync(Encoding.UTF8.GetBytes("value\n7\n"), "data.csv", new(new() { Columns = [new(0, "signal")] }, Analyze: true), CancellationToken.None);
            var saved = JsonSerializer.SerializeToElement(await session.SaveSnapshotAsync("saved", CancellationToken.None));
            var restored = JsonSerializer.SerializeToElement(await session.RestoreSnapshotAsync(saved.GetProperty("id").GetString()!, CancellationToken.None));
            if (restored.GetProperty("status").GetString() != "paused" || restored.GetProperty("dataOrigin").GetString() != "restored-data-snapshot" || restored.GetProperty("buffers").GetProperty("out").EnumerateArray().Last().GetDouble() != 14)
                throw new Exception("Server import/snapshot restore mismatch.");
            Console.WriteLine("Server storage integration passed: initialization journal, replay without hardware, CSV import, paused snapshot restore.");
        }
        finally
        {
            await session.StopAsync(CancellationToken.None);
            session.Dispose();
            Directory.Delete(root, true);
        }
    }
}
