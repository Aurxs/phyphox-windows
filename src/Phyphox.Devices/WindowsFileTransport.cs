using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using Microsoft.Win32;
namespace Phyphox.Devices;

public abstract class WindowsFileTransport : IDeviceTransport
{
    private FileStream? stream;
    private readonly SemaphoreSlim writes = new(1,1);
    private int reading;
    protected Microsoft.Win32.SafeHandles.SafeFileHandle ConnectedHandle => stream?.SafeFileHandle ?? throw new InvalidOperationException("Not connected.");
    protected DeviceProfile? Profile { get; private set; }
    protected virtual int ReadSize => 4096;
    public abstract TransportKind Kind { get; }
    public bool IsAvailable => OperatingSystem.IsWindows();
    public DeviceStatus Status { get; protected set; }
    public abstract Task<IReadOnlyList<DeviceDescriptor>> ScanAsync(CancellationToken cancellationToken = default);
    protected void RequireWindows() { if(!IsAvailable) throw new PlatformNotSupportedException("Real device access requires Windows; no simulated device is substituted."); }
    protected virtual void Configure(Microsoft.Win32.SafeHandles.SafeFileHandle handle,DeviceProfile profile) { }
    public Task ConnectAsync(DeviceDescriptor device,DeviceProfile profile,CancellationToken cancellationToken=default) {
        RequireWindows(); cancellationToken.ThrowIfCancellationRequested();
        if(stream != null) throw new InvalidOperationException("Disconnect the current device first.");
        if(device.Kind!=Kind || profile.Kind!=Kind) throw new ArgumentException("Transport/profile mismatch.");
        if(Kind==TransportKind.Serial && (!device.Id.StartsWith(@"\\.\COM",StringComparison.OrdinalIgnoreCase) || !int.TryParse(device.Id[7..],out var port) || port<1)) throw new ArgumentException("Expected an enumerated COM device path.");
        if(Kind==TransportKind.Hid && !device.Id.StartsWith(@"\\?\hid#",StringComparison.OrdinalIgnoreCase)) throw new ArgumentException("Expected an enumerated HID interface path.");
        Status=DeviceStatus.Connecting;
        try {
            var handle=WindowsNative.Open(device.Id);
            try { Configure(handle,profile); stream=new FileStream(handle,FileAccess.ReadWrite,4096,true); }
            catch { handle.Dispose(); throw; }
            Profile=profile; Status=DeviceStatus.Connected; return Task.CompletedTask;
        } catch { Status=DeviceStatus.Faulted; throw; }
    }
    public async IAsyncEnumerable<DeviceFrame> ReadAsync([EnumeratorCancellation] CancellationToken cancellationToken=default) {
        var input=stream ?? throw new InvalidOperationException("Not connected.");
        if(Interlocked.Exchange(ref reading,1)!=0) throw new InvalidOperationException("Only one reader per device is allowed.");
        try {
            var bytes=new byte[ReadSize];
            while(!cancellationToken.IsCancellationRequested) {
                int count;
                try { count=await input.ReadAsync(bytes,cancellationToken); }
                catch(OperationCanceledException) { throw; }
                catch { if(ReferenceEquals(stream,input))Status=DeviceStatus.Faulted; throw; }
                if(count==0) { await Task.Delay(10,cancellationToken); continue; }
                yield return new DeviceFrame(bytes[..count],DateTimeOffset.UtcNow,Stopwatch.GetTimestamp());
            }
        } finally { Interlocked.Exchange(ref reading,0); }
    }
    protected virtual void ValidateWrite(ReadOnlyMemory<byte> data) { }
    public async Task WriteAsync(ReadOnlyMemory<byte> data,CancellationToken cancellationToken=default) {
        ValidateWrite(data); await writes.WaitAsync(cancellationToken);
        try { await (stream ?? throw new InvalidOperationException("Not connected.")).WriteAsync(data,cancellationToken); }
        catch(OperationCanceledException) { throw; }
        catch { if(stream!=null)Status=DeviceStatus.Faulted; throw; }
        finally { writes.Release(); }
    }
    public async Task DisconnectAsync(CancellationToken cancellationToken=default) {
        var old=Interlocked.Exchange(ref stream,null);
        if(old!=null) await old.DisposeAsync();
        Profile=null; Status=DeviceStatus.Disconnected;
    }
    public async ValueTask DisposeAsync() { await DisconnectAsync(); }
}

