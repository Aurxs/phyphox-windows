using System.Buffers.Binary;
using System.Text.Json;
using System.Xml.Linq;
using Phyphox.Server;
static void Assert(bool value,string message){if(!value)throw new Exception(message);}
var root=Path.GetFullPath(args.FirstOrDefault() ?? "../official-reference/phyphox-docs");
var xml=XDocument.Load(Path.Combine(root,"corpus/valid/ble-libraries/micropython-randomNumbers.phyphox"));
var names=xml.Root!.Element("data-containers")!.Elements().Select(x=>x.Value.Trim()).ToHashSet();
var plan=BleInputCoordinator.ParsePlan(xml.Root.Element("input")!.Element("bluetooth")!,names);
Assert(plan.Mode=="notification"&&!plan.SubscribeOnStart&&plan.Configs.Single().Bytes.SequenceEqual(new byte[]{0,0,0}),"official fixture modes/config");
using var baseline=JsonDocument.Parse(File.ReadAllText(Path.Combine(root,"fixtures/ble/baselines/micropython-randomNumbers.json")));
var times=baseline.RootElement.GetProperty("buffers").GetProperty("CH0").EnumerateArray().Select(x=>x.GetDouble()).ToArray();
var values=baseline.RootElement.GetProperty("buffers").GetProperty("CH1").EnumerateArray().Select(x=>x.GetDouble()).ToArray();
for(int i=0;i<values.Length;i++) {
 // Reconstruct numeric packets from published buffer values; these are NOT captured raw BLE packets or device validation.
 var bytes=new byte[4];BinaryPrimitives.WriteInt32LittleEndian(bytes,BitConverter.SingleToInt32Bits((float)values[i]));
 var result=BleInputCoordinator.Decode(plan.Fields,[(plan.Fields[0].Characteristic,bytes)],times[i]);
 Assert(result["CH1"].Single()==values[i]&&result["CH0"].Single()==times[i],"official baseline numeric conversion/time");
 Assert(result["CH2"].Length==0&&result["CH5"].Length==0,"short packet does not fabricate trailing fields");
}
const string uuid="cddf1002-30f7-4671-8b43-5e40ba53514a";
var repeat=XElement.Parse($"<bluetooth mode='poll' rate='5' subscribeOnStart='true'><output char='{uuid}' conversion='int24BigEndian' offset='1' repeating='3'>x</output><output char='{uuid}' extra='time'>t</output></bluetooth>");
var repeated=BleInputCoordinator.ParsePlan(repeat,new HashSet<string>{"x","t"});
var decoded=BleInputCoordinator.Decode(repeated.Fields,[(Guid.Parse(uuid),new byte[]{0,0xff,0xff,0xff,0,0,2})],4);
Assert(repeated.Rate==5&&decoded["x"].SequenceEqual(new double[]{-1,2})&&decoded["t"].Single()==4,"poll parse/repeated int24");
var unsupported=new XElement(repeat);unsupported.SetAttributeValue("mtu",512);
try{BleInputCoordinator.ParsePlan(unsupported,new HashSet<string>{"x","t"});throw new Exception("MTU silently accepted");}catch(NotSupportedException){}
var duplicate=new XElement(repeat);duplicate.Add(new XElement(duplicate.Elements().Last()));
try{BleInputCoordinator.ParsePlan(duplicate,new HashSet<string>{"x","t"});throw new Exception("duplicate time accepted");}catch(ArgumentException){}
Console.WriteLine("BLE declaration/official numerical fixture checks passed. No BLE device connected; reconstructed packets are not hardware acceptance.");
var replacement=Phyphox.Core.ExperimentParser.Parse("<phyphox version='1.20'><title>explicit replacement</title><category>test</category><data-containers><container>x</container></data-containers><input><bluetooth mode='notification' mtu='512'><output char='cddf1002-30f7-4671-8b43-5e40ba53514a' conversion='float32LittleEndian'>x</output></bluetooth></input><views><view label='v'><value label='x'><input>x</input></value></view></views></phyphox>");
await using(var manager=new DeviceManager())await using(var coordinator=new BleInputCoordinator(manager)) {
 Assert(coordinator.CapabilityIssues(replacement).Count>0,"unbound native BLE remains blocked");
 var external=new HashSet<int>{0};Assert(coordinator.CapabilityIssues(replacement,external).Count==0,"explicit generic replacement bypasses native constraints");
 await coordinator.StartAsync(replacement,CancellationToken.None,external);await coordinator.StopAsync();
}
Console.WriteLine("Explicit replacement API compatibility passed; no device opened.");
