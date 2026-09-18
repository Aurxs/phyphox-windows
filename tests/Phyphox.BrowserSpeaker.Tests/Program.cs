using System.Text.Json;
using Phyphox.Server;
static JsonElement Json(object o)=>JsonSerializer.SerializeToElement(o);
static void Check(bool c,string why){if(!c)throw new Exception(why);}
static void Reject(Action a){try{a();}catch(ArgumentException){return;}catch(InvalidOperationException){return;}catch(NotSupportedException){return;}throw new Exception("Invalid speaker operation accepted");}
var root=Path.Combine(Path.GetTempPath(),"phyphox-speaker-test-"+Guid.NewGuid().ToString("N"));
var assets=Path.Combine(root,"assets");Directory.CreateDirectory(Path.Combine(assets,"samples"));
const string xml="<phyphox version='1.20'><title>Speaker</title><category>Tests</category><data-containers><container>frequency</container></data-containers><output><audio rate='48000' loop='true' normalize='false'><tone><input type='value' parameter='frequency'>1000</input><input type='value' parameter='amplitude'>0.5</input></tone></audio></output></phyphox>";
File.WriteAllText(Path.Combine(assets,"samples","speaker.phyphox"),xml);
var library=new LibraryService(assets,root);using var session=new SessionService(library,root,new DeviceManager());
try{
 var id=library.List().Single().Id;session.Load(id);
 var configured=Json(session.ConfigureBrowserSpeaker(new(id,0)));string token=configured.GetProperty("streamId").GetString()!;
 Check(configured.GetProperty("sampleRate").GetInt32()==48000,"source rate");
 Check(Json(session.PullBrowserSpeaker(new(id,token,0))).GetProperty("samples").GetArrayLength()==0,"must remain silent before start");
 Reject(()=>session.PullBrowserSpeaker(new(id,token,0)));
 await session.CommandAsync(new("start"),CancellationToken.None);
 var pcm=Json(session.PullBrowserSpeaker(new(id,token,1))).GetProperty("samples").EnumerateArray().Select(x=>x.GetSingle()).ToArray();
 Check(pcm.Length==9600,"100ms stereo packet");Check(pcm.Max()>.49&&pcm.Max()<=.5,"real backend tone amplitude");
 for(int i=0;i<40;i++)Check(pcm[2*i]==pcm[2*i+1],"centered stereo");
 await session.CommandAsync(new("pause"),CancellationToken.None);
 Check(Json(session.PullBrowserSpeaker(new(id,token,2))).GetProperty("samples").GetArrayLength()==0,"pause silence");
 await session.CommandAsync(new("start"),CancellationToken.None);
 Check(Json(session.PullBrowserSpeaker(new(id,token,3))).GetProperty("samples").GetArrayLength()>0,"resume same stream");
 await session.CommandAsync(new("stop"),CancellationToken.None);
 Reject(()=>session.PullBrowserSpeaker(new(id,token,4)));
 Console.WriteLine("PASS: browser speaker service PCM amplitude/stereo, ordered sequence, ready/pause silence, resume and stop invalidation. No speaker device opened.");
}finally{await session.StopAsync(CancellationToken.None);Directory.Delete(root,true);}
