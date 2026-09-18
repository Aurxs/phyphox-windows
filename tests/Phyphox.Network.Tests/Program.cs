// SPDX-License-Identifier: GPL-3.0-only
using Phyphox.Network;
using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Xml.Linq;

var root = args.Length > 0 ? Path.GetFullPath(args[0]) : Path.GetFullPath("../official-reference/phyphox-docs");
if (args.Contains("--mqtt-only")) { await MqttTests.Run(root); return; }
var socket = new TcpListener(IPAddress.Loopback, 0); socket.Start(); var port = ((IPEndPoint)socket.LocalEndpoint).Port; socket.Stop();
var process = new Process { StartInfo = new("python3") { RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false } };
process.StartInfo.ArgumentList.Add("-u"); process.StartInfo.ArgumentList.Add("-c");
process.StartInfo.ArgumentList.Add("import importlib.util,sys; s=importlib.util.spec_from_file_location('fixture',sys.argv[1]); m=importlib.util.module_from_spec(s); s.loader.exec_module(m); server=m.Server(('127.0.0.1',int(sys.argv[2])),m.Handler); print('ready',flush=True); server.serve_forever()");
process.StartInfo.ArgumentList.Add(Path.Combine(root, "tools/network_fixture.py")); process.StartInfo.ArgumentList.Add(port.ToString());
process.Start();
try
{
    if (await process.StandardOutput.ReadLineAsync().WaitAsync(TimeSpan.FromSeconds(5)) != "ready") throw new Exception("Official HTTP fixture failed to start.");
    var address = $"http://127.0.0.1:{port}";
    XElement Fixture(string name) => XDocument.Parse(File.ReadAllText(Path.Combine(root, "fixtures/network", name + ".phyphox")).Replace("FIXTURE-HOST", "127.0.0.1").Replace("FIXTURE-PORT", port.ToString())).Root!.Elements().Single(e => e.Name.LocalName == "network");
    double[] Read(string name) => name == "out" ? [42.5] : [];
    void Check(bool condition, string message) { if (!condition) throw new Exception(message); Console.WriteLine("PASS " + message); }
    using (var receive = new NetworkCoordinator(Fixture("http-get-receive"), Read))
    {
        await receive.TickAsync(); Check(receive.Pending.IsEmpty, "construction and pre-start ticks perform no request");
        receive.Start(); await receive.TickAsync();
        Check(receive.Pending.TryDequeue(out var batch) && batch.Writes.Single(w => w.Buffer == "seq").Values.SequenceEqual([1d]) && batch.Writes.Single(w => w.Buffer == "value").Values.SequenceEqual([.5]), "official HTTP GET receive fixture");
        receive.Stop(); await receive.TickAsync(); Check(receive.Pending.IsEmpty, "stop prevents new responses");
    }
    using (var get = new NetworkCoordinator(Fixture("http-get-send-roundtrip"), Read))
    {
        get.Start(); await get.TickAsync(); Check(get.Pending.TryDequeue(out var batch) && batch.Writes.Single(w => w.Buffer == "back").Values.SequenceEqual([42.5]) && batch.Writes.All(w => w.Append), "official GET last-value roundtrip and default append");
    }
    using (var post = new NetworkCoordinator(Fixture("http-post-roundtrip"), _ => [1, 2.5, 3]))
    {
        post.Start(); await post.TickAsync(); Check(post.Pending.TryDequeue(out var batch) && batch.Writes.Single(w => w.Buffer == "back").Values.SequenceEqual([1d,2.5,3]), "official POST array roundtrip");
    }
    var errors = new List<string>();
    foreach (var endpoint in new[] { "/malformed", "/empty", "/http500", "/timeout" })
    {
        var network = XElement.Parse($"<network><connection address='{address}{endpoint}' service='http/get' conversion='json' interval='0.1'><receive id='v'>never</receive></connection></network>");
        using var bad = new NetworkCoordinator(network, Read, errors.Add) { RequestTimeout = TimeSpan.FromMilliseconds(150) };
        bad.Start(); await bad.TickAsync(); Check(bad.Pending.IsEmpty && bad.Status[0].Error is not null, $"HTTP error remains contained: {endpoint}");
    }
    using (var down = new NetworkCoordinator(XElement.Parse("<network><connection service='http/get' address='http://127.0.0.1:1' interval='1'/></network>"), Read, errors.Add) { RequestTimeout = TimeSpan.FromMilliseconds(150) })
    { down.Start(); await down.TickAsync(); Check(down.Status[0].Error is not null, "unreachable endpoint remains contained"); }
    using (var limited = new NetworkCoordinator(XElement.Parse($"<network><connection service='http/get' address='{address}/data' interval='1'/></network>"), Read, errors.Add) { MaximumResponseBytes = 2 })
    { limited.Start(); await limited.TickAsync(); Check(limited.Pending.IsEmpty && limited.Status[0].Error is not null, "response size bounded"); }
    var triggeredXml = XElement.Parse($"<network><connection id='manual' address='{address}/collect' service='http/post' conversion='json'><send id='arr' keep='false'>out</send><receive id='arr' append='false'>back</receive></connection></network>");
    using (var missing = new NetworkCoordinator(triggeredXml, Read)) Check(missing.Issues.Any(i => i.Contains("atomic")), "keep=false never silently loses concurrent samples");
    var consumed = false;
    using (var trigger = new NetworkCoordinator(triggeredXml, Read, capture: requests => { consumed = requests.Single().Keep == false; return new Dictionary<string,double[]> { ["out"] = [8,9] }; }))
    {
        trigger.Start(); await trigger.TickAsync(); Check(trigger.Pending.IsEmpty, "zero interval requires explicit trigger");
        trigger.RequestTrigger("manual"); await trigger.TickAsync(); Check(consumed && trigger.Pending.TryDequeue(out var batch) && !batch.Writes.Single().Append && batch.Writes.Single().Values.SequenceEqual([8d,9]), "atomic capture and explicit trigger overwrite semantics");
    }
    using (var meta = new NetworkCoordinator(XElement.Parse($"<network><connection service='http/get' address='{address}/collect'><send id='device' type='meta'>deviceModel</send></connection></network>"), Read)) Check(meta.Issues.Any(i=>i.Contains("metadata")), "missing real metadata provider explicitly blocked");
    Check(NetworkConversion.Read("{\"a\":{\"v\":[1,\"2.5\",3]}}", "json", "a.v").SequenceEqual([1d,2.5,3]), "JSON nested path and numeric array conversion");
    var csv = NetworkConversion.Read("1,2\n3;bad\n", "csv", "1"); Check(csv.Length == 2 && csv[0] == 2 && double.IsNaN(csv[1]), "CSV column and invalid numeric semantics");
    await MqttTests.Run(root);
    Console.WriteLine("Network tests passed. No BLE/USB/Windows/external broker verification claimed.");
}
finally
{
    if (!process.HasExited) process.Kill(true);
    await process.WaitForExitAsync(); process.Dispose();
}
