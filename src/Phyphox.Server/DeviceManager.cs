// SPDX-License-Identifier: GPL-3.0-only
using System.Collections.Concurrent;
using Phyphox.Devices;

namespace Phyphox.Server;
public sealed record ScanRequest(TransportKind Kind);
public sealed record ConnectRequest(DeviceDescriptor Device, DeviceProfile Profile);
public sealed record WriteRequest(string Hex);
public sealed class DeviceManager : IAsyncDisposable
{
    readonly ConcurrentDictionary<string, DeviceDescriptor> scanned = new();
    readonly ConcurrentDictionary<string, Connection> connections = new();
    readonly ConcurrentDictionary<string, SemaphoreSlim> deviceGates = new();
    public event Action<string, DeviceFrame>? FrameReceived;
    public event Action<string,BleValue>? BleFrameReceived;
    public event Action<string,string>? ConnectionFaulted;
    public bool IsConnected(string id) => connections.TryGetValue(id, out var c) && c.Transport.Status == DeviceStatus.Connected && c.Error is null;
    public DeviceDescriptor Descriptor(string id) => Get(id).Device;
    public DeviceProfile Profile(string id) => Get(id).Profile;
    public object List() => new
    {
        items = scanned.Values.ToArray(),
        connections = connections.Select(p => new { connectionId = p.Key, device = p.Value.Device, status = p.Value.Transport.Status, error = p.Value.Error }),
        capabilities = new[] { Capability("serial"), Capability("hid"), Capability("ble"),
            new { kind = "audio", status = OperatingSystem.IsWindows() ? "implementedUnverified" : "unavailable", reason = "WASAPI 已实现；循环播放与采集已接入，非循环输出时序尚未完成，设备未验收。" },
            new { kind = "camera", status = OperatingSystem.IsWindows() ? "partialUnverified" : "unavailable", reason = "MSMF 采集与图像分析已实现；需明确相机配置与曝光条件，设备未验收。" } }
    };
    static dynamic Capability(string kind)
    {
        var available = OperatingSystem.IsWindows();
#if !WINDOWS
        if (kind == "ble") available = false;
#endif
        return new { kind, status = available ? "implementedUnverified" : "unavailable", reason = available ? "传输层已实现；需要真实设备协议与独立验收，未验证硬件兼容。" : "当前构建/操作系统无法访问此 Windows 设备接口。" };
    }
    static IDeviceTransport Create(TransportKind kind) => kind switch
    {
        TransportKind.Serial => new WindowsSerialTransport(),
        TransportKind.Hid => new WindowsHidTransport(),
#if WINDOWS
        TransportKind.Ble => new WindowsBleTransport(),
#endif
        _ => throw new PlatformNotSupportedException("此构建不提供 Windows BLE 接入，请使用 win-x64 发布包。")
    };
    public async Task<object> Scan(TransportKind kind, CancellationToken ct)
    {
        await using var transport = Create(kind);
        if (!transport.IsAvailable) throw new PlatformNotSupportedException("当前操作系统不支持此 Windows 设备接口。");
        var found = await transport.ScanAsync(ct);
        foreach (var item in found) scanned[item.Id] = item;
        return new { items = found };
    }
    public async Task<object> Connect(ConnectRequest request, CancellationToken ct)
    {
        if (!scanned.TryGetValue(request.Device.Id, out var device) || device.Kind != request.Profile.Kind) throw new InvalidOperationException("必须先扫描并选择真实设备，且配置传输类型必须一致。");
        var deviceGate=deviceGates.GetOrAdd(device.Id,_=>new SemaphoreSlim(1,1));
        await deviceGate.WaitAsync(ct);
        try {
        if (connections.Values.Any(c => c.Device.Id == device.Id)) throw new InvalidOperationException("此设备已有连接。");
        var transport = Create(device.Kind);
        try
        {
            await transport.ConnectAsync(device, request.Profile, ct);
            var id = Guid.NewGuid().ToString("N");
            var connection = new Connection(device, request.Profile, transport);
            if(transport is IBleDeviceTransport ble) {
                if(request.Profile.Ble is {Subscribe:true} initial) {
                    await ble.SubscribeAsync(initial.Characteristic,initial.Indicate,ct);
                    connection.Subscriptions[initial.Characteristic]=(1,initial.Indicate);
                }
                connections[id]=connection;
                connection.Reader=ReadBle(id,connection,ble);
            } else {connections[id]=connection;connection.Reader = Read(id, connection);}
            return new { connectionId = id };
        }
        catch { await transport.DisposeAsync(); throw; }
        } finally {deviceGate.Release();}
    }
    async Task Read(string id, Connection connection)
    {
        try
        {
            await foreach (var frame in connection.Transport.ReadAsync(connection.Cancellation.Token))
            {
                connection.Frames.Enqueue(new FrameDto(Convert.ToHexString(frame.Data), frame.ReceivedAt, Interlocked.Increment(ref connection.Sequence)));
                while (connection.Frames.Count > 100) connection.Frames.TryDequeue(out _);
                FrameReceived?.Invoke(id, frame);
            }
        }
        catch (OperationCanceledException) when (connection.Cancellation.IsCancellationRequested) { }
        catch (Exception ex) { connection.Error = ex.Message;ConnectionFaulted?.Invoke(id,ex.Message); }
    }
    async Task ReadBle(string id,Connection connection,IBleDeviceTransport ble) {
        try {
            await foreach(var value in ble.ReadValuesAsync(connection.Cancellation.Token)) {
                connection.Frames.Enqueue(new FrameDto(Convert.ToHexString(value.Frame.Data),value.Frame.ReceivedAt,Interlocked.Increment(ref connection.Sequence),value.Characteristic));
                while(connection.Frames.Count>100)connection.Frames.TryDequeue(out _);
                BleFrameReceived?.Invoke(id,value);
                if(value.Characteristic==connection.Profile.Ble?.Characteristic)FrameReceived?.Invoke(id,value.Frame);
            }
        }catch(OperationCanceledException)when(connection.Cancellation.IsCancellationRequested){}
        catch(Exception ex){connection.Error=ex.Message;ConnectionFaulted?.Invoke(id,ex.Message);}
    }
    public async Task SubscribeBleAsync(string id,Guid characteristic,bool indicate,CancellationToken ct=default) {
        var connection=Get(id);if(connection.Transport is not IBleDeviceTransport ble)throw new InvalidOperationException("BLE transport required.");
        await connection.SubscriptionGate.WaitAsync(ct);
        try {
            if(connection.Subscriptions.TryGetValue(characteristic,out var lease)) {
                if(lease.Indicate!=indicate)throw new InvalidOperationException("Conflicting notification/indication modes for the same characteristic.");
                connection.Subscriptions[characteristic]=(lease.Count+1,indicate);return;
            }
            await ble.SubscribeAsync(characteristic,indicate,ct);connection.Subscriptions[characteristic]=(1,indicate);
        }finally{connection.SubscriptionGate.Release();}
    }
    public async Task UnsubscribeBleAsync(string id,Guid characteristic,CancellationToken ct=default) {
        if(!connections.TryGetValue(id,out var connection)||connection.Transport is not IBleDeviceTransport ble)return;
        await connection.SubscriptionGate.WaitAsync(ct);
        try {
            if(!connection.Subscriptions.TryGetValue(characteristic,out var lease))return;
            if(lease.Count>1){connection.Subscriptions[characteristic]=(lease.Count-1,lease.Indicate);return;}
            connection.Subscriptions.Remove(characteristic);await ble.UnsubscribeAsync(characteristic,ct);
        }finally{connection.SubscriptionGate.Release();}
    }
    public async Task<DeviceFrame> ReadBleCharacteristicAsync(string id,Guid characteristic,CancellationToken ct=default) {
        var connection=Get(id);if(!IsConnected(id)||connection.Transport is not IBleDeviceTransport ble)throw new IOException("Connected BLE transport required.");
        try{return await ble.ReadCharacteristicAsync(characteristic,ct);}
        catch(OperationCanceledException){throw;}
        catch(Exception ex){connection.Error=ex.Message;ConnectionFaulted?.Invoke(id,ex.Message);throw;}
    }
    Connection Get(string id) => connections.TryGetValue(id, out var connection) ? connection : throw new KeyNotFoundException("连接不存在。");
    public object Frames(string id)
    {
        var connection = Get(id);
        return new { frames = connection.Frames.ToArray(), status = connection.Transport.Status, error = connection.Error, transportOnly = true, hardwareVerified = false };
    }
    public async Task Write(string id, string hex, CancellationToken ct)
    {
        var normalized = string.Concat(hex.Where(c => !char.IsWhiteSpace(c)));
        if (normalized.Length is 0 or > 131072 || normalized.Length % 2 != 0) throw new InvalidOperationException("十六进制命令长度无效。");
        await WriteBytesAsync(id,Convert.FromHexString(normalized),ct);
    }
    public async Task WriteBytesAsync(string id,ReadOnlyMemory<byte> bytes,CancellationToken ct=default) {
        if(bytes.Length is <1 or >65536)throw new ArgumentException("Device write must contain 1..65536 bytes.");
        if(!IsConnected(id))throw new IOException("Device connection is unavailable.");
        var connection=Get(id);
        try {await connection.Transport.WriteAsync(bytes,ct);}
        catch(OperationCanceledException){throw;}
        catch(Exception ex){connection.Error=ex.Message;ConnectionFaulted?.Invoke(id,ex.Message);throw;}
    }
    public async Task WriteCharacteristicAsync(string id,Guid characteristic,ReadOnlyMemory<byte> bytes,bool withoutResponse=false,CancellationToken ct=default) {
        if(bytes.Length is <1 or >65536)throw new ArgumentException("BLE write length invalid.");
        if(!IsConnected(id))throw new IOException("BLE connection is unavailable.");
        var connection=Get(id);
        if(connection.Transport is not IBleDeviceTransport ble)throw new InvalidOperationException("Connection is not a BLE GATT transport.");
        try {await ble.WriteCharacteristicAsync(characteristic,bytes,withoutResponse,ct);}
        catch(OperationCanceledException){throw;}
        catch(Exception ex){connection.Error=ex.Message;ConnectionFaulted?.Invoke(id,ex.Message);throw;}
    }
    public async Task<IReadOnlyList<BleCharacteristicInfo>> BleCapabilitiesAsync(string id,CancellationToken ct=default) {
        if(!IsConnected(id)||Get(id).Transport is not IBleDeviceTransport ble)throw new InvalidOperationException("Connected BLE device required.");
        return await ble.GetCharacteristicsAsync(ct);
    }
    public async Task<byte[]> DownloadExperimentAsync(string deviceId,CancellationToken ct=default) {
        if(!scanned.TryGetValue(deviceId,out var device)||device.Kind!=TransportKind.Ble)throw new InvalidOperationException("先扫描并选择真实 BLE 设备。");
        var deviceGate=deviceGates.GetOrAdd(device.Id,_=>new SemaphoreSlim(1,1));await deviceGate.WaitAsync(ct);
        try {
            if(connections.Values.Any(c=>c.Device.Id==device.Id))throw new InvalidOperationException("实验下载需要独立连接；请先显式断开该设备的现有连接。");
            await using var transport=Create(TransportKind.Ble);
            if(transport is not IBleDeviceTransport ble)throw new PlatformNotSupportedException("当前构建没有 BLE 下载实现。");
            using var timeout=CancellationTokenSource.CreateLinkedTokenSource(ct);timeout.CancelAfter(TimeSpan.FromMinutes(5));
            return await BleExperimentDownloader.DownloadAsync(ble,device,cancellationToken:timeout.Token);
        } finally {deviceGate.Release();}
    }
    public async Task Disconnect(string id)
    {
        if (!connections.TryRemove(id, out var connection)) return;
        ConnectionFaulted?.Invoke(id,"Device disconnected explicitly.");
        connection.Cancellation.Cancel();
        try { await connection.Transport.DisconnectAsync(); }
        finally
        {
            try { if (connection.Reader is not null) await connection.Reader.WaitAsync(TimeSpan.FromSeconds(5)); } catch (TimeoutException) { }
            await connection.Transport.DisposeAsync(); connection.Cancellation.Dispose();
        }
    }
    public async ValueTask DisposeAsync() { foreach (var id in connections.Keys) await Disconnect(id); }
    sealed class Connection(DeviceDescriptor device, DeviceProfile profile, IDeviceTransport transport)
    {
        public DeviceDescriptor Device { get; } = device;
        public DeviceProfile Profile { get; } = profile;
        public IDeviceTransport Transport { get; } = transport;
        public CancellationTokenSource Cancellation { get; } = new();
        public SemaphoreSlim SubscriptionGate {get;}=new(1,1);
        public Dictionary<Guid,(int Count,bool Indicate)> Subscriptions {get;}=[];
        public ConcurrentQueue<FrameDto> Frames { get; } = new();
        public Task? Reader; public string? Error; public long Sequence;
    }
    sealed record FrameDto(string Hex, DateTimeOffset ReceivedAt, long Sequence,Guid? Characteristic=null);
}