public sealed class WindowsSerialTransport : WindowsFileTransport
{
    public override TransportKind Kind=>TransportKind.Serial;
    public override Task<IReadOnlyList<DeviceDescriptor>> ScanAsync(CancellationToken cancellationToken=default) {
        if(!OperatingSystem.IsWindows()) throw new PlatformNotSupportedException("Serial access requires Windows.");
        cancellationToken.ThrowIfCancellationRequested();
        var result=new List<DeviceDescriptor>();
        using var key=Registry.LocalMachine.OpenSubKey(@"HARDWARE\DEVICEMAP\SERIALCOMM");
        if(key!=null) foreach(var name in key.GetValueNames()) if(key.GetValue(name) is string port) result.Add(new DeviceDescriptor(@"\\.\"+port,port,Kind));
        // Device instance IDs expose identity without claiming every USB device has a serial number.
        using var usb=Registry.LocalMachine.OpenSubKey(@"SYSTEM\CurrentControlSet\Enum\USB");
        if(usb!=null) foreach(var hardware in usb.GetSubKeyNames()) {
            using var hardwareKey=usb.OpenSubKey(hardware);
            if(hardwareKey==null) continue;
            foreach(var instance in hardwareKey.GetSubKeyNames()) {
                cancellationToken.ThrowIfCancellationRequested();
                using var parameters=hardwareKey.OpenSubKey(instance+@"\Device Parameters");
                if(parameters?.GetValue("PortName") is not string port) continue;
                var match=result.FindIndex(x=>x.Id==@"\\.\"+port);
                if(match>=0) result[match]=result[match] with { DeviceInstanceId=@"USB\"+hardware+@"\"+instance, HardwareId=hardware };
            }
        }
        return Task.FromResult<IReadOnlyList<DeviceDescriptor>>(result.DistinctBy(x=>x.Id).ToArray());
    }
    protected override void Configure(Microsoft.Win32.SafeHandles.SafeFileHandle handle,DeviceProfile profile) {
        var s=profile.Serial ?? throw new ArgumentException("Explicit serial settings are required.");
        if(s.BaudRate<=0 || s.DataBits is <5 or >8 || s.Parity>4 || s.StopBits>2) throw new ArgumentException("Invalid serial settings.");
        var state=new WindowsNative.Dcb { Length=(uint)Marshal.SizeOf<WindowsNative.Dcb>() };
        WindowsNative.Check(WindowsNative.GetCommState(handle,ref state));
        state.BaudRate=(uint)s.BaudRate; state.ByteSize=s.DataBits; state.Parity=s.Parity; state.StopBits=s.StopBits;
        // Binary mode; parity when requested. Disable inherited software/hardware flow control.
        state.Flags=1u | (s.Parity==0 ? 0u : 2u);
        WindowsNative.Check(WindowsNative.SetCommState(handle,ref state));
        var timeout=new WindowsNative.CommTimeouts { ReadInterval=uint.MaxValue,ReadMultiplier=uint.MaxValue,ReadConstant=100,WriteConstant=5000 };
        WindowsNative.Check(WindowsNative.SetCommTimeouts(handle,ref timeout));
    }
}

public sealed class WindowsHidTransport : WindowsFileTransport
{
    public byte[] GetFeature(byte reportId,int reportLength) {
        if(reportLength<1 || reportLength>65536) throw new ArgumentOutOfRangeException(nameof(reportLength));
        var bytes=new byte[reportLength]; bytes[0]=reportId;
        WindowsNative.Check(WindowsNative.HidD_GetFeature(ConnectedHandle,bytes,bytes.Length)); return bytes;
    }
    public void SetFeature(ReadOnlySpan<byte> report) {
        if(report.Length<1 || report.Length>65536) throw new ArgumentOutOfRangeException(nameof(report));
        var bytes=report.ToArray(); WindowsNative.Check(WindowsNative.HidD_SetFeature(ConnectedHandle,bytes,bytes.Length));
    }
    public HidCapabilities GetCapabilities() {
        WindowsNative.Check(WindowsNative.HidD_GetPreparsedData(ConnectedHandle,out var pointer));
        try {
            var caps=new WindowsNative.HidCaps { Reserved=new ushort[17] };
            var status=WindowsNative.HidP_GetCaps(pointer,ref caps);
            if(status!=0x00110000) throw new IOException($"HID descriptor query failed: {status:X8}");
            return new HidCapabilities(caps.Usage,caps.UsagePage,caps.InputReportByteLength,caps.OutputReportByteLength,caps.FeatureReportByteLength);
        } finally {WindowsNative.HidD_FreePreparsedData(pointer);}
    }

    public override TransportKind Kind=>TransportKind.Hid;
    protected override int ReadSize => Profile?.Hid?.InputReportLength ?? throw new InvalidOperationException("HID profile missing.");
    protected override void Configure(Microsoft.Win32.SafeHandles.SafeFileHandle handle,DeviceProfile profile) {
        if(profile.Hid is not { InputReportLength: >0 and <=65536, OutputReportLength: >=0 and <=65536 }) throw new ArgumentException("Explicit HID report lengths are required (including report ID byte).");
    }
    protected override void ValidateWrite(ReadOnlyMemory<byte> data) {
        if(Profile?.Hid is not {} hid || hid.OutputReportLength==0 || data.Length!=hid.OutputReportLength) throw new ArgumentException("HID writes must contain the exact configured report length, including report ID.");
    }
    public override Task<IReadOnlyList<DeviceDescriptor>> ScanAsync(CancellationToken cancellationToken=default) {
        RequireWindows(); WindowsNative.HidD_GetHidGuid(out var guid);
        var set=WindowsNative.SetupDiGetClassDevs(ref guid,null,IntPtr.Zero,0x12);
        if(set==new IntPtr(-1)) throw new System.ComponentModel.Win32Exception(Marshal.GetLastWin32Error());
        var result=new List<DeviceDescriptor>();
        try {
            for(uint index=0;;index++) {
                cancellationToken.ThrowIfCancellationRequested();
                var entry=new WindowsNative.InterfaceData {Size=Marshal.SizeOf<WindowsNative.InterfaceData>()};
                if(!WindowsNative.SetupDiEnumDeviceInterfaces(set,IntPtr.Zero,ref guid,index,ref entry)) {
                    if(Marshal.GetLastWin32Error()==259) break;
                    throw new System.ComponentModel.Win32Exception(Marshal.GetLastWin32Error());
                }
                WindowsNative.SetupDiGetDeviceInterfaceDetail(set,ref entry,IntPtr.Zero,0,out var required,IntPtr.Zero);
                var memory=Marshal.AllocHGlobal(checked((int)required));
                try {
                    Marshal.WriteInt32(memory,IntPtr.Size==8?8:6);
                    WindowsNative.Check(WindowsNative.SetupDiGetDeviceInterfaceDetail(set,ref entry,memory,required,out _,IntPtr.Zero));
                    var path=Marshal.PtrToStringUni(memory+4)!;
                    result.Add(new DeviceDescriptor(path,path,Kind));
                } finally {Marshal.FreeHGlobal(memory);}
            }
        } finally {WindowsNative.SetupDiDestroyDeviceInfoList(set);}
        return Task.FromResult<IReadOnlyList<DeviceDescriptor>>(result);
    }
}
