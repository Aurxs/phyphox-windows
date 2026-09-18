using Phyphox.Devices;
static void Assert(bool value,string message) {if(!value) throw new Exception(message);}
Assert(ByteConversions.Read([0xff,0xff,0xff],"int24LittleEndian")==-1,"24 bit sign extension");
Assert(ByteConversions.Read([0x80,0,0],"int24BigEndian")==-8388608,"24 bit sign minimum");
Assert(ByteConversions.Read([255,255,255,255],"uint32BigEndian")==4294967295d,"unsigned 32");
Assert(ByteConversions.Read([0,0,128,63],"float32LittleEndian")==1,"IEEE float");
Assert(ByteConversions.Convert([0,1,0,2,0,3,7],"uint16LittleEndian",1,2).SequenceEqual(new double[]{1,2,1795}),"offset and repeating");
Assert(ByteConversions.Convert([1,0,9],"uint16LittleEndian",0,2).SequenceEqual(new double[]{1}),"partial tail ignored like Android");
Assert(ByteConversions.FormattedString("x=2|v=3"u8,"|","v=").Single()==3,"literal separator/label");
var framer=new PacketFramer(delimiter:[13,10]);
Assert(framer.Push([1,2,13]).Count==0,"partial delimiter");
var frames=framer.Push([10,3,13,10]); Assert(frames.Count==2 && frames[0].SequenceEqual(new byte[]{1,2}) && frames[1].SequenceEqual(new byte[]{3}),"split and coalesced frames");
if(!OperatingSystem.IsWindows()) {
    await using var device=new WindowsSerialTransport();
    Assert(!device.IsAvailable,"platform capability");
    try {await device.ScanAsync();throw new Exception("must reject fake devices");} catch(PlatformNotSupportedException) {}
}
Console.WriteLine("Device codec/framing checks passed. No hardware tests were run.");
var transfer=new BleExperimentTransfer();
transfer.Receive([112,104,121,112,104,111,120,0,0,0,9,0xcb,0xf4,0x39,0x26]);
transfer.Receive("1234"u8); transfer.Receive("56789"u8);
Assert(transfer.GetVerifiedPayload().SequenceEqual("123456789"u8.ToArray()),"official transfer CRC32 fixture");
var corrupt=new BleExperimentTransfer(); corrupt.Receive([112,104,121,112,104,111,120,0,0,0,1,0,0,0,0]);corrupt.Receive([1]);
try {corrupt.GetVerifiedPayload();throw new Exception("corrupt file accepted");} catch(InvalidDataException) {}
Assert(OutputConversions.Encode(4294967295,"uint32BigEndian").SequenceEqual(new byte[]{255,255,255,255}),"uint32 output Java long semantics");
Assert(OutputConversions.Encode(double.PositiveInfinity,"int16LittleEndian").SequenceEqual(new byte[]{255,255}),"Java saturated cast before truncation");
Assert(OutputConversions.Encode(double.NaN,"float32BigEndian").SequenceEqual(new byte[]{0x7f,0xc0,0,0}),"Java canonical NaN");
Assert(OutputConversions.Config("AB C","hexadecimal")!.SequenceEqual(new byte[]{0xab}),"odd hex tail ignored");
Assert(OutputConversions.Config("zz","hexadecimal")!.Length==0,"invalid hex empty");
Assert(OutputConversions.Config("255","uint8")!.Single()==255 && OutputConversions.Config("1.5","uint8")==null,"int8 config uses integer grammar");
await using(var scripted=new TransferFixture()) {
    var payload=await BleExperimentDownloader.DownloadAsync(scripted,new DeviceDescriptor("fixture","protocol test only",TransportKind.Ble));
    Assert(payload.SequenceEqual("123456789"u8.ToArray()),"download payload");
    Assert(scripted.Controls.SequenceEqual(new byte[]{1,0}) && scripted.Status==DeviceStatus.Disconnected,"download control start/finish and disconnect");
}
Console.WriteLine("Output/config and BLE download protocol fixture checks passed; fixture is not hardware validation.");

sealed class TransferFixture : IBleDeviceTransport
{
    public List<byte> Controls {get;}=[];
    public TransportKind Kind=>TransportKind.Ble;
    public bool IsAvailable=>true;
    public DeviceStatus Status {get;private set;}
    public Task<IReadOnlyList<DeviceDescriptor>> ScanAsync(CancellationToken cancellationToken=default)=>throw new NotSupportedException();
    public Task ConnectAsync(DeviceDescriptor device,DeviceProfile profile,CancellationToken cancellationToken=default){Status=DeviceStatus.Connected;return Task.CompletedTask;}
    public Task DisconnectAsync(CancellationToken cancellationToken=default){Status=DeviceStatus.Disconnected;return Task.CompletedTask;}
    public Task WriteAsync(ReadOnlyMemory<byte> data,CancellationToken cancellationToken=default)=>throw new NotSupportedException();
    public IAsyncEnumerable<DeviceFrame> ReadAsync(CancellationToken cancellationToken=default)=>throw new NotSupportedException();
    public Task<IReadOnlyList<BleCharacteristicInfo>> GetCharacteristicsAsync(CancellationToken cancellationToken=default)=>Task.FromResult<IReadOnlyList<BleCharacteristicInfo>>([new(BleExperimentDownloader.Experiment,true,true,false,false,false),new(BleExperimentDownloader.Control,false,false,false,true,false)]);
    public Task SubscribeAsync(Guid characteristic,bool indicate=false,CancellationToken cancellationToken=default)=>Task.CompletedTask;
    public Task UnsubscribeAsync(Guid characteristic,CancellationToken cancellationToken=default)=>Task.CompletedTask;
    public async IAsyncEnumerable<BleValue> ReadValuesAsync([System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken=default) {
        await Task.Yield();cancellationToken.ThrowIfCancellationRequested();
        yield return new(BleExperimentDownloader.Experiment,new DeviceFrame([112,104,121,112,104,111,120,0,0,0,9,0xcb,0xf4,0x39,0x26],DateTimeOffset.UtcNow,0));
        yield return new(BleExperimentDownloader.Experiment,new DeviceFrame("123456789"u8.ToArray(),DateTimeOffset.UtcNow,0));
    }
    public Task WriteCharacteristicAsync(Guid characteristic,ReadOnlyMemory<byte> data,bool withoutResponse=false,CancellationToken cancellationToken=default){Controls.Add(data.Span[0]);return Task.CompletedTask;}
    public Task<DeviceFrame> ReadCharacteristicAsync(Guid characteristic,CancellationToken cancellationToken=default)=>throw new NotSupportedException();
    public ValueTask DisposeAsync()=>ValueTask.CompletedTask;
}
