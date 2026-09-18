// SPDX-License-Identifier: GPL-3.0-only
using System.Diagnostics;
using System.Globalization;
using System.Xml.Linq;
using Phyphox.Core;
using Phyphox.Devices;
namespace Phyphox.Server;

public sealed record BleInputBinding(string ConnectionId,int InputIndex);
public sealed record BleInputField(Guid Characteristic,string Buffer,string? Conversion,bool ExtraTime,int Offset=0,int Repeating=0,int Length=0,string? DecimalPoint=null,string? Separator=null,string? Label=null,int Index=0);
public sealed record BleInputConfig(Guid Characteristic,byte[] Bytes);
public sealed record BleInputPlan(string Mode,double Rate,bool SubscribeOnStart,BleInputField[] Fields,BleInputConfig[] Configs);

/// <summary>Official declarative BLE input. Explicit bindings, no simulated values or guessed USB replacement.</summary>
public sealed class BleInputCoordinator : IAsyncDisposable
{
    readonly DeviceManager devices;
    readonly object gate=new();
    readonly SemaphoreSlim lifecycleGate=new(1,1);
    readonly Dictionary<int,ActiveBinding> bindings=[];
    readonly Queue<PendingBatch> pending=[];
    CancellationTokenSource? lifetime;
    Func<long,double>? timeMapper;
    public event Action<string>? Faulted;
    public IReadOnlyList<BleInputBinding> Bindings {get{lock(gate)return bindings.Values.Select(x=>x.Binding).ToArray();}}
    public BleInputCoordinator(DeviceManager devices){this.devices=devices;devices.BleFrameReceived+=Receive;devices.ConnectionFaulted+=DeviceFault;}
    public void SetTimeMapper(Func<long,double> mapper){lock(gate)timeMapper=mapper;}
    public async Task BindAsync(ExperimentDefinition definition,BleInputBinding binding,CancellationToken ct=default){await lifecycleGate.WaitAsync(ct);try{await BindCoreAsync(definition,binding,ct);}finally{lifecycleGate.Release();}}
    async Task BindCoreAsync(ExperimentDefinition definition,BleInputBinding binding,CancellationToken ct) {
        lock(gate){if(lifetime!=null)throw new InvalidOperationException("Stop before modifying BLE input bindings.");if(bindings.ContainsKey(binding.InputIndex))throw new InvalidOperationException("Clear the old input binding before replacing it.");}
        if(binding.InputIndex<0||binding.InputIndex>=definition.Inputs.Count||definition.Inputs[binding.InputIndex].Name.LocalName!="bluetooth")throw new ArgumentException("Explicit original BLE input index required.");
        if(!devices.IsConnected(binding.ConnectionId))throw new InvalidOperationException("Select and connect a real BLE device first.");
        var profile=devices.Profile(binding.ConnectionId);if(profile.Kind!=TransportKind.Ble||profile.Ble is null)throw new ArgumentException("BLE GATT connection required.");
        if(profile.Ble.Subscribe)throw new ArgumentException("Reconnect with profile Subscribe=false so the XML controls subscription lifetime.");
        var xml=definition.Inputs[binding.InputIndex];var plan=ParsePlan(xml,definition.Containers.Select(x=>x.Name).ToHashSet());
        if(plan.Fields.Any(x=>x.ExtraTime)&&timeMapper==null)throw new InvalidOperationException("extra=time requires experiment clock mapping.");
        if(xml.Attr("uuid") is {Length:>0} uuid&&Guid.Parse(uuid)!=profile.Ble.Service)throw new ArgumentException("Selected service does not match original experiment UUID filter.");
        var descriptor=devices.Descriptor(binding.ConnectionId);
        if(xml.Attr("name") is {Length:>0} name&&!descriptor.Name.Contains(name,StringComparison.Ordinal))throw new ArgumentException("Selected device name does not match experiment filter.");
        if(xml.Attr("address") is {Length:>0} address && !descriptor.Id.Equals("ble-address:"+address.Replace(":","",StringComparison.Ordinal).Replace("-","",StringComparison.Ordinal),StringComparison.OrdinalIgnoreCase))throw new ArgumentException("Selected device address cannot be confirmed against experiment address filter.");
        var caps=await devices.BleCapabilitiesAsync(binding.ConnectionId,ct);var subscriptionModes=new Dictionary<Guid,bool>();
        foreach(var group in plan.Fields.GroupBy(x=>x.Characteristic)) {
            var cap=caps.FirstOrDefault(x=>x.Uuid==group.Key)??throw new NotSupportedException("Input characteristic not present in explicitly selected service: "+group.Key);
            if(plan.Mode=="poll"){if(!cap.Read)throw new NotSupportedException("Characteristic does not support polling reads.");}
            else {if(!cap.Notify&&!cap.Indicate)throw new NotSupportedException("Characteristic has no supported notify/indicate property.");subscriptionModes[group.Key]=!cap.Notify&&cap.Indicate;}
        }
        foreach(var config in plan.Configs)if(!caps.Any(x=>x.Uuid==config.Characteristic&&x.Write))throw new NotSupportedException("Configuration characteristic needs write-with-response support.");
        var active=new ActiveBinding(binding,plan,subscriptionModes);
        try {
            // Authored config runs exactly once for this explicit binding; reconnect/Start never replay it.
            foreach(var config in plan.Configs)await devices.WriteCharacteristicAsync(binding.ConnectionId,config.Characteristic,config.Bytes,false,ct);
            if(plan.Mode!="poll"&&!plan.SubscribeOnStart)await Subscribe(active,ct);
            lock(gate){if(lifetime!=null||bindings.ContainsKey(binding.InputIndex))throw new InvalidOperationException("BLE binding lifecycle changed while preparing device.");bindings.Add(binding.InputIndex,active);}
        }catch{await Unsubscribe(active);throw;}
    }
    public IReadOnlyList<string> CapabilityIssues(ExperimentDefinition definition,IReadOnlySet<int>? externallyBoundInputs=null) {
        var issues=new List<string>();var existing=Bindings;
        for(int i=0;i<definition.Inputs.Count;i++)if(definition.Inputs[i].Name.LocalName=="bluetooth" && externallyBoundInputs?.Contains(i)!=true) {
            try {var plan=ParsePlan(definition.Inputs[i],definition.Containers.Select(x=>x.Name).ToHashSet());if(plan.Fields.Any(x=>x.ExtraTime)&&timeMapper==null)issues.Add("BLE extra=time 尚未绑定实验时钟。");}
            catch(Exception ex)when(ex is ArgumentException or FormatException or NotSupportedException){issues.Add(ex.Message);}
            if(!existing.Any(b=>b.InputIndex==i))issues.Add($"BLE 输入 {i} 尚未绑定明确的连接设备。");
        }
        foreach(var binding in existing)if(externallyBoundInputs?.Contains(binding.InputIndex)!=true&&!devices.IsConnected(binding.ConnectionId))issues.Add("BLE 输入连接不可用："+binding.ConnectionId);
        return issues;
    }
    public async Task StartAsync(ExperimentDefinition definition,CancellationToken ct=default,IReadOnlySet<int>? externallyBoundInputs=null){await lifecycleGate.WaitAsync(ct);try{await StartCoreAsync(definition,ct,externallyBoundInputs);}finally{lifecycleGate.Release();}}
    async Task StartCoreAsync(ExperimentDefinition definition,CancellationToken ct,IReadOnlySet<int>? externallyBoundInputs) {
        var issues=CapabilityIssues(definition,externallyBoundInputs);if(issues.Count>0)throw new InvalidOperationException(string.Join("；",issues));
        ActiveBinding[] current;CancellationTokenSource life;
        lock(gate){if(lifetime!=null)throw new InvalidOperationException("BLE input already started.");lifetime=life=CancellationTokenSource.CreateLinkedTokenSource(ct);pending.Clear();current=bindings.Values.Where(x=>externallyBoundInputs?.Contains(x.Binding.InputIndex)!=true).ToArray();}
        try {
            foreach(var active in current) {
                if(active.Plan.Mode!="poll"&&active.Plan.SubscribeOnStart)await Subscribe(active,life.Token);
                if(active.Plan.Mode=="poll")active.PollTask=Poll(active,life.Token);
            }
        }catch{await StopCoreAsync();throw;}
    }
    async Task Subscribe(ActiveBinding active,CancellationToken ct) {
        foreach(var (uuid,indicate) in active.SubscriptionModes){if(active.Subscribed.Contains(uuid))continue;await devices.SubscribeBleAsync(active.Binding.ConnectionId,uuid,indicate,ct);active.Subscribed.Add(uuid);}
    }
    async Task Unsubscribe(ActiveBinding active) {
        foreach(var uuid in active.Subscribed.ToArray()) {
            try{await devices.UnsubscribeBleAsync(active.Binding.ConnectionId,uuid,CancellationToken.None);}
            catch(Exception ex){Faulted?.Invoke("BLE unsubscribe failed: "+ex.Message);}
            finally{active.Subscribed.Remove(uuid);}
        }
    }
    void Receive(string connectionId,BleValue value) {
        ActiveBinding[] current;
        lock(gate){if(lifetime==null||lifetime.IsCancellationRequested)return;current=bindings.Values.Where(x=>x.Binding.ConnectionId==connectionId&&x.Plan.Mode!="poll"&&x.Plan.Fields.Any(f=>f.Characteristic==value.Characteristic)).ToArray();}
        foreach(var active in current) {
            var fields=active.Plan.Fields.Where(x=>x.Characteristic==value.Characteristic).ToArray();
            QueueBatch(new(fields,[(value.Characteristic,value.Frame.Data)],value.Frame.MonotonicTicks));
        }
    }
    async Task Poll(ActiveBinding active,CancellationToken ct) {
        try {
            var period=TimeSpan.FromMilliseconds(Math.Max(1,active.Plan.Rate<=0?1:Math.Floor(1000/active.Plan.Rate)));
            while(!ct.IsCancellationRequested) {
                long began=Stopwatch.GetTimestamp();var values=new List<(Guid,byte[])>();
                foreach(var uuid in active.Plan.Fields.Select(x=>x.Characteristic).Distinct()){var frame=await devices.ReadBleCharacteristicAsync(active.Binding.ConnectionId,uuid,ct);values.Add((uuid,frame.Data));}
                QueueBatch(new(active.Plan.Fields,values.ToArray(),Stopwatch.GetTimestamp()));
                var delay=period-Stopwatch.GetElapsedTime(began);if(delay>TimeSpan.Zero)await Task.Delay(delay,ct);
            }
        }catch(OperationCanceledException)when(ct.IsCancellationRequested){}
        catch(Exception ex){ReportFault("BLE poll failed: "+ex.Message);}
    }
    void QueueBatch(PendingBatch batch) {
        bool overflow=false;lock(gate){if(lifetime==null||lifetime.IsCancellationRequested)return;if(pending.Count>=256)overflow=true;else pending.Enqueue(batch);}
        if(overflow)ReportFault("BLE input queue overflow; stopped rather than fabricating or silently dropping samples.");
    }
    void DeviceFault(string connectionId,string message){lock(gate){if(lifetime==null||!bindings.Values.Any(x=>x.Binding.ConnectionId==connectionId))return;}ReportFault("BLE input connection failed: "+message);}
    void ReportFault(string message){CancellationTokenSource? life;lock(gate)life=lifetime;try{life?.Cancel();}catch(ObjectDisposedException){}Faulted?.Invoke(message);}
    /// <summary>Call before analysis under the session state lock; the callback appends all fields as one transaction.</summary>
    public void Flush(Action<IReadOnlyDictionary<string,double[]>> ingest) {
        PendingBatch[] batches;Func<long,double>? clock;lock(gate){batches=pending.ToArray();pending.Clear();clock=timeMapper;}
        foreach(var batch in batches){var values=Decode(batch.Fields,batch.Values,clock?.Invoke(batch.Timestamp)??double.NaN);if(values.Count>0)ingest(values);}
    }
    public async Task StopAsync(){await lifecycleGate.WaitAsync();try{await StopCoreAsync();}finally{lifecycleGate.Release();}}
    async Task StopCoreAsync() {
        ActiveBinding[] current;CancellationTokenSource? life;
        lock(gate){life=lifetime;lifetime=null;pending.Clear();current=bindings.Values.ToArray();}
        if(life==null)return;await life.CancelAsync();
        try{await Task.WhenAll(current.Select(x=>x.PollTask).OfType<Task>());foreach(var active in current){active.PollTask=null;if(active.Plan.SubscribeOnStart)await Unsubscribe(active);}}
        finally{life.Dispose();}
    }
    public async Task ClearBindingsAsync(){await lifecycleGate.WaitAsync();try{await StopCoreAsync();ActiveBinding[] current;lock(gate){current=bindings.Values.ToArray();bindings.Clear();}foreach(var active in current)await Unsubscribe(active);}finally{lifecycleGate.Release();}}
    public async ValueTask DisposeAsync(){await ClearBindingsAsync();devices.BleFrameReceived-=Receive;devices.ConnectionFaulted-=DeviceFault;}
    public static BleInputPlan ParsePlan(XElement input,IReadOnlySet<string> containers) {
        if(input.Name.LocalName!="bluetooth")throw new ArgumentException("Expected original bluetooth input.");
        Allowed(input,["id","name","address","uuid","autoConnect","rate","mode","subscribeOnStart","mtu"]);
        if(Int(input,"mtu")>0)throw new NotSupportedException("BLE input MTU requirement cannot yet be verified against negotiated Windows MTU.");
        string mode=input.Attr("mode","notification").ToLowerInvariant();if(mode is not("notification" or "indication" or "poll"))throw new NotSupportedException("Unknown BLE mode: "+mode);
        double rate=XmlUtil.Number(input.Attr("rate","0"));if(!double.IsFinite(rate)||mode=="poll"&&rate<0)throw new ArgumentException("Invalid BLE acquisition rate.");
        var fields=new List<BleInputField>();var configs=new List<BleInputConfig>();
        foreach(var item in input.Elements().Where(x=>x.Name.Namespace==input.Name.Namespace)) {
            if(!Guid.TryParse(item.Attr("char"),out var characteristic))throw new ArgumentException("BLE characteristic UUID required.");
            if(item.Name.LocalName=="config") {
                Allowed(item,["char","conversion"]);var bytes=OutputConversions.Config(item.Value,item.Attr("conversion"));if(bytes is not {Length:>0 and <=65536})throw new ArgumentException("Invalid authored BLE configuration payload.");configs.Add(new(characteristic,bytes));continue;
            }
            if(item.Name.LocalName!="output")throw new NotSupportedException("Unsupported BLE input element: "+item.Name.LocalName);
            Allowed(item,["char","conversion","extra","offset","length","repeating","decimalPoint","separator","label","index"]);
            string target=item.Value.Trim();if(!containers.Contains(target))throw new ArgumentException("BLE input references unknown container: "+target);
            string extra=item.Attr("extra").ToLowerInvariant();if(extra is not("" or "time"))throw new NotSupportedException("Unknown BLE extra: "+extra);
            bool time=extra=="time";if(time&&fields.Any(x=>x.Characteristic==characteristic&&x.ExtraTime))throw new ArgumentException("Only one extra=time per characteristic.");
            var conversion=time?null:item.Attr("conversion");
            if(!time){if(conversion!.Equals("formattedstring",StringComparison.OrdinalIgnoreCase)||conversion.Equals("string",StringComparison.OrdinalIgnoreCase)){}else _=ByteConversions.Read(new byte[8],conversion);}
            int offset=Int(item,"offset"),repeat=Int(item,"repeating"),length=Int(item,"length"),index=Int(item,"index");if(offset<0||repeat<0||length<0||index<0)throw new ArgumentException("BLE byte/index offsets cannot be negative.");
            fields.Add(new(characteristic,target,conversion,time,offset,repeat,length,item.Attribute("decimalPoint")?.Value,item.Attribute("separator")?.Value,item.Attribute("label")?.Value,index));
        }
        if(fields.Count==0)throw new ArgumentException("BLE input needs at least one declared output.");
        return new(mode,rate,input.Flag("subscribeOnStart"),fields.ToArray(),configs.ToArray());
    }
    public static IReadOnlyDictionary<string,double[]> Decode(BleInputField[] fields,(Guid Characteristic,byte[] Bytes)[] packets,double time) {
        var result=new Dictionary<string,List<double>>();
        foreach(var field in fields) {
            double[] values;
            if(field.ExtraTime)values=[time];
            else {
                var packet=packets.FirstOrDefault(x=>x.Characteristic==field.Characteristic).Bytes;if(packet==null)continue;
                try{values=field.Conversion!.Equals("formattedstring",StringComparison.OrdinalIgnoreCase)?ByteConversions.FormattedString(packet,field.Separator,field.Label,field.Index).ToArray():ByteConversions.Convert(packet,field.Conversion,field.Offset,field.Repeating,field.Length,field.DecimalPoint).ToArray();}
                catch(Exception ex)when(ex is ArgumentException or FormatException or OverflowException){values=[];}
            }
            if(!result.TryGetValue(field.Buffer,out var list))result[field.Buffer]=list=[];list.AddRange(values);
        }
        return result.ToDictionary(x=>x.Key,x=>x.Value.ToArray());
    }
    static int Int(XElement element,string name)=>int.Parse(element.Attr(name,"0"),CultureInfo.InvariantCulture);
    static void Allowed(XElement element,string[] allowed){foreach(var a in element.Attributes().Where(x=>!x.IsNamespaceDeclaration&&x.Name.NamespaceName.Length==0))if(!allowed.Contains(a.Name.LocalName))throw new NotSupportedException("Unsupported BLE attribute: "+a.Name.LocalName);}
    sealed class ActiveBinding(BleInputBinding binding,BleInputPlan plan,Dictionary<Guid,bool> modes){public BleInputBinding Binding {get;}=binding;public BleInputPlan Plan {get;}=plan;public Dictionary<Guid,bool> SubscriptionModes {get;}=modes;public HashSet<Guid> Subscribed {get;}=[];public Task? PollTask;}
    sealed record PendingBatch(BleInputField[] Fields,(Guid Characteristic,byte[] Bytes)[] Values,long Timestamp);
}
