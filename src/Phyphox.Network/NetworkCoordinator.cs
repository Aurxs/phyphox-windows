// SPDX-License-Identifier: GPL-3.0-only
using System.Collections.Concurrent;
using System.Diagnostics;
using System.Globalization;
using System.Net;
using System.Text;
using System.Text.Json;
using System.Xml.Linq;

namespace Phyphox.Network;

public sealed record NetworkWrite(string Buffer, double[] Values, bool Append);
public sealed record NetworkBatch(string ConnectionId, IReadOnlyList<NetworkWrite> Writes, IReadOnlyList<string> ConsumedBuffers);
public sealed record NetworkBufferRequest(string Buffer, bool Keep);
public sealed record NetworkStatus(string Id, string Service, string Address, string? Error, long Completed, bool? Connected = null);

/// <summary>Explicit-start network adapter. Tick on a worker; drain Pending inside the engine transaction.</summary>
public sealed class NetworkCoordinator : IDisposable
{
    sealed record Send(string Id, string Type, string Source, string DataType, bool Keep);
    sealed record Receive(string Id, string Buffer, bool Append);
    sealed class Connection
    {
        public required string Id, Service, Conversion, Address;
        public required double Interval;
        public required Send[] Sends;
        public required Receive[] Receives;
        public double Due;
        public int Requested;
        public string? Error;
        public long Completed;
        public MqttSettings? MqttSettings;
        public MqttTransport? Mqtt;
        public double ConnectDue;
    }
    readonly List<Connection> connections = [];
    readonly List<string> issues = [];
    readonly Func<string, double[]> readBuffer;
    readonly Func<IReadOnlyList<NetworkBufferRequest>, IReadOnlyDictionary<string, double[]>>? capture;
    readonly Func<string, string?>? metadata;
    readonly Func<object>? timeInfo;
    readonly Action<string>? fault;
    readonly Func<string, byte[]>? resource;
    readonly HttpClient client;
    readonly SemaphoreSlim ticking = new(1, 1);
    readonly object stateGate = new();
    CancellationTokenSource lifetime = new();
    bool running, disposed;
    long generation;
    readonly Stopwatch clock = new();
    public IReadOnlyList<string> Issues => issues;
    public ConcurrentQueue<NetworkBatch> Pending { get; } = new();
    public TimeSpan RequestTimeout { get; init; } = TimeSpan.FromSeconds(5);
    public int MaximumResponseBytes { get; init; } = 8 * 1024 * 1024;
    public long DroppedResponses { get; private set; }
    public IReadOnlyList<NetworkStatus> Status => connections.Select(c => new NetworkStatus(c.Id, c.Service, c.Address, c.Error, Interlocked.Read(ref c.Completed), c.MqttSettings is null ? null : c.Mqtt?.Connected == true)).ToArray();

