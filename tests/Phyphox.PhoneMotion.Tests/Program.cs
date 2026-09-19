using System.Text.Json;
using Phyphox.Server;
static JsonElement Json(object value)=>JsonSerializer.SerializeToElement(value);
static void Check(bool condition,string message){if(!condition)throw new Exception(message);}
static void Reject(Action action){try{action();}catch(ArgumentException){return;}catch(InvalidOperationException){return;}catch(NotSupportedException){return;}throw new Exception("Invalid motion operation accepted");}
var root=Path.Combine(Path.GetTempPath(),"phyphox-motion-test-"+Guid.NewGuid().ToString("N"));
var assets=Path.Combine(root,"assets");Directory.CreateDirectory(Path.Combine(assets,"samples"));
const string xml="<phyphox version='1.20'><title>Phone motion</title><category>Tests</category><data-containers><container size='0'>x</container><container size='0'>t</container><container size='0'>abs</container></data-containers><input><sensor type='accelerometer'><output component='x'>x</output><output component='t'>t</output><output component='abs'>abs</output></sensor></input></phyphox>";
File.WriteAllText(Path.Combine(assets,"samples","motion.phyphox"),xml);
File.WriteAllText(Path.Combine(assets,"samples","audio.phyphox"),"<phyphox version='1.20'><title>Phone audio</title><category>Tests</category><data-containers><container size='0'>audio</container></data-containers><input><audio rate='8000' append='true'><output>audio</output></audio></input></phyphox>");
File.WriteAllText(Path.Combine(assets,"samples","camera.phyphox"),"<phyphox version='1.20'><title>Phone camera</title><category>Tests</category><data-containers><container size='0'>light</container><container size='0'>time</container></data-containers><input><camera feature='photometric'><output component='luma'>light</output><output component='t'>time</output></camera></input></phyphox>");
var library=new LibraryService(assets,root);using var session=new SessionService(library,root,new DeviceManager());
try
{
    var id=library.List().Single(i=>i.Title=="Phone motion").Id;session.Load(id);
    var configuration=Json(session.ConfigurePhoneMotion(new(id,0,"accelerometer")));
    var capture=configuration.GetProperty("captureId").GetString()!;
    string Run()=>Json(session.Snapshot()).GetProperty("phoneRunId").GetString()!;
    var probeRun=Run();
    Check(!Json(session.Snapshot()).GetProperty("canStart").GetBoolean(),"must require actual probe before start");
    session.ReceivePhoneMotion(new(id,capture,probeRun,0,[new(10,3,4,0)]));
    Check(Json(session.Snapshot()).GetProperty("buffers").GetProperty("x").GetArrayLength()==0,"standby samples discarded");
    Reject(()=>session.ReceivePhoneMotion(new(id,capture,probeRun,1,[new(9,3,4,0)])));
    Reject(()=>session.ReceivePhoneMotion(new(id,capture,probeRun,1,[new(11,double.NaN,4,0)])));
    await session.CommandAsync(new("start"),CancellationToken.None);
    var run=Run();Check(run!=probeRun,"start rotates token");
    Reject(()=>session.ReceivePhoneMotion(new(id,capture,probeRun,1,[new(11,3,4,0)])));
    session.ReceivePhoneMotion(new(id,capture,run,0,[new(20,3,4,0),new(20.01,6,8,0)]));
    var buffers=Json(session.Snapshot()).GetProperty("buffers");
    Check(buffers.GetProperty("abs")[1].GetDouble()==10,"magnitude mapping");
    Check(Math.Abs(buffers.GetProperty("t")[1].GetDouble()-buffers.GetProperty("t")[0].GetDouble()-.01)<1e-8,"relative segment preserves intervals");

    await session.CommandAsync(new("pause"),CancellationToken.None);Check(Run()!=run,"pause revokes prior run");
    session.ReceivePhoneMotion(new(id,capture,Run(),0,[new(30,1,1,1)]));
    await session.CommandAsync(new("start"),CancellationToken.None);
    session.ReceivePhoneMotion(new(id,capture,Run(),0,[new(40,1,1,1)]));
    await session.StopPhoneBridgeAsync();Check(Json(session.Snapshot()).GetProperty("status").GetString()!="running","bridge stop halts phone-dependent session");
    Reject(()=>session.ReceivePhoneMotion(new(id,capture,Run(),1,[new(41,1,1,1)])));
    Check(Directory.GetFiles(Path.Combine(root,"recordings"),"*.jsonl").Any(path=>File.ReadAllText(path).Contains("relative-segment")),"record source timing quality");
    session.Load(id);
    configuration=Json(session.ConfigurePhoneMotion(new(id,0,"accelerometer")));capture=configuration.GetProperty("captureId").GetString()!;
    session.ReceivePhoneMotion(new(id,capture,Run(),0,[new(0,1,2,3)]));
    await session.CommandAsync(new("start"),CancellationToken.None);
    Reject(()=>session.ReceivePhoneMotion(new(id,capture,Run(),2,[new(1,1,2,3)])));
    Check(Json(session.Snapshot()).GetProperty("status").GetString()!="running","sequence gaps fail closed");
    await session.CommandAsync(new("stop"),CancellationToken.None);
    var audioId=library.List().Single(i=>i.Title=="Phone audio").Id;session.Load(audioId);
    var audio=Json(session.ConfigureBrowserMedia(new(audioId,0,"audio",8000,Source:"phone"))).GetProperty("captureId").GetString()!;
    Check(!Json(session.Snapshot()).GetProperty("canStart").GetBoolean(),"phone audio also requires actual data");
    Reject(()=>session.ConfigureBrowserMedia(new(audioId,0,"audio",8000,Source:"browser")));
    try{session.Bind(new("unused",[],TimeBuffer:"audio"));throw new Exception("media/time binding conflict allowed");}catch(InvalidOperationException ex){Check(ex.Message.Contains("浏览器来源"),"conflict must reject before device probing");}
    Reject(()=>session.ReceiveBrowserAudio(new(audioId,audio,0,8000,[0])));
    Reject(()=>session.ReceiveBrowserAudio(new(audioId,audio,0,8000,[double.NaN],Run())));
    Reject(()=>session.ReceiveBrowserAudio(new(audioId,audio,0,8000,[double.PositiveInfinity],Run())));
    Reject(()=>session.ReceiveBrowserAudio(new(audioId,audio,0,8000,[double.MaxValue],Run())));
    Reject(()=>session.ReceiveBrowserAudio(new(audioId,audio,0,96000,[0],Run())));
    Reject(()=>session.ReceiveBrowserAudio(new(audioId,audio,0,8000,new double[4001],Run())));
    session.ReceiveBrowserAudio(new(audioId,audio,0,8000,[0],Run()));
    Check(Json(session.Snapshot()).GetProperty("canStart").GetBoolean(),"phone audio proof available");
    var standby=Run();await session.CommandAsync(new("start"),CancellationToken.None);
    Reject(()=>session.ReceiveBrowserAudio(new(audioId,audio,0,8000,[0],standby)));
    session.ReceiveBrowserAudio(new(audioId,audio,0,8000,[.1,1.1298022270202637,-1.125],Run()));
    await session.CommandAsync(new("pause"),CancellationToken.None);
    Check(Json(session.Snapshot()).GetProperty("buffers").GetProperty("audio").GetArrayLength()==3,"pause must flush acknowledged PCM even before background tick");
    session.ReceiveBrowserAudio(new(audioId,audio,0,8000,[.9],Run()));
    await session.CommandAsync(new("start"),CancellationToken.None);
    session.ReceiveBrowserAudio(new(audioId,audio,0,8000,[.3],Run()));
    await session.StopPhoneBridgeAsync();
    var pcm=Json(session.Snapshot()).GetProperty("buffers").GetProperty("audio").EnumerateArray().Select(x=>x.GetDouble()).ToArray();
    Check(pcm.SequenceEqual(new[]{.1,1.1298022270202637,-1.125,.3}),"resume and stop preserve measured PCM, discard standby PCM");
    var jpeg=File.ReadAllBytes(Path.Combine(AppContext.BaseDirectory,"Fixtures","chrome-srgb.jpg"));
    BrowserImageColor.RequireSrgb(jpeg,false);
    var changed=(byte[])jpeg.Clone();
    int icc=changed.AsSpan().IndexOf("ICC_PROFILE\0"u8)+14;
    int tagCount=System.Buffers.Binary.BinaryPrimitives.ReadInt32BigEndian(changed.AsSpan(icc+128));
    int xyzOffset=-1,trcOffset=-1;
    for(int i=0;i<tagCount;i++)
    {
        int entry=icc+132+i*12;
        if(changed.AsSpan(entry,4).SequenceEqual("rXYZ"u8))xyzOffset=icc+System.Buffers.Binary.BinaryPrimitives.ReadInt32BigEndian(changed.AsSpan(entry+4));
        if(changed.AsSpan(entry,4).SequenceEqual("rTRC"u8))trcOffset=icc+System.Buffers.Binary.BinaryPrimitives.ReadInt32BigEndian(changed.AsSpan(entry+4));
    }
    Check(xyzOffset>0&&trcOffset>0,"fixture has actual matrix/TRC profile");
    changed[xyzOffset+9]^=0x40;Reject(()=>BrowserImageColor.RequireSrgb(changed,false));
    changed=(byte[])jpeg.Clone();changed[trcOffset+13]^=0x10;Reject(()=>BrowserImageColor.RequireSrgb(changed,false));
    changed=(byte[])jpeg.Clone();changed[icc-2]=2;Reject(()=>BrowserImageColor.RequireSrgb(changed,false));
    var cameraId=library.List().Single(i=>i.Title=="Phone camera").Id;session.Load(cameraId);
    var camera=Json(session.ConfigureBrowserMedia(new(cameraId,0,"camera",Width:32,Height:32,Source:"phone"))).GetProperty("captureId").GetString()!;
    session.ReceiveBrowserFrame(new(cameraId,camera,0,"image/jpeg",Convert.ToBase64String(jpeg),32,32,Run()));
    await session.CommandAsync(new("start"),CancellationToken.None);
    Check(Json(session.ReceiveBrowserFrame(new(cameraId,camera,0,"image/jpeg",Convert.ToBase64String(jpeg),32,32,Run()))).GetProperty("accepted").GetBoolean(),"standby frame must not throttle first measuring frame");
    await session.CommandAsync(new("pause"),CancellationToken.None);
    session.ReceiveBrowserFrame(new(cameraId,camera,0,"image/jpeg",Convert.ToBase64String(jpeg),32,32,Run()));
    await session.CommandAsync(new("start"),CancellationToken.None);
    Check(Json(session.ReceiveBrowserFrame(new(cameraId,camera,0,"image/jpeg",Convert.ToBase64String(jpeg),32,32,Run()))).GetProperty("accepted").GetBoolean(),"resume frame must not be throttled by standby");
    await session.CommandAsync(new("stop"),CancellationToken.None);
    Check(Json(session.Snapshot()).GetProperty("buffers").GetProperty("light").GetArrayLength()==2,"camera records exactly measured frames");
    var journals=string.Join("\n",Directory.GetFiles(Path.Combine(root,"recordings"),"*.jsonl").Select(File.ReadAllText));
    Check(journals.Contains("sample-order-only")&&journals.Contains("server-arrival"),"media journal must identify unsynchronized timing quality");
    Console.WriteLine("PASS: motion probes, mapping, timestamp/finite validation, run revocation and disconnect. Synthetic service validation only; no phone hardware tested.");
}
finally{await session.StopAsync(CancellationToken.None);Directory.Delete(root,true);}
