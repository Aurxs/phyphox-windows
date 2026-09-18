// SPDX-License-Identifier: GPL-3.0-only
using System.Collections.Concurrent;
using System.Globalization;
using System.Threading.Channels;
using System.Xml.Linq;
using Phyphox.Core;
using Phyphox.Devices;
namespace Phyphox.Server;

public sealed record OutputValueMapping(string Buffer,string Conversion,int Offset=0,bool Keep=true);
public sealed record UsbOutputPacket(string ProtocolId,string TemplateHex,OutputValueMapping[] Fields,byte? HidReportId=null);
public sealed record DeviceOutputBinding(string Id,string ConnectionId,int? OutputIndex=null,UsbOutputPacket? Usb=null,string? TriggerId=null,bool SendOnEachAnalysis=false,bool WithoutResponse=false);
public sealed record DeviceWriteReceipt(string BindingId,string ConnectionId,Guid? Characteristic,int Bytes,bool Triggered,bool Succeeded,string? Error);
public sealed record PreparedDeviceWrite(Guid? Characteristic,byte[] Payload,bool Triggered,string[] ConsumedBuffers);

/// <summary>Explicit device bindings only. Snapshot under the experiment lock; execute I/O outside it. No retries or control replay.</summary>
public sealed class OutputCoordinator(DeviceManager devices) : IAsyncDisposable
{
    readonly object gate=new();
    readonly Dictionary<string,DeviceOutputBinding> bindings=[];
    readonly HashSet<string> triggers=[];
    readonly ConcurrentDictionary<string,(DeviceOutputBinding Binding,PreparedDeviceWrite Write)> pending=new();
    Channel<string>? queue;
    CancellationTokenSource? lifetime;
    Task? worker;
    public event Action<DeviceWriteReceipt>? WriteCompleted;
    public event Action<string>? Faulted;
    public IReadOnlyList<DeviceOutputBinding> Bindings {get{lock(gate)return bindings.Values.ToArray();}}
    public void ClearBindings(){lock(gate){if(lifetime!=null)throw new InvalidOperationException("Stop outputs before clearing bindings.");bindings.Clear();triggers.Clear();}}
    public void Bind(ExperimentDefinition definition,DeviceOutputBinding binding) {
        if(string.IsNullOrWhiteSpace(binding.Id)||binding.Id.Length>128)throw new ArgumentException("Binding ID required.");
        if(!devices.IsConnected(binding.ConnectionId))throw new InvalidOperationException("Explicit connected device required.");
        var profile=devices.Profile(binding.ConnectionId);
        if(profile.Kind==TransportKind.Ble) {
            if(binding.Usb!=null||binding.OutputIndex is not int i||i<0||i>=definition.Outputs.Count||definition.Outputs[i].Name.LocalName!="bluetooth")throw new ArgumentException("BLE binding must identify an original bluetooth output index.");
            var output=definition.Outputs[i];ValidateBle(output,definition.Containers.Select(x=>x.Name).ToHashSet());
            if(output.Attr("uuid") is {Length:>0} uuid && (!Guid.TryParse(uuid,out var service)||profile.Ble?.Service!=service))throw new ArgumentException("Selected BLE service does not match experiment UUID filter.");
        } else {
            if(binding.OutputIndex!=null||binding.Usb==null)throw new ArgumentException("USB requires an explicit protocol packet, not an invented .phyphox output type.");
            if(!binding.SendOnEachAnalysis&&string.IsNullOrEmpty(binding.TriggerId))throw new ArgumentException("USB output requires an explicit trigger or opt-in SendOnEachAnalysis.");
            ValidateUsb(binding.Usb,definition.Containers.Select(x=>x.Name).ToHashSet());
            var bytes=ParseTemplate(binding.Usb.TemplateHex);
            if(profile.Kind==TransportKind.Hid&&(binding.Usb.HidReportId==null||profile.Hid?.OutputReportLength!=bytes.Length||bytes[0]!=binding.Usb.HidReportId))throw new ArgumentException("HID packet requires exact output-report length and report ID.");
            if(profile.Kind==TransportKind.Serial&&binding.Usb.HidReportId!=null)throw new ArgumentException("HID report ID cannot be assigned to a serial protocol.");
        }
        lock(gate){if(lifetime!=null)throw new InvalidOperationException("Stop before modifying device output binding.");if(binding.OutputIndex.HasValue&&bindings.Values.Any(x=>x.Id!=binding.Id&&x.OutputIndex==binding.OutputIndex))throw new ArgumentException("An output already has an explicit device binding.");bindings[binding.Id]=binding;}
    }
    public IReadOnlyList<string> CapabilityIssues(ExperimentDefinition definition) {
        var issues=new List<string>();var current=Bindings;
        for(int i=0;i<definition.Outputs.Count;i++)if(definition.Outputs[i].Name.LocalName=="bluetooth") {
            if(!current.Any(b=>b.OutputIndex==i))issues.Add($"BLE 输出 {i} 尚未绑定明确的连接设备。");
            try{ValidateBle(definition.Outputs[i],definition.Containers.Select(x=>x.Name).ToHashSet());}catch(Exception ex)when(ex is ArgumentException or NotSupportedException or FormatException){issues.Add(ex.Message);}
        }
        foreach(var binding in current)if(!devices.IsConnected(binding.ConnectionId))issues.Add("输出连接不可用："+binding.ConnectionId);
        return issues;
    }
    public async Task StartAsync(ExperimentDefinition definition,CancellationToken cancellationToken=default) {
        if(lifetime!=null)throw new InvalidOperationException("Outputs already running.");
        var issues=CapabilityIssues(definition);if(issues.Count>0)throw new InvalidOperationException(string.Join("；",issues));
        var life=CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        lock(gate){lifetime=life;triggers.Clear();pending.Clear();queue=Channel.CreateBounded<string>(new BoundedChannelOptions(64){SingleReader=true,FullMode=BoundedChannelFullMode.Wait});}
        try {
            foreach(var binding in Bindings.Where(x=>x.OutputIndex.HasValue)) {
                var output=definition.Outputs[binding.OutputIndex!.Value];var caps=await devices.BleCapabilitiesAsync(binding.ConnectionId,life.Token);
                foreach(var item in output.Elements().Where(x=>x.Name.Namespace==output.Name.Namespace)) {
                    var uuid=Guid.Parse(item.Attr("char"));var cap=caps.FirstOrDefault(c=>c.Uuid==uuid)??throw new IOException("Requested output/config characteristic not found: "+uuid);
                    if(binding.WithoutResponse?!cap.WriteWithoutResponse:!cap.Write)throw new NotSupportedException("Selected characteristic does not support the requested write mode.");
                }
                foreach(var config in output.Children("config")) {
                    var bytes=OutputConversions.Config(config.Value,config.Attr("conversion"))??throw new FormatException("BLE configuration conversion failed.");
                    if(bytes.Length==0)throw new FormatException("Empty BLE configuration payload rejected.");
                    // Explicit experiment start executes each authored config once. Nothing is replayed after failure.
                    await devices.WriteCharacteristicAsync(binding.ConnectionId,Guid.Parse(config.Attr("char")),bytes,binding.WithoutResponse,life.Token);
                }
            }
            worker=Run(queue!.Reader,life.Token);
        }catch{await StopAsync();throw;}
    }
    public void RequestTrigger(string triggerId){if(string.IsNullOrWhiteSpace(triggerId))throw new ArgumentException("Trigger ID required.");lock(gate){if(lifetime==null)throw new InvalidOperationException("Device outputs are stopped.");triggers.Add(triggerId);}}
    /// <summary>Call after a successful analysis cycle under its state lock. Clears consumed buffers only after writes are accepted into the bounded queue.</summary>
    public void EnqueueAfterAnalysis(ExperimentDefinition definition,Func<string,double[]> readBuffer,Action<string> clearBuffer) {
        DeviceOutputBinding[] current;HashSet<string> requested;Channel<string>? target;
        lock(gate){if(lifetime==null||lifetime.IsCancellationRequested)return;current=bindings.Values.ToArray();requested=new(triggers);triggers.Clear();target=queue;}
        try {
            foreach(var binding in current) {
                var prepared=binding.OutputIndex is int index?BuildBleWrites(definition.Outputs[index],readBuffer,requested):BuildUsbWrites(binding,readBuffer,requested);
                foreach(var write in prepared) {
                    string key=write.Triggered?Guid.NewGuid().ToString("N"):binding.Id+":"+(write.Characteristic?.ToString()??"usb");
                    lock(gate) {
                        if(lifetime==null||lifetime.IsCancellationRequested||!ReferenceEquals(target,queue))return;
                        bool added=pending.TryAdd(key,(binding,write));if(!added)pending[key]=(binding,write);
                        if(added&&!target!.Writer.TryWrite(key)){pending.TryRemove(key,out _);throw new IOException("Device output queue full; output stopped instead of silently dropping control commands.");}
                    }
                    foreach(var buffer in write.ConsumedBuffers)clearBuffer(buffer);
                }
            }
        }catch(Exception ex){lifetime?.Cancel();Faulted?.Invoke(ex.Message);}
    }
    async Task Run(ChannelReader<string> reader,CancellationToken token) {
        try {
            await foreach(var key in reader.ReadAllAsync(token)) {
                (DeviceOutputBinding Binding,PreparedDeviceWrite Write) item;lock(gate){if(!pending.TryRemove(key,out item))continue;}var write=item.Write;var binding=item.Binding;
                try {
                    if(write.Characteristic is Guid uuid)await devices.WriteCharacteristicAsync(binding.ConnectionId,uuid,write.Payload,binding.WithoutResponse,token);
                    else await devices.WriteBytesAsync(binding.ConnectionId,write.Payload,token);
                    WriteCompleted?.Invoke(new(binding.Id,binding.ConnectionId,write.Characteristic,write.Payload.Length,write.Triggered,true,null));
                }catch(OperationCanceledException)when(token.IsCancellationRequested){throw;}
                catch(Exception ex){WriteCompleted?.Invoke(new(binding.Id,binding.ConnectionId,write.Characteristic,write.Payload.Length,write.Triggered,false,ex.Message));lifetime?.Cancel();Faulted?.Invoke("设备输出失败；命令不会自动重试："+ex.Message);break;}
            }
        }catch(OperationCanceledException)when(token.IsCancellationRequested){}
        finally{pending.Clear();}
    }
    public async Task StopAsync() {
        CancellationTokenSource? life;Task? old;lock(gate){life=lifetime;lifetime=null;old=worker;worker=null;queue?.Writer.TryComplete();queue=null;triggers.Clear();}
        if(life==null)return;await life.CancelAsync();try{if(old!=null)await old;}finally{pending.Clear();life.Dispose();}
    }
    public async ValueTask DisposeAsync()=>await StopAsync();
    public static IReadOnlyList<PreparedDeviceWrite> BuildBleWrites(XElement output,Func<string,double[]> read,IReadOnlySet<string> requested) {
        var writes=new List<PreparedDeviceWrite>();var alreadyConsumed=new HashSet<string>();
        foreach(var group in output.Children("input").GroupBy(x=>Guid.Parse(x.Attr("char")))) {
            byte[] payload=[];var consumed=new List<string>();bool triggered=false;
            foreach(var field in group) {
                string trigger=field.Attr("triggerId");if(trigger.Length>0&&!requested.Contains(trigger))continue;
                var name=field.Value.Trim();var data=alreadyConsumed.Contains(name)?[]:read(name);if(data.Length==0)continue;
                var bytes=OutputConversions.Convert(data,field.Attr("conversion"));int offset=int.Parse(field.Attr("offset","0"),CultureInfo.InvariantCulture);
                if(offset<0||offset+(long)bytes.Length>65536)throw new ArgumentException("BLE field exceeds bounded packet length.");
                if(payload.Length<offset+bytes.Length)Array.Resize(ref payload,offset+bytes.Length);bytes.CopyTo(payload,offset);triggered|=trigger.Length>0;
                if(!field.Flag("keep",true)){consumed.Add(name);alreadyConsumed.Add(name);}
            }
            if(payload.Length>0)writes.Add(new(group.Key,payload,triggered,consumed.ToArray()));
        }
        return writes;
    }
    public static IReadOnlyList<PreparedDeviceWrite> BuildUsbWrites(DeviceOutputBinding binding,Func<string,double[]> read,IReadOnlySet<string> requested) {
        bool triggered=!string.IsNullOrEmpty(binding.TriggerId)&&requested.Contains(binding.TriggerId);
        if(!binding.SendOnEachAnalysis&&!triggered)return [];
        var packet=binding.Usb??throw new ArgumentException("USB protocol packet missing.");var payload=ParseTemplate(packet.TemplateHex);var consumed=new List<string>();
        foreach(var field in packet.Fields){var data=consumed.Contains(field.Buffer)?[]:read(field.Buffer);if(data.Length==0)return [];var bytes=OutputConversions.Convert(data,field.Conversion);if(field.Offset<0||field.Offset+(long)bytes.Length>payload.Length)throw new ArgumentException("USB field exceeds explicit packet template.");bytes.CopyTo(payload,field.Offset);if(!field.Keep)consumed.Add(field.Buffer);}
        if(packet.HidReportId is byte report&&payload[0]!=report)throw new ArgumentException("Output field overwrote HID report ID.");
        return [new(null,payload,triggered,consumed.ToArray())];
    }
    static byte[] ParseTemplate(string hex){var clean=string.Concat(hex.Where(x=>!char.IsWhiteSpace(x)));if(clean.Length is <2 or >131072||clean.Length%2!=0)throw new ArgumentException("Explicit USB packet template must contain 1..65536 bytes.");return Convert.FromHexString(clean);}
    static void ValidateUsb(UsbOutputPacket packet,HashSet<string> containers) {
        if(string.IsNullOrWhiteSpace(packet.ProtocolId))throw new ArgumentException("A named, documented USB protocol is required; no universal USB protocol is assumed.");
        var bytes=ParseTemplate(packet.TemplateHex);if(packet.Fields.Length>64)throw new ArgumentException("Too many USB fields.");
        foreach(var field in packet.Fields){if(!containers.Contains(field.Buffer)||field.Offset<0||field.Offset>=bytes.Length)throw new ArgumentException("Invalid USB output buffer/offset.");_=OutputConversions.Convert([0],field.Conversion);}
    }
    static void ValidateBle(XElement output,HashSet<string> containers) {
        if(int.Parse(output.Attr("mtu","0"),CultureInfo.InvariantCulture)>0)throw new NotSupportedException("BLE 输出指定 MTU，但当前尚未核对实际协商 MTU，不能承诺此实验设备兼容。");
        foreach(var child in output.Elements().Where(x=>x.Name.Namespace==output.Name.Namespace)) {
            if(child.Name.LocalName is not("input" or "config")||!Guid.TryParse(child.Attr("char"),out _))throw new ArgumentException("Invalid BLE output/config characteristic mapping.");
            if(child.Name.LocalName=="config") {var bytes=OutputConversions.Config(child.Value,child.Attr("conversion"));if(bytes is not {Length:>0 and <=65536})throw new ArgumentException("Invalid BLE configuration bytes.");}
            else {if(!containers.Contains(child.Value.Trim()))throw new ArgumentException("Unknown BLE output buffer.");int offset=int.Parse(child.Attr("offset","0"),CultureInfo.InvariantCulture);if(offset is <0 or >32767)throw new ArgumentException("BLE output offset exceeds official positive short range.");_=OutputConversions.Convert([0],child.Attr("conversion"));}
        }
    }
}
