// SPDX-License-Identifier: GPL-3.0-only
using System.Diagnostics;
using System.Security.Cryptography;
using System.Xml.Linq;
using Phyphox.Core;
using Phyphox.Camera;
using StbImageSharp;
namespace Phyphox.Server;

public sealed record BrowserMediaConfiguration(string SessionId, int InputIndex, string Kind, int? SampleRate=null, int Channels=1, int? Width=null, int? Height=null, string SpectrumAxis="horizontal", string Source="browser");
public sealed record BrowserAudioPacket(string SessionId,string CaptureId,long Sequence,int SampleRate,double[] Samples,string? RunId=null);
public sealed record BrowserFramePacket(string SessionId,string CaptureId,long Sequence,string MimeType,string DataBase64,int Width,int Height,string? RunId=null);
public sealed record BrowserMediaStop(string SessionId,string CaptureId);

public sealed partial class SessionService
{
    sealed class BrowserCapture(BrowserMediaConfiguration configuration, XElement input)
    {
        public readonly string Id=Convert.ToHexString(RandomNumberGenerator.GetBytes(24));
        public readonly BrowserMediaConfiguration Configuration=configuration;
        public readonly XElement Input=input;
        public long Sequence=-1, LastReceived=Stopwatch.GetTimestamp(), Started=Stopwatch.GetTimestamp(), LastFrame;
        public long SampleCount;
        public readonly List<double> Pending=[];
        public bool Used=true, HasData;
    }
    readonly Dictionary<string,BrowserCapture> browserCaptures=[];
    CameraFrame? browserCameraFrame;
    void InvalidateBrowserMedia() { if(runtime?.State=="running")FlushBrowserMedia(false); InvalidatePhoneInputs(); browserCaptures.Clear(); browserCameraFrame=null; InvalidateBrowserAudioOutputLocked(); }
    void StartBrowserMedia()
    {
        foreach(var capture in browserCaptures.Values)
        { capture.Started=Stopwatch.GetTimestamp();if(capture.Configuration.Source!="phone")capture.LastReceived=capture.Started;capture.SampleCount=0;capture.Pending.Clear();capture.Used=true; }
    }
    object[] BrowserMediaSnapshot()=>browserCaptures.Values.Select(c=>(object)new {captureId=c.Id,kind=c.Configuration.Kind,inputIndex=c.Configuration.InputIndex,sampleRate=c.Configuration.SampleRate,width=c.Configuration.Width,height=c.Configuration.Height,source=c.Configuration.Source,ready=c.HasData&&Stopwatch.GetElapsedTime(c.LastReceived).TotalSeconds<=5}).ToArray();
    ExperimentDefinition NativeMediaDefinition(ExperimentDefinition source)=>new()
    {
        SourceXml=source.SourceXml,Title=source.Title,Category=source.Category,Description=source.Description,Version=source.Version,
        Containers=source.Containers,Views=source.Views,Analysis=source.Analysis,Outputs=source.Outputs.Where((_,index)=>!BrowserAudioOutputIndices.Contains(index)).ToArray(),Root=source.Root,CapabilityProblems=source.CapabilityProblems,
        RestoredTimeMappings=source.RestoredTimeMappings,Inputs=source.Inputs.Where((_,index)=>!browserCaptures.Values.Any(c=>c.Configuration.InputIndex==index)).ToArray()
    };
    static string BrowserMapping(XElement element,string fallback="")=>element.Attr("as",element.Attr("component",fallback)).ToLowerInvariant();
    public object ConfigureBrowserMedia(BrowserMediaConfiguration config)
    {
        lock(gate)
        {
            if(runtime==null||entry?.Item.Id!=config.SessionId)throw new InvalidOperationException("浏览器媒体目标实验已改变，请重新打开并配置。");
            if(runtime.State=="running"||starting)throw new InvalidOperationException("请先暂停测量再配置浏览器媒体。");
            if(config.InputIndex<0||config.InputIndex>=runtime.Definition.Inputs.Count)throw new ArgumentException("无效媒体输入槽位。");
            if(config.Source is not("browser" or "phone"))throw new ArgumentException("未知媒体来源。");
            var input=runtime.Definition.Inputs[config.InputIndex];
            if(config.Kind is not("audio" or "camera")||input.Name.LocalName!=config.Kind)throw new ArgumentException("浏览器媒体类型与实验输入不匹配。");
            if(config.Kind=="audio")
            {
                if(config.SampleRate is not(>=8000 and <=192000)||config.Channels!=1)throw new ArgumentException("必须提供真实 AudioContext 采样率（8–192 kHz）和单声道 PCM。");
                if(input.Attributes().Any(a=>a.Name.LocalName is not("rate" or "append")))throw new NotSupportedException("浏览器音频不支持此实验输入属性。");
                if(input.Elements().Count(e=>BrowserMapping(e,"out")=="out")!=1||input.Elements().Any(e=>e.Name.LocalName!="output"||BrowserMapping(e,"out") is not("out" or "rate")))throw new NotSupportedException("浏览器音频只支持一个out和可选rate映射。");
                if(!input.Elements().Any(e=>BrowserMapping(e,"out")=="rate")&&config.SampleRate!=int.Parse(input.Attr("rate","48000")))throw new NotSupportedException("真实采样率与实验要求不同且实验没有rate映射；不会伪装重采样。");
            }
            else
            {
                if(config.Width is not(>=1 and <=1920)||config.Height is not(>=1 and <=1080)||(long)config.Width*config.Height>2_073_600)throw new ArgumentException("帧尺寸限制为实际尺寸且不超过1920×1080。");
                if(config.SpectrumAxis is not("horizontal" or "vertical"))throw new ArgumentException("无效光谱方向。");
                if(input.Attr("feature","photometric").ToLowerInvariant()!="photometric"||!input.Flag("auto_exposure",true)||input.Attr("locked").Length>0||input.Attr("aeStrategy","mean").ToLowerInvariant()!="mean"||input.Attr("aeFPSTarget","0")!="0")throw new NotSupportedException("浏览器只提供未标定普通图像；光谱、固定曝光、物理控制和曝光策略不支持。");
                if(input.Elements().Any(e=>e.Name.LocalName!="output"||BrowserMapping(e) is not("t" or "luma" or "hue" or "saturation" or "value")))throw new NotSupportedException("浏览器相机仅支持t/luma/hue/saturation/value，不提供物理曝光或校准亮度。");
                if(input.Attributes().Any(a=>a.Name.LocalName is not("auto_exposure" or "feature" or "x1" or "x2" or "y1" or "y2" or "aeStrategy" or "aeFPSTarget" or "locked")))throw new NotSupportedException("浏览器相机存在未实现的输入属性。");
                _=CameraAnalysis.ResolveRoi(BrowserRoi(input),config.Width.Value,config.Height.Value);
            }
            if(input.Elements().Any(e=>e.Attributes().Any(a=>a.Name.LocalName is not("as" or "component"))||!runtime.Buffers.ContainsKey(e.Value.Trim())))throw new NotSupportedException("浏览器媒体输出映射无效。");
            var targets=input.Elements().Select(e=>e.Value.Trim()).ToHashSet();
            if(bindings.Any(b=>b.Definition.InputIndex==config.InputIndex||b.Definition.Mappings.Any(m=>targets.Contains(m.Buffer))||targets.Contains(b.Definition.TimeBuffer??"")))throw new InvalidOperationException("输入已有设备来源，不能重复写入。");
            if(browserCaptures.Values.Any(c=>(c.Configuration.InputIndex==config.InputIndex&&c.Configuration.Source!=config.Source)||(c.Configuration.InputIndex!=config.InputIndex&&c.Input.Elements().Any(e=>targets.Contains(e.Value.Trim())))))throw new InvalidOperationException("输入或容器已有浏览器来源，请先停止原采集再切换来源。");
            if(phoneCaptures.Values.Any(c=>c.Input.Elements().Any(e=>input.Elements().Any(m=>m.Value.Trim()==e.Value.Trim()))))throw new InvalidOperationException("容器已有手机运动来源，不能重复写入。");
            foreach(var old in browserCaptures.Values.Where(c=>c.Configuration.InputIndex==config.InputIndex).ToArray())browserCaptures.Remove(old.Id);
            var capture=new BrowserCapture(config,input);browserCaptures.Add(capture.Id,capture);
            error=null;revision++;
            return new {captureId=capture.Id,session=SnapshotUnsafe(),maximumFrameRate=10,timeoutSeconds=5};
        }
    }
    BrowserCapture BrowserOwner(string sessionId,string captureId,string kind,long sequence)
    {
        if(entry?.Item.Id!=sessionId||!browserCaptures.TryGetValue(captureId,out var capture)||capture.Configuration.Kind!=kind)throw new InvalidOperationException("浏览器媒体流已失效；停止、清空或更换实验后必须重新启用。");
        if(sequence<0||sequence<=capture.Sequence)throw new ArgumentException("媒体sequence重复或倒序。");
        return capture;
    }
    public object ReceiveBrowserAudio(BrowserAudioPacket packet)
    {
        lock(gate)
        {
            var capture=BrowserOwner(packet.SessionId,packet.CaptureId,"audio",packet.Sequence);
            if(capture.Configuration.Source=="phone"&&packet.RunId!=phoneRunId)throw new InvalidOperationException("手机运行令牌已失效。");
            try
            {
                if(packet.Sequence != capture.Sequence + 1)throw new ArgumentException("PCM序号缺失，不能将断续数据伪装为连续音频。");
                if(packet.SampleRate!=capture.Configuration.SampleRate)throw new ArgumentException($"PCM采样率已改变：配置{capture.Configuration.SampleRate}Hz，收到{packet.SampleRate}Hz，请重新绑定。");
                if(packet.Samples==null||packet.Samples.Length==0)throw new ArgumentException("PCM批次缺少样本。");
                if(packet.Samples.Length>packet.SampleRate/2)throw new ArgumentException($"PCM批次超过0.5秒：{packet.Samples.Length}样本/{packet.SampleRate}Hz。");
                if(packet.Samples.Any(x=>!double.IsFinite(x)))throw new ArgumentException("PCM批次包含非有限样本。");
                // Web Audio exposes Float32 PCM. [-1,1] is nominal, not a clipping bound:
                // resampling/filter peaks may exceed it and must remain unchanged for analysis.
                if(packet.Samples.Any(x=>Math.Abs(x)>float.MaxValue))throw new ArgumentException("PCM样本超出有限Float32范围。");
                var now=Stopwatch.GetTimestamp();
                if(runtime?.State=="running")
                {
                    if(capture.SampleCount+packet.Samples.Length>(Stopwatch.GetElapsedTime(capture.Started,now).TotalSeconds+1)*packet.SampleRate||capture.Pending.Count+packet.Samples.Length>packet.SampleRate)throw new InvalidOperationException("PCM输入速度或积压超过1秒限制，停止测量。");
                    capture.SampleCount+=packet.Samples.Length;capture.Pending.AddRange(packet.Samples);
                }
                capture.HasData=true;capture.Sequence=packet.Sequence;capture.LastReceived=now;
                return new {accepted=true,sequence=packet.Sequence};
            }
            catch(Exception ex) { if(runtime?.State=="running")MediaFault("浏览器麦克风输入失败："+ex.Message);throw; }
        }
    }
    static NormalizedCameraRoi BrowserRoi(XElement input)=>new(XmlUtil.Number(input.Attr("x1","0.4")),XmlUtil.Number(input.Attr("y1","0.4")),XmlUtil.Number(input.Attr("x2","0.6")),XmlUtil.Number(input.Attr("y2","0.6")));
    public object ReceiveBrowserFrame(BrowserFramePacket packet)
    {
        lock(gate)
        {
            var capture=BrowserOwner(packet.SessionId,packet.CaptureId,"camera",packet.Sequence);
            if(capture.Configuration.Source=="phone"&&packet.RunId!=phoneRunId)throw new InvalidOperationException("手机运行令牌已失效。");
            try
            {
                if(packet.Width!=capture.Configuration.Width||packet.Height!=capture.Configuration.Height||packet.MimeType is not("image/jpeg" or "image/png")||packet.DataBase64==null||packet.DataBase64.Length>4_000_000)throw new ArgumentException("相机帧类型/实际尺寸无效或超过3MB预算。");
                long now=Stopwatch.GetTimestamp();
                var arrivalExperimentTime=runtime?.ExperimentTime??0;
                if(capture.LastFrame!=0&&Stopwatch.GetElapsedTime(capture.LastFrame,now).TotalSeconds<0.1)return new {accepted=false,sequence=packet.Sequence,reason="frame-rate-limit"};
                var encoded=Convert.FromBase64String(packet.DataBase64);
                bool png=encoded.AsSpan().StartsWith(new byte[]{137,80,78,71,13,10,26,10});bool jpeg=encoded.AsSpan().StartsWith(new byte[]{255,216,255});
                if((packet.MimeType=="image/png"&&!png)||(packet.MimeType=="image/jpeg"&&!jpeg))throw new ArgumentException("帧MIME与PNG/JPEG签名不匹配。");
                BrowserImageColor.RequireSrgb(encoded,png);
                using var stream=new MemoryStream(encoded,false);var info=ImageInfo.FromStream(stream);
                if(info==null||info.Value.Width!=packet.Width||info.Value.Height!=packet.Height)throw new ArgumentException("编码帧尺寸与声明不符。");
                stream.Position=0;var image=ImageResult.FromStream(stream,ColorComponents.RedGreenBlue);
                for(int i=0;i<image.Data.Length;i+=3)(image.Data[i],image.Data[i+2])=(image.Data[i+2],image.Data[i]);
                var analysis=CameraAnalysis.AnalyzeBgr(image.Data,image.Width,image.Height,image.Width*3,CameraAnalysis.ResolveRoi(BrowserRoi(capture.Input),image.Width,image.Height));
                var frame=new CameraFrame(packet.Sequence,image.Width,image.Height,0,now,DateTimeOffset.UtcNow,packet.MimeType=="image/jpeg"?encoded:[],analysis,[]);
                if(runtime?.State=="running")
                {
                    var writes=MediaCoordinator.MapCameraFrame(capture.Input,frame,arrivalExperimentTime,null);
                    if(writes.Append.Count>0)IngestBrowserMedia(capture,writes.Append,false);
                }
                browserCameraFrame=packet.MimeType=="image/jpeg"?frame:null;
                capture.HasData=true;capture.Sequence=packet.Sequence;capture.LastReceived=capture.LastFrame=now;
                return new {accepted=true,sequence=packet.Sequence};
            }
            catch(Exception ex) { if(runtime?.State=="running")MediaFault("浏览器相机输入失败："+ex.Message);throw; }
        }
    }
    void IngestBrowserMedia(BrowserCapture capture,IReadOnlyDictionary<string,double[]> writes,bool replace)
    {
        if(capture.Configuration.Source!="phone"){IngestMedia(writes,replace);return;}
        if(runtime?.State!="running")return;
        var metadata=new Dictionary<string,string>{["source"]="phone-browser",["sensor"]=capture.Configuration.Kind,["timeQuality"]=capture.Configuration.Kind=="audio"?"sample-order-only":"server-arrival",["hardwareVerified"]="false"};
        if(capture.Configuration.SampleRate is int rate)metadata["sampleRate"]=rate.ToString(System.Globalization.CultureInfo.InvariantCulture);
        Record(new(){Sequence=0,Kind="input",Buffers=writes.ToDictionary(p=>p.Key,p=>p.Value),ReplaceBuffers=replace?writes.Keys.ToArray():[],SourceMetadata=metadata});
        foreach(var(name,values) in writes)runtime.ReceiveSamples(name,values,!replace);
        revision++;
    }
    void FlushBrowserMedia(bool checkFreshness=true)
    {
        foreach(var capture in browserCaptures.Values)
        {
            if(checkFreshness&&Stopwatch.GetElapsedTime(capture.LastReceived).TotalSeconds>5)throw new IOException("浏览器媒体超过5秒无真实数据，测量已停止；请检查权限、设备或页面连接并重新启用。");
            if(capture.Configuration.Kind!="audio"||capture.Pending.Count==0)continue;
            var target=capture.Input.Elements().Single(e=>BrowserMapping(e,"out")=="out").Value.Trim();
            IngestBrowserMedia(capture,new Dictionary<string,double[]>{{target,capture.Pending.ToArray()}},capture.Used&&!capture.Input.Flag("append"));capture.Pending.Clear();capture.Used=false;
            var rate=capture.Input.Elements().FirstOrDefault(e=>BrowserMapping(e,"out")=="rate");
            if(rate!=null)IngestBrowserMedia(capture,new Dictionary<string,double[]>{{rate.Value.Trim(),[(double)capture.Configuration.SampleRate!]}},false);
        }
    }
    void BrowserAnalysisCompleted(){foreach(var capture in browserCaptures.Values)capture.Used=true;}
    public async Task<object> StopBrowserMediaAsync(BrowserMediaStop request,CancellationToken ct)
    {
        await commandGate.WaitAsync(ct);
        try
        {
            lock(gate){if(entry?.Item.Id!=request.SessionId||!browserCaptures.ContainsKey(request.CaptureId))throw new InvalidOperationException("媒体流已经失效。");}
            await StopAcquisitionAsync();
            return Command(new("stop"));
        }
        finally {commandGate.Release();}
    }
}
