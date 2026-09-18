namespace Phyphox.Devices;

public enum TransportKind { Ble, Serial, Hid }
public enum DeviceStatus { Disconnected, Connecting, Connected, Faulted }
public sealed record DeviceDescriptor(string Id, string Name, TransportKind Kind)
{
    public string? DeviceInstanceId { get; init; }
    public string? HardwareId { get; init; }
}
public sealed record SerialSettings(int BaudRate, byte DataBits = 8, byte Parity = 0, byte StopBits = 0);
public sealed record HidSettings(int InputReportLength, int OutputReportLength);
public sealed record BleSettings(Guid Service, Guid Characteristic, bool Subscribe = true, bool Indicate = false, bool WriteWithoutResponse = false);
public sealed record DeviceProfile(string Id, TransportKind Kind, SerialSettings? Serial = null, HidSettings? Hid = null, BleSettings? Ble = null);
public sealed record DeviceFrame(byte[] Data, DateTimeOffset ReceivedAt, long MonotonicTicks);

/// <summary>Raw transport only. Frames are not physical samples until an explicit device protocol decodes them.</summary>
public interface IDeviceTransport : IAsyncDisposable
{
    TransportKind Kind { get; }
    bool IsAvailable { get; }
    DeviceStatus Status { get; }
    Task<IReadOnlyList<DeviceDescriptor>> ScanAsync(CancellationToken cancellationToken = default);
    Task ConnectAsync(DeviceDescriptor device, DeviceProfile profile, CancellationToken cancellationToken = default);
    IAsyncEnumerable<DeviceFrame> ReadAsync(CancellationToken cancellationToken = default);
    Task WriteAsync(ReadOnlyMemory<byte> data, CancellationToken cancellationToken = default);
    Task DisconnectAsync(CancellationToken cancellationToken = default);
}

public sealed record HidCapabilities(ushort Usage,ushort UsagePage,ushort InputReportLength,ushort OutputReportLength,ushort FeatureReportLength);
public sealed record BleCharacteristicInfo(Guid Uuid,bool Read,bool Notify,bool Indicate,bool Write,bool WriteWithoutResponse);
public sealed record BleValue(Guid Characteristic,DeviceFrame Frame);
public interface IBleDeviceTransport : IDeviceTransport
{
    Task<IReadOnlyList<BleCharacteristicInfo>> GetCharacteristicsAsync(CancellationToken cancellationToken=default);
    Task SubscribeAsync(Guid characteristic,bool indicate=false,CancellationToken cancellationToken=default);
    Task UnsubscribeAsync(Guid characteristic,CancellationToken cancellationToken=default);
    IAsyncEnumerable<BleValue> ReadValuesAsync(CancellationToken cancellationToken=default);
    Task WriteCharacteristicAsync(Guid characteristic,ReadOnlyMemory<byte> data,bool withoutResponse=false,CancellationToken cancellationToken=default);
    Task<DeviceFrame> ReadCharacteristicAsync(Guid characteristic,CancellationToken cancellationToken=default);
}