    public NetworkCoordinator(XElement? network, Func<string, double[]> readBuffer, Action<string>? fault = null,
        Func<string, string?>? metadata = null, Func<object>? timeInfo = null, HttpMessageHandler? handler = null,
        Func<IReadOnlyList<NetworkBufferRequest>, IReadOnlyDictionary<string, double[]>>? capture = null, Func<string, byte[]>? resource = null)
    {
        this.readBuffer = readBuffer; this.fault = fault; this.metadata = metadata; this.timeInfo = timeInfo; this.capture = capture; this.resource = resource;
        client = new HttpClient(handler ?? new HttpClientHandler { AllowAutoRedirect = false, AutomaticDecompression = DecompressionMethods.GZip | DecompressionMethods.Deflate }, true) { Timeout = Timeout.InfiniteTimeSpan };
        if (network is null) return;
        foreach (var element in network.Elements())
        {
            if (element.Name.Namespace != network.Name.Namespace) continue;
            if (element.Name.LocalName != "connection") { issues.Add($"Unsupported network element: {element.Name.LocalName}"); continue; }
            try { Parse(element); } catch (Exception e) when (e is FormatException or InvalidDataException or ArgumentException) { issues.Add(e.Message); }
        }
        if (connections.Count > 32) issues.Add("At most 32 network connections are supported.");
        if (connections.Select(c => c.Id).Distinct().Count() != connections.Count) issues.Add("Network connection ids must be unique.");
    }
    static string A(XElement e, string name, string fallback = "") => (string?)e.Attribute(name) ?? fallback;
    static bool B(XElement e, string name, bool fallback) => e.Attribute(name) is null ? fallback : bool.Parse(A(e, name));
    void Parse(XElement e)
    {
        var id = A(e, "id", $"connection-{connections.Count}");
        var service = A(e, "service").ToLowerInvariant();
        var conversion = A(e, "conversion", "none").ToLowerInvariant();
        var address = A(e, "address");
        if (service is not ("http/get" or "http/post" or "mqtt/json" or "mqtt/csv" or "mqtts/json" or "mqtts/csv")) issues.Add($"{id}: unsupported network service {service}.");
        var mqtt = service.StartsWith("mqtt", StringComparison.Ordinal);
        if (conversion is not ("json" or "csv" or "none")) issues.Add($"{id}: unsupported conversion {conversion}.");
        if (!mqtt && (!Uri.TryCreate(address, UriKind.Absolute, out var uri) || uri.Scheme is not ("http" or "https") || !string.IsNullOrEmpty(uri.UserInfo))) issues.Add($"{id}: an explicit HTTP(S) address without embedded credentials is required.");
        if (e.Attribute("discovery") is not null) issues.Add($"{id}: network discovery is not implemented; provide an explicit address.");
        var interval = double.Parse(A(e, "interval", "0"), CultureInfo.InvariantCulture);
        if (!double.IsFinite(interval) || interval < 0) throw new InvalidDataException($"{id}: invalid interval.");
        var sends = new List<Send>(); var receives = new List<Receive>();
        foreach (var child in e.Elements().Where(n => n.Name.Namespace == e.Name.Namespace))
        {
            var key = A(child, "id");
            if (key.Length == 0) throw new InvalidDataException($"{id}: send/receive id is required.");
            if (child.Name.LocalName == "send")
            {
                var type = A(child, "type", "buffer").ToLowerInvariant(); var datatype = A(child, "datatype", "array").ToLowerInvariant();
                var keep = B(child, "keep", !B(child, "clear", false));
                if (type is not ("buffer" or "meta" or "time")) issues.Add($"{id}: unsupported send type {type}.");
                if (datatype is not ("number" or "array")) issues.Add($"{id}: unsupported datatype {datatype}.");
                if (type == "meta" && metadata is null) issues.Add($"{id}: real metadata provider required for {child.Value.Trim()}.");
                if (type == "time" && (service == "http/post" || service.EndsWith("/json", StringComparison.Ordinal)) && timeInfo is null) issues.Add($"{id}: actual time-event provider required for POST time metadata.");
                if (type == "buffer" && !keep && capture is null) issues.Add($"{id}: keep=false requires atomic capture-and-consume callback.");
                sends.Add(new(key, type, child.Value.Trim(), datatype, keep));
            }
            else if (child.Name.LocalName == "receive") receives.Add(new(key, child.Value.Trim(), B(child, "append", !B(child, "clear", false))));
            else issues.Add($"{id}: unsupported connection element {child.Name.LocalName}.");
        }
        MqttSettings? settings = null;
        if (mqtt)
        {
            var tls = service.StartsWith("mqtts/", StringComparison.Ordinal); MqttTransport.ParseAddress(address, tls);
            var sendTopic = A(e, "sendTopic"); var certificate = (string?)e.Attribute("certificate");
            if (service.EndsWith("/json", StringComparison.Ordinal) && string.IsNullOrEmpty(sendTopic)) issues.Add($"{id}: MQTT JSON requires sendTopic.");
            if (tls && (string.IsNullOrEmpty(A(e, "username")) || string.IsNullOrEmpty(A(e, "password")))) issues.Add($"{id}: MQTTS requires username and password as in the original format.");
            if (!string.IsNullOrEmpty(certificate) && (certificate.Contains('/') || certificate.Contains('\\') || certificate is "." or "..")) issues.Add($"{id}: unsafe certificate resource name.");
            if (!string.IsNullOrEmpty(certificate) && resource is null) issues.Add($"{id}: certificate resource provider required.");
            settings = new(address, tls, A(e,"receiveTopic"), sendTopic, (string?)e.Attribute("username"), (string?)e.Attribute("password"), certificate, service.EndsWith("/json",StringComparison.Ordinal) && B(e,"persistence",false));
        }
        connections.Add(new() { Id = id, Service = service, Conversion = conversion, Address = address, Interval = interval, Sends = sends.ToArray(), Receives = receives.ToArray(), MqttSettings = settings });
    }
    public void Start()
    {
        lock (stateGate)
        {
            ObjectDisposedException.ThrowIf(disposed, this);
            if (issues.Count != 0) throw new NotSupportedException(string.Join("; ", issues));
            if (running) return;
            lifetime.Dispose(); lifetime = new(); generation++; running = true; clock.Restart();
            foreach (var c in connections) { c.Due = 0; c.Requested = 0; c.Error = null; c.ConnectDue = 0; }
        }
    }
    public void Stop()
    {
        MqttTransport[] transports;
        lock (stateGate) { running = false; generation++; lifetime.Cancel(); clock.Stop(); transports = connections.Where(c=>c.Mqtt is not null).Select(c=>c.Mqtt!).ToArray(); foreach(var c in connections)c.Mqtt=null; while (Pending.TryDequeue(out _)) { } }
        foreach(var transport in transports) transport.Dispose();
    }
    public void RequestTrigger(string id)
    {
        lock (stateGate)
        {
            if (!running) throw new InvalidOperationException("Start the network session explicitly before triggering a request.");
            var c = connections.FirstOrDefault(c => c.Id == id) ?? throw new KeyNotFoundException($"Unknown network id: {id}");
            Interlocked.Exchange(ref c.Requested, 1);
        }
    }
    public async Task TickAsync(CancellationToken cancellationToken = default)
    {
        if (!await ticking.WaitAsync(0, cancellationToken).ConfigureAwait(false)) return;
        try
        {
            Connection[] due; long current; CancellationToken stop;
            lock (stateGate)
            {
                if (!running || disposed) return;
                current = generation; stop = lifetime.Token; var now = clock.Elapsed.TotalSeconds;
                due = connections.Where(c => Interlocked.Exchange(ref c.Requested, 0) == 1 || (c.Interval > 0 && now >= c.Due)).ToArray();
                foreach (var c in due) c.Due = now + c.Interval;
            }
            using var linked = CancellationTokenSource.CreateLinkedTokenSource(stop, cancellationToken);
            await Task.WhenAll(connections.Where(c => c.MqttSettings is not null).Select(c => ConnectMqtt(c, current, linked.Token))).ConfigureAwait(false);
            await Task.WhenAll(due.Select(c => Execute(c, current, linked.Token))).ConfigureAwait(false);
        }
        finally { ticking.Release(); }
    }
    async Task Execute(Connection c, long current, CancellationToken cancellationToken)
    {
        try
        {
            if(c.MqttSettings is not null && c.Mqtt?.Connected != true) return;
            var requests = c.Sends.Where(s => s.Type == "buffer").Select(s => new NetworkBufferRequest(s.Source, s.Keep)).ToArray();
            var values = capture?.Invoke(requests) ?? requests.Select(r => r.Buffer).Distinct().ToDictionary(n => n, n => readBuffer(n));
            var send = new Dictionary<string, object?>();
            foreach (var s in c.Sends)
            {
                if (s.Type == "meta") send[s.Id] = metadata!(s.Source) ?? throw new InvalidDataException($"Metadata unavailable: {s.Source}");
                else if (s.Type == "time") send[s.Id] = (c.Service == "http/get" || c.Service.EndsWith("/csv",StringComparison.Ordinal)) ? DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() / 1000.0 : timeInfo!();
                else
                {
                    var data = values[s.Source];
                    var last = data.Length == 0 ? double.NaN : data[^1];
                    send[s.Id] = c.Service == "http/get" ? last.ToString("R", CultureInfo.InvariantCulture) : s.DataType == "number" ? (double.IsFinite(last) ? (double?)last : null) : data.Select(v => double.IsFinite(v) ? (double?)v : null).ToArray();
                }
            }
            if (c.MqttSettings is not null)
            {
                var mqtt = c.Mqtt!; using var timeoutMqtt = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken); timeoutMqtt.CancelAfter(RequestTimeout);
                if(c.Service.EndsWith("/json",StringComparison.Ordinal)) await mqtt.PublishAsync(c.MqttSettings.SendTopic,JsonSerializer.Serialize(send,new JsonSerializerOptions(JsonSerializerDefaults.Web)),timeoutMqtt.Token).ConfigureAwait(false);
                else foreach(var item in c.Sends)
                {
                    var payload = item.Type == "time" ? (DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()/1000.0).ToString("F3",CultureInfo.InvariantCulture) : item.Type == "meta" ? Convert.ToString(send[item.Id],CultureInfo.InvariantCulture)??"" : item.DataType == "number" ? values[item.Source].Length == 0 ? "" : double.IsFinite(values[item.Source][^1]) ? values[item.Source][^1].ToString("R",CultureInfo.InvariantCulture) : "null" : string.Join(",",values[item.Source].Select(v=>double.IsFinite(v)?v.ToString("R",CultureInfo.InvariantCulture):"null"));
                    await mqtt.PublishAsync(item.Id,payload,timeoutMqtt.Token).ConfigureAwait(false);
                }
                return;
            }
            using var request = new HttpRequestMessage(c.Service == "http/post" ? HttpMethod.Post : HttpMethod.Get, c.Address);
            if (c.Service == "http/post") request.Content = new StringContent(JsonSerializer.Serialize(send, new JsonSerializerOptions(JsonSerializerDefaults.Web)), Encoding.UTF8, "application/json");
            else if (send.Count > 0)
            {
                var builder = new UriBuilder(c.Address);
                var query = string.Join("&", send.Select(p => Uri.EscapeDataString(p.Key) + "=" + Uri.EscapeDataString(Convert.ToString(p.Value, CultureInfo.InvariantCulture) ?? "")));
                builder.Query = builder.Query.TrimStart('?') + (builder.Query.Length > 1 ? "&" : "") + query;
                request.RequestUri = builder.Uri;
            }
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken); timeout.CancelAfter(RequestTimeout);
            using var response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, timeout.Token).ConfigureAwait(false);
            response.EnsureSuccessStatusCode();
            if (response.Content.Headers.ContentLength > MaximumResponseBytes) throw new InvalidDataException("Network response exceeds size limit.");
            using var stream = await response.Content.ReadAsStreamAsync(timeout.Token).ConfigureAwait(false);
            using var output = new MemoryStream(); var block = new byte[8192];
            while (true) { var n = await stream.ReadAsync(block, timeout.Token).ConfigureAwait(false); if (n == 0) break; if (output.Length + n > MaximumResponseBytes) throw new InvalidDataException("Network response exceeds size limit."); output.Write(block, 0, n); }
            var body = Encoding.UTF8.GetString(output.ToArray());
            AcceptResponse(c, body, current, cancellationToken);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { }
        catch (Exception e)
        {
            lock (stateGate) { if (!running || generation != current) return; c.Error = e is OperationCanceledException ? "Network request timed out." : e.Message; }
            try { fault?.Invoke($"{c.Id}: {c.Error}"); } catch { /* Diagnostics must not terminate acquisition. */ }
        }
    }
    async Task ConnectMqtt(Connection c, long current, CancellationToken token)
    {
        try
        {
            lock(stateGate)
            {
                if(!running || generation != current || c.Mqtt?.Connected == true || clock.Elapsed.TotalSeconds < c.ConnectDue) return;
                c.ConnectDue=clock.Elapsed.TotalSeconds+1;
                c.Mqtt ??= new MqttTransport(c.MqttSettings!,resource,bytes=>
                {
                    if(bytes.Length>MaximumResponseBytes) { Report(c,"MQTT response exceeds size limit.",current); return; }
                    try { AcceptResponse(c,Encoding.UTF8.GetString(bytes),current,token); } catch(Exception e) { Report(c,e.Message,current); }
                },message=>Report(c,message,current),RequestTimeout);
            }
            using var timeout=CancellationTokenSource.CreateLinkedTokenSource(token); timeout.CancelAfter(RequestTimeout);
            await c.Mqtt!.ConnectAsync(timeout.Token).ConfigureAwait(false);
            lock(stateGate) { if(running && generation==current)c.Error=null; }
        }
        catch(OperationCanceledException) when(token.IsCancellationRequested) { }
        catch(Exception e) { Report(c,e.Message,current); MqttTransport? failed=null; lock(stateGate) { if(generation==current) { failed=c.Mqtt;c.Mqtt=null; } } failed?.Dispose(); }
    }
    void Report(Connection c,string message,long current)
    {
        lock(stateGate) { if(!running || generation!=current) return; c.Error=message; }
        try { fault?.Invoke($"{c.Id}: {message}"); } catch { }
    }
    void AcceptResponse(Connection c,string body,long current,CancellationToken token)
    {
        using var json=c.Conversion=="json"?JsonDocument.Parse(body):null;
        if(json is not null && json.RootElement.ValueKind!=JsonValueKind.Object) throw new InvalidDataException("Expected JSON object response.");
        var writes=new List<NetworkWrite>(); string? conversionError=null;
        foreach(var r in c.Receives)
        {
            try { writes.Add(new(r.Buffer,NetworkConversion.Read(body,c.Conversion,r.Id,json),r.Append)); }
            catch(InvalidDataException e) { conversionError=e.Message; }
        }
        lock(stateGate)
        {
            if(!running || generation!=current || token.IsCancellationRequested) return;
            while(Pending.Count>=128 && Pending.TryDequeue(out _)) DroppedResponses++;
            Pending.Enqueue(new(c.Id,writes,Array.Empty<string>())); c.Error=conversionError; Interlocked.Increment(ref c.Completed);
        }
        if(conversionError is not null) Report(c,conversionError,current);
    }
    public void Dispose() { lock (stateGate) { if (disposed) return; Stop(); disposed = true; client.Dispose(); /* CTS retained until in-flight callers observe cancellation. */ } }
}
