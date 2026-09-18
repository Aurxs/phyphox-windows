using System.Text.Json;
using Phyphox.Server;
using Phyphox.Devices;

internal static class BrowserMediaIntegration
{
    static JsonElement Json(object value)=>JsonSerializer.SerializeToElement(value);
    static void Check(bool condition,string message){if(!condition)throw new Exception(message);}
    static void Reject(Action action){try{action();}catch(ArgumentException){return;}catch(InvalidOperationException){return;}throw new Exception("Stale/invalid browser packet accepted.");}
    public static async Task Run()
    {
        string root=Path.Combine(Path.GetTempPath(),"phyphox-browser-test-"+Guid.NewGuid().ToString("N"));
        string assets=Path.Combine(root,"assets"),samples=Path.Combine(assets,"samples");Directory.CreateDirectory(samples);
        const string audio="<phyphox version='1.20'><title>Audio bridge</title><category>Tests</category><data-containers><container size='0'>pcm</container><container>rate</container></data-containers><input><audio rate='48000' append='true'><output>pcm</output><output as='rate'>rate</output></audio></input></phyphox>";
        const string camera="<phyphox version='1.20'><title>Camera bridge</title><category>Tests</category><data-containers><container>light</container></data-containers><input><camera feature='photometric'><output component='luma'>light</output></camera></input></phyphox>";
        File.WriteAllText(Path.Combine(samples,"audio.phyphox"),audio);File.WriteAllText(Path.Combine(samples,"camera.phyphox"),camera);
        var library=new LibraryService(assets,Path.Combine(root,"data"));var session=new SessionService(library,Path.Combine(root,"data"),new DeviceManager());
        await session.StartAsync(CancellationToken.None);
        try
        {
            string id=library.List().Single(i=>i.Title=="Audio bridge").Id;session.Load(id);
            var configured=Json(session.ConfigureBrowserMedia(new(id,0,"audio",48000)));
            string token=configured.GetProperty("captureId").GetString()!;
            Check(configured.GetProperty("session").GetProperty("canStart").GetBoolean(),"Explicit browser input did not replace native requirement");
            session.ReceiveBrowserAudio(new(id,token,0,48000,[.25,-.5]));
            Check(Json(session.Snapshot()).GetProperty("buffers").GetProperty("pcm").GetArrayLength()==0,"Armed PCM entered experiment");
            await session.CommandAsync(new("start"),CancellationToken.None);
            session.ReceiveBrowserAudio(new(id,token,1,48000,[.25,-.5]));await Task.Delay(80);
            Check(Json(session.Snapshot()).GetProperty("buffers").GetProperty("pcm").GetArrayLength()==2,"PCM did not reach service buffers");
            Reject(()=>session.ReceiveBrowserAudio(new(id,token,1,48000,[.1])));
            await session.CommandAsync(new("pause"),CancellationToken.None);
            Check(Json(session.Snapshot()).GetProperty("browserMedia").GetArrayLength()==1,"Pause invalidated stream");
            session.ReceiveBrowserAudio(new(id,token,2,48000,[.7]));
            Check(Json(session.Snapshot()).GetProperty("buffers").GetProperty("pcm").GetArrayLength()==2,"Paused PCM entered experiment");
            await session.CommandAsync(new("start"),CancellationToken.None);session.ReceiveBrowserAudio(new(id,token,3,48000,[.1]));await Task.Delay(50);
            await session.CommandAsync(new("clear"),CancellationToken.None);
            Reject(()=>session.ReceiveBrowserAudio(new(id,token,4,48000,[.1])));
            token=Json(session.ConfigureBrowserMedia(new(id,0,"audio",48000))).GetProperty("captureId").GetString()!;
            await session.CommandAsync(new("start"),CancellationToken.None);
            Reject(()=>session.ReceiveBrowserAudio(new(id,token,1,48000,[.1])));
            Check(Json(session.Snapshot()).GetProperty("status").GetString()!="running","Missing PCM sequence did not stop measurement");
            await session.CommandAsync(new("stop"),CancellationToken.None);
            token=Json(session.ConfigureBrowserMedia(new(id,0,"audio",48000))).GetProperty("captureId").GetString()!;
            await session.CommandAsync(new("start"),CancellationToken.None);await Task.Delay(5300);
            var timed=Json(session.Snapshot());Check(timed.GetProperty("status").GetString()!="running"&&timed.GetProperty("error").GetString()!.Contains("5秒"),"Missing browser upload did not fault");
            await session.CommandAsync(new("stop"),CancellationToken.None);
            string cameraId=library.List().Single(i=>i.Title=="Camera bridge").Id;await session.LoadAsync(cameraId,CancellationToken.None);
            string cameraToken=Json(session.ConfigureBrowserMedia(new(cameraId,0,"camera",Width:1,Height:1))).GetProperty("captureId").GetString()!;
            await session.CommandAsync(new("start"),CancellationToken.None);
            const string png="iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAQAAAC1HAwCAAAAC0lEQVR42mP8/x8AAwMCAO+aPp8AAAAASUVORK5CYII=";
            session.ReceiveBrowserFrame(new(cameraId,cameraToken,0,"image/png",png,1,1));
            Check(Json(session.Snapshot()).GetProperty("buffers").GetProperty("light").GetArrayLength()==1,"Decoded PNG did not enter camera ROI analysis");
            await session.CommandAsync(new("stop"),CancellationToken.None);
            Reject(()=>session.ReceiveBrowserFrame(new(cameraId,cameraToken,1,"image/png",png,1,1)));
            Console.WriteLine("PASS browser bridge: armed/pause isolation, PCM mapping, resume, duplicate/stale rejection, timeout stop, PNG ROI. Fixture tests, not hardware acceptance.");
        }
        finally{await session.StopAsync(CancellationToken.None);session.Dispose();Directory.Delete(root,true);}
    }
}
