using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Threading.Channels;
using Windows.Devices.Bluetooth;
using Windows.Devices.Bluetooth.Advertisement;
using System.Collections.Concurrent;
using Windows.Devices.Bluetooth.GenericAttributeProfile;
using Windows.Devices.Enumeration;
using Windows.Storage.Streams;
namespace Phyphox.Devices;

/// <summary>One explicit service/characteristic endpoint. Does not invent device protocols or silently reconnect/control devices.</summary>
public sealed class WindowsBleTransport : IBleDeviceTransport
{
    private readonly SemaphoreSlim gate=new(1,1);
    private BluetoothLEDevice? device;
    private GattDeviceService? service;
    private GattCharacteristic? characteristic;
    private BleSettings? settings;
    private Channel<DeviceFrame> frames=CreateQueue();
    private Channel<BleValue> values=Channel.CreateBounded<BleValue>(1024);
    private readonly ConcurrentDictionary<Guid,GattCharacteristic> subscriptions=new();
    private bool multiConsumer;
    private static Channel<DeviceFrame> CreateQueue()=>Channel.CreateBounded<DeviceFrame>(new BoundedChannelOptions(1024){FullMode=BoundedChannelFullMode.Wait,SingleReader=true});
    public TransportKind Kind=>TransportKind.Ble;
    public bool IsAvailable=>OperatingSystem.IsWindowsVersionAtLeast(10,0,19041);
    public DeviceStatus Status { get; private set; }
    public async Task<IReadOnlyList<DeviceDescriptor>> ScanAsync(CancellationToken cancellationToken=default) {
        if(!IsAvailable) throw new PlatformNotSupportedException("Windows 10 build 19041 or newer is required.");
        var found=new ConcurrentDictionary<ulong,DeviceDescriptor>();
        var watcher=new BluetoothLEAdvertisementWatcher { ScanningMode=BluetoothLEScanningMode.Active };
        string? scanError=null;
        watcher.Received+=(sender,args)=> {
            string id=$"ble-address:{args.BluetoothAddress:X12}";
            string name=string.IsNullOrWhiteSpace(args.Advertisement.LocalName)?id:args.Advertisement.LocalName;
            found.AddOrUpdate(args.BluetoothAddress,new DeviceDescriptor(id,name,Kind),(_,previous)=>name==id?previous:new DeviceDescriptor(id,name,Kind));
        };
        watcher.Stopped+=(sender,args)=> { if(args.Error!=BluetoothError.Success) scanError=args.Error.ToString(); };
        try {
            watcher.Start(); await Task.Delay(TimeSpan.FromSeconds(5),cancellationToken);
            if(scanError!=null) throw new IOException($"Bluetooth scan failed: {scanError}");
            return found.Values.OrderBy(x=>x.Name,StringComparer.OrdinalIgnoreCase).ToArray();
        } finally { watcher.Stop(); }
    }
    public async Task ConnectAsync(DeviceDescriptor descriptor,DeviceProfile profile,CancellationToken cancellationToken=default) {
        if(!IsAvailable) throw new PlatformNotSupportedException();
        if(profile.Kind!=Kind || descriptor.Kind!=Kind || profile.Ble==null) throw new ArgumentException("Explicit GATT endpoint required.");
        using var timeout=CancellationTokenSource.CreateLinkedTokenSource(cancellationToken); timeout.CancelAfter(TimeSpan.FromSeconds(20));
        await gate.WaitAsync(timeout.Token);
        if(device!=null) { gate.Release(); throw new InvalidOperationException("Disconnect first."); }
        try {
            Status=DeviceStatus.Connecting; settings=profile.Ble; frames=CreateQueue(); values=Channel.CreateBounded<BleValue>(1024); multiConsumer=false;
            device=descriptor.Id.StartsWith("ble-address:",StringComparison.Ordinal)
                ? await BluetoothLEDevice.FromBluetoothAddressAsync(Convert.ToUInt64(descriptor.Id[12..],16)).AsTask(timeout.Token)
                : await BluetoothLEDevice.FromIdAsync(descriptor.Id).AsTask(timeout.Token);
            if(device==null) throw new IOException("Bluetooth device unavailable or access denied.");
            device.ConnectionStatusChanged+=ConnectionChanged;
            var services=await device.GetGattServicesForUuidAsync(settings.Service,BluetoothCacheMode.Uncached).AsTask(timeout.Token);
            Check(services.Status);
            service=services.Services.FirstOrDefault() ?? throw new IOException("GATT service not found.");
            foreach(var extra in services.Services.Skip(1)) extra.Dispose();
            var chars=await service.GetCharacteristicsForUuidAsync(settings.Characteristic,BluetoothCacheMode.Uncached).AsTask(timeout.Token);
            Check(chars.Status); characteristic=chars.Characteristics.FirstOrDefault() ?? throw new IOException("GATT characteristic not found.");
            if(settings.Subscribe) {
                characteristic.ValueChanged+=ValueChanged; subscriptions[characteristic.Uuid]=characteristic;
                var mode=settings.Indicate?GattClientCharacteristicConfigurationDescriptorValue.Indicate:GattClientCharacteristicConfigurationDescriptorValue.Notify;
                Check(await characteristic.WriteClientCharacteristicConfigurationDescriptorAsync(mode).AsTask(timeout.Token));
            }
            Status=DeviceStatus.Connected;
        } catch { Cleanup(); Status=DeviceStatus.Faulted; throw; }
        finally {gate.Release();}
    }
    private static void Check(GattCommunicationStatus status) { if(status!=GattCommunicationStatus.Success) throw new IOException($"GATT communication failed: {status}"); }
    private void ConnectionChanged(BluetoothLEDevice sender,object args) {
        if(!ReferenceEquals(sender,device)) return;
        if(sender.ConnectionStatus==BluetoothConnectionStatus.Disconnected && Status==DeviceStatus.Connected) {
            Status=DeviceStatus.Faulted; frames.Writer.TryComplete(new IOException("BLE disconnected. Explicit reconnect is required; no commands were replayed.")); values.Writer.TryComplete(new IOException("BLE disconnected."));
        }
    }
    private void ValueChanged(GattCharacteristic sender,GattValueChangedEventArgs args) {
        if(!subscriptions.TryGetValue(sender.Uuid,out var active) || !ReferenceEquals(sender,active)) return;
        var frame=Decode(args.CharacteristicValue);
        bool accepted=multiConsumer?values.Writer.TryWrite(new BleValue(sender.Uuid,frame)):frames.Writer.TryWrite(frame);
        if(!accepted) { Status=DeviceStatus.Faulted; var error=new IOException("BLE receive queue overflow; acquisition must be restarted.");frames.Writer.TryComplete(error);values.Writer.TryComplete(error); }
    }
    private static DeviceFrame Decode(IBuffer buffer) {
        var bytes=new byte[buffer.Length]; using(var reader=DataReader.FromBuffer(buffer)) reader.ReadBytes(bytes);
        return new DeviceFrame(bytes,DateTimeOffset.UtcNow,Stopwatch.GetTimestamp());
    }
    public async IAsyncEnumerable<DeviceFrame> ReadAsync([EnumeratorCancellation] CancellationToken cancellationToken=default) {
        if(characteristic==null || settings==null) throw new InvalidOperationException("Not connected.");
        if(settings.Subscribe) { await foreach(var frame in frames.Reader.ReadAllAsync(cancellationToken)) yield return frame; }
        else {
            // A single explicit read; caller determines polling interval, never a hidden device sampling rate.
            DeviceFrame result;
            using var timeout=CancellationTokenSource.CreateLinkedTokenSource(cancellationToken); timeout.CancelAfter(TimeSpan.FromSeconds(10));
            await gate.WaitAsync(timeout.Token);
            try { var read=await characteristic.ReadValueAsync(BluetoothCacheMode.Uncached).AsTask(timeout.Token); Check(read.Status); result=Decode(read.Value); }
            finally {gate.Release();}
            yield return result;
        }
    }
    public async Task WriteAsync(ReadOnlyMemory<byte> data,CancellationToken cancellationToken=default) {
        using var timeout=CancellationTokenSource.CreateLinkedTokenSource(cancellationToken); timeout.CancelAfter(TimeSpan.FromSeconds(10));
        await gate.WaitAsync(timeout.Token);
        try {
            if(characteristic==null || settings==null || Status!=DeviceStatus.Connected) throw new InvalidOperationException("Not connected.");
            using var writer=new DataWriter(); writer.WriteBytes(data.ToArray());
            Check(await characteristic.WriteValueAsync(writer.DetachBuffer(),settings.WriteWithoutResponse?GattWriteOption.WriteWithoutResponse:GattWriteOption.WriteWithResponse).AsTask(timeout.Token));
        } finally {gate.Release();}
    }
    /// <summary>Serialized writes to another characteristic in the connected service (configuration/control).</summary>
    public async Task WriteCharacteristicAsync(Guid uuid,ReadOnlyMemory<byte> data,bool withoutResponse=false,CancellationToken cancellationToken=default) {
        using var timeout=CancellationTokenSource.CreateLinkedTokenSource(cancellationToken); timeout.CancelAfter(TimeSpan.FromSeconds(10));
        await gate.WaitAsync(timeout.Token);
        try {
            if(service==null || Status!=DeviceStatus.Connected) throw new InvalidOperationException("Not connected.");
            var found=await service.GetCharacteristicsForUuidAsync(uuid,BluetoothCacheMode.Uncached).AsTask(timeout.Token); Check(found.Status);
            var target=found.Characteristics.FirstOrDefault() ?? throw new IOException("Requested characteristic not found.");
            using var writer=new DataWriter(); writer.WriteBytes(data.ToArray());
            Check(await target.WriteValueAsync(writer.DetachBuffer(),withoutResponse?GattWriteOption.WriteWithoutResponse:GattWriteOption.WriteWithResponse).AsTask(timeout.Token));
        } finally {gate.Release();}
    }
    public async Task<DeviceFrame> ReadCharacteristicAsync(Guid uuid,CancellationToken cancellationToken=default) {
        using var timeout=CancellationTokenSource.CreateLinkedTokenSource(cancellationToken); timeout.CancelAfter(TimeSpan.FromSeconds(10));
        await gate.WaitAsync(timeout.Token);
        try {
            if(service==null || Status!=DeviceStatus.Connected) throw new InvalidOperationException("Not connected.");
            var found=await service.GetCharacteristicsForUuidAsync(uuid,BluetoothCacheMode.Uncached).AsTask(timeout.Token); Check(found.Status);
            var target=found.Characteristics.FirstOrDefault() ?? throw new IOException("Requested characteristic not found.");
            var result=await target.ReadValueAsync(BluetoothCacheMode.Uncached).AsTask(timeout.Token); Check(result.Status); return Decode(result.Value);
        } finally {gate.Release();}
    }
    public async Task<IReadOnlyList<BleCharacteristicInfo>> GetCharacteristicsAsync(CancellationToken cancellationToken=default) {
        using var timeout=CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);timeout.CancelAfter(TimeSpan.FromSeconds(10));
        await gate.WaitAsync(timeout.Token);
        try {
            if(service==null)throw new InvalidOperationException("Not connected.");
            var result=await service.GetCharacteristicsAsync(BluetoothCacheMode.Uncached).AsTask(timeout.Token);Check(result.Status);
            return result.Characteristics.Select(c=>new BleCharacteristicInfo(c.Uuid,c.CharacteristicProperties.HasFlag(GattCharacteristicProperties.Read),c.CharacteristicProperties.HasFlag(GattCharacteristicProperties.Notify),c.CharacteristicProperties.HasFlag(GattCharacteristicProperties.Indicate),c.CharacteristicProperties.HasFlag(GattCharacteristicProperties.Write),c.CharacteristicProperties.HasFlag(GattCharacteristicProperties.WriteWithoutResponse))).ToArray();
        } finally {gate.Release();}
    }
    public async Task SubscribeAsync(Guid uuid,bool indicate=false,CancellationToken cancellationToken=default) {
        using var timeout=CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);timeout.CancelAfter(TimeSpan.FromSeconds(10));
        await gate.WaitAsync(timeout.Token);
        try {
            if(service==null)throw new InvalidOperationException("Not connected.");
            multiConsumer=true;
            if(subscriptions.ContainsKey(uuid))return;
            var found=await service.GetCharacteristicsForUuidAsync(uuid,BluetoothCacheMode.Uncached).AsTask(timeout.Token);Check(found.Status);
            var target=found.Characteristics.FirstOrDefault()??throw new IOException("Characteristic not found.");
            subscriptions[uuid]=target;target.ValueChanged+=ValueChanged;
            try {Check(await target.WriteClientCharacteristicConfigurationDescriptorAsync(indicate?GattClientCharacteristicConfigurationDescriptorValue.Indicate:GattClientCharacteristicConfigurationDescriptorValue.Notify).AsTask(timeout.Token));}
            catch {subscriptions.TryRemove(uuid,out _);target.ValueChanged-=ValueChanged;throw;}
        } finally {gate.Release();}
    }
    public async Task UnsubscribeAsync(Guid uuid,CancellationToken cancellationToken=default) {
        using var timeout=CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);timeout.CancelAfter(TimeSpan.FromSeconds(2));
        await gate.WaitAsync(timeout.Token);
        try {
            if(!subscriptions.TryRemove(uuid,out var target))return;
            target.ValueChanged-=ValueChanged;
            Check(await target.WriteClientCharacteristicConfigurationDescriptorAsync(GattClientCharacteristicConfigurationDescriptorValue.None).AsTask(timeout.Token));
        } finally {gate.Release();}
    }
    public async IAsyncEnumerable<BleValue> ReadValuesAsync([EnumeratorCancellation] CancellationToken cancellationToken=default) {
        await foreach(var value in values.Reader.ReadAllAsync(cancellationToken))yield return value;
    }
    public async Task DisconnectAsync(CancellationToken cancellationToken=default) {
        await gate.WaitAsync(cancellationToken);
        try { Cleanup(); Status=DeviceStatus.Disconnected; } finally {gate.Release();}
    }
    private void Cleanup() {
        frames.Writer.TryComplete();values.Writer.TryComplete();
        foreach(var target in subscriptions.Values)target.ValueChanged-=ValueChanged;subscriptions.Clear();
        if(device!=null) device.ConnectionStatusChanged-=ConnectionChanged;
        characteristic=null; service?.Dispose(); service=null; device?.Dispose(); device=null; settings=null;
    }
    public async ValueTask DisposeAsync()=>await DisconnectAsync();
}
