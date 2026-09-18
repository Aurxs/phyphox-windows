global using Microsoft.AspNetCore.Builder;
global using Microsoft.AspNetCore.Http;
using System.Xml.Linq;
using Phyphox.Server;
using System.IO.Compression;
static void Assert(bool condition,string message){if(!condition)throw new Exception(message);}
var uuid="00002a19-0000-1000-8000-00805f9b34fb";
var output=XElement.Parse($"<bluetooth><input char='{uuid}' conversion='uint16LittleEndian' offset='1' keep='false'>a</input><input char='{uuid}' conversion='uint8' offset='4' triggerId='press'>b</input></bluetooth>");
double[] Read(string name)=>name=="a"?[513]:[7];
var automatic=OutputCoordinator.BuildBleWrites(output,Read,new HashSet<string>());
Assert(automatic.Count==1&&automatic[0].Payload.SequenceEqual(new byte[]{0,1,2})&&!automatic[0].Triggered&&automatic[0].ConsumedBuffers.SequenceEqual(new[]{"a"}),"BLE offsets and default trigger exclusion");
var triggered=OutputCoordinator.BuildBleWrites(output,Read,new HashSet<string>{"press"});Assert(triggered[0].Payload.SequenceEqual(new byte[]{0,1,2,0,7})&&triggered[0].Triggered,"triggered command not marked coalescible");
var duplicate=XElement.Parse($"<bluetooth><input char='{uuid}' conversion='uint8' keep='false'>a</input><input char='{uuid}' conversion='uint8' offset='1'>a</input></bluetooth>");
Assert(OutputCoordinator.BuildBleWrites(duplicate,Read,new HashSet<string>())[0].Payload.Length==1,"keep=false consumption order");
var usb=new DeviceOutputBinding("usb","test-protocol-only",Usb:new("documented-protocol-fixture","01 00 00 FF",[new("a","uint16LittleEndian",1)],1),TriggerId:"send");
Assert(OutputCoordinator.BuildUsbWrites(usb,Read,new HashSet<string>()).Count==0,"USB doesn't send without explicit trigger");
Assert(OutputCoordinator.BuildUsbWrites(usb,Read,new HashSet<string>{"send"})[0].Payload.SequenceEqual(new byte[]{1,1,2,255}),"USB explicit packet template");
try{OutputCoordinator.BuildUsbWrites(usb with {Usb=usb.Usb! with {Fields=[new("a","uint32LittleEndian",2)]}},Read,new HashSet<string>{"send"});throw new Exception("oversize not rejected");}catch(ArgumentException){}
var raw="123456789"u8.ToArray();var partial=raw.Concat(new byte[]{0x50,0x4b,7,8,0x26,0x39,0xf4,0xcb,9,0,0,0,9,0,0,0}).ToArray();
var normalized=DeviceTransferEndpoints.NormalizePackage(partial);using(var archive=new ZipArchive(new MemoryStream(normalized.Bytes))){using var reader=new StreamReader(archive.Entries.Single().Open());Assert(reader.ReadToEnd()=="123456789"&&archive.Entries[0].FullName=="a.phyphox","official partial ZIP normalized");}
partial[9+4]=0;try{DeviceTransferEndpoints.NormalizePackage(partial);throw new Exception("CRC not checked");}catch(InvalidDataException){}
Console.WriteLine("Device output serialization/trigger/consume/packet and BLE partial-ZIP tests passed. No device I/O executed.");
