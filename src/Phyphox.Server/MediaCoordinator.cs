// SPDX-License-Identifier: GPL-3.0-only
using System.Globalization;
using System.Threading.Channels;
using System.Xml.Linq;
using Phyphox.Core;
using Phyphox.Media;
using Phyphox.Camera;

namespace Phyphox.Server;

/// <summary>Session-owned actual audio I/O. Call FlushInputs before analysis, UpdateOutputs after analysis; await start/stop outside the session lock.</summary>
public sealed record CameraExperimentProfile(int Index=0,int Width=640,int Height=480,double FramesPerSecond=30,SpectrumAxis SpectrumAxis=SpectrumAxis.Horizontal,ExposureMetadata? Exposure=null,bool FixedExposureVerified=false,CameraControlRequest[]? Controls=null,bool? VerifiedAutoExposureMode=null);
public sealed record CameraBufferUpdate(IReadOnlyDictionary<string,double[]> Append,IReadOnlyDictionary<string,double[]> Replace);
public sealed class MediaCoordinator : IAsyncDisposable
{
    private readonly object gate=new();
    private readonly WasapiAudioService audio=new();
    private readonly WindowsCameraService cameraService=new();
    private CameraExperimentProfile cameraProfile=new();
    private Func<long,double>? cameraTimeMapper;
    private readonly Queue<CameraFrame> cameraFrames=new();
    private XElement? cameraInput;
    private Task? cameraTask;
    public CameraFrame? LatestCameraFrame {get;private set;}
    public void ConfigureCamera(CameraExperimentProfile profile){lock(gate){if(lifetime!=null)throw new InvalidOperationException("Stop before changing camera profile.");cameraProfile=profile;}}
    public void SetCameraTimeMapper(Func<long,double> mapper){lock(gate)cameraTimeMapper=mapper;}

    private CancellationTokenSource? lifetime;
    private Task? captureTask,outputTask;
    private XElement? audioInput,audioOutput;
    private Func<string,double[]>? readBuffer;
    private Action<IReadOnlyDictionary<string,double[]>,bool>? ingest;
    private Action<string>? reportFault;
    private Channel<AudioPlaybackRequest>? outputUpdates;
    private AudioPlaybackRequest? lastOutput;
    private readonly List<float> pending=[];
    private int actualRate;
    private bool recordingUsed=true;
    public event Action<string>? Notice;
    public bool Running {get {lock(gate)return lifetime!=null;}}
    public IReadOnlyList<string> CapabilityIssues(ExperimentDefinition definition) {
        var issues=new List<string>();var audioInputs=definition.Inputs.Where(x=>x.Name.LocalName=="audio").ToArray();var audioOutputs=definition.Outputs.Where(x=>x.Name.LocalName=="audio").ToArray();
        if(audioInputs.Length+audioOutputs.Length>0 && !audio.IsAvailable)issues.Add("当前选择的原生音频接口需要 Windows WASAPI；可在媒体页显式启用浏览器音频来源或扬声器。");
        if(audioInputs.Length>1||audioOutputs.Length>1)issues.Add("当前每个实验只支持一个音频输入和一个音频输出。");
        var names=definition.Containers.Select(x=>x.Name).ToHashSet();
        foreach(var input in audioInputs) {
            CheckAttributes(input,["rate","append"],issues);
            var mappings=input.Elements().ToArray();
            if(mappings.Count(x=>Mapping(x,"out")=="out")!=1)issues.Add("音频输入必须有且仅有一个 out 输出。");
            foreach(var mapping in mappings) {
                if(mapping.Name.LocalName!="output"||Mapping(mapping,"out") is not("out" or "rate"))issues.Add("音频输入仅支持 out/rate 容器映射。");
                if(!names.Contains(mapping.Value.Trim()))issues.Add("音频输入引用不存在的容器："+mapping.Value.Trim());
                CheckAttributes(mapping,["as","component"],issues);
            }
        }
        foreach(var output in audioOutputs) issues.AddRange(AudioOutputCapabilityIssues(output,names));
        var cameras=definition.Inputs.Where(x=>x.Name.LocalName=="camera").ToArray();
        if(cameras.Length>1)issues.Add("一个实验最多支持一个普通相机输入。");
        foreach(var camera in cameras) {
            if(!cameraService.IsAvailable)issues.Add("原生普通相机采集需要 Windows Media Foundation。");
            issues.AddRange(CameraCapabilityIssues(camera,cameraProfile,cameraTimeMapper!=null));
            foreach(var output in camera.Elements())if(!names.Contains(output.Value.Trim()))issues.Add("相机输出引用不存在的容器："+output.Value.Trim());
        }
        return issues.Distinct().ToArray();
    }
    public static IReadOnlyList<string> AudioOutputCapabilityIssues(XElement output,IEnumerable<string> containerNames) {
        var names=containerNames.ToHashSet();var issues=new List<string>();
            CheckAttributes(output,["rate","loop","normalize"],issues);
            if(!output.Flag("loop"))issues.Add("非循环音频的逐分析触发时序尚未迁移；不能将此类实验视为交互兼容。");
            foreach(var plugin in output.Elements()) {
                if(plugin.Name.LocalName is not("input" or "tone" or "noise")){issues.Add("不支持的音频插件："+plugin.Name.LocalName);continue;}
                if(plugin.Name.LocalName=="input") {CheckAttributes(plugin,["type","keep"],issues);if(plugin.Attr("type","buffer")!="buffer")issues.Add("直接音频必须引用数据容器。");if(!names.Contains(plugin.Value.Trim()))issues.Add("直接音频引用不存在的容器。");continue;}
                CheckAttributes(plugin,plugin.Name.LocalName=="tone"?["waveform"]:[],issues);
                if(plugin.Name.LocalName=="tone" && plugin.Attr("waveform","sine").ToLowerInvariant() is not("sine" or "square" or "sawtooth"))issues.Add("不支持的音频波形。");
                foreach(var parameter in plugin.Elements()) {
                    var name=parameter.Attr("parameter").ToLowerInvariant();
                    if(parameter.Name.LocalName!="input"||name is not("amplitude" or "pan" or "duration" or "frequency")||(plugin.Name.LocalName=="noise"&&name=="frequency"))issues.Add("不支持的音频参数："+name);
                    CheckAttributes(parameter,["parameter","type","keep"],issues);
                    if(parameter.Attr("type","buffer")=="buffer"&&!names.Contains(parameter.Value.Trim()))issues.Add("音频参数引用不存在的容器。");
                    if(parameter.Attr("type","buffer") is not("buffer" or "value"))issues.Add("不支持的音频参数输入类型。");
                }
            }
            if(output.Elements("input").Count()>1)issues.Add("多直接音频源的混合语义尚未实现。");
        return issues;
    }
    public static IReadOnlyList<string> CameraCapabilityIssues(XElement camera,CameraExperimentProfile profile,bool hasTimeMapper) {
        var issues=new List<string>();
        CheckAttributes(camera,["auto_exposure","feature","x1","x2","y1","y2","aeStrategy","aeFPSTarget","locked"],issues);
        if(profile.VerifiedAutoExposureMode!=camera.Flag("auto_exposure",true))issues.Add("相机曝光模式需要外部真机验证配置；当前 MSMF Get 返回默认值，不能证明 auto_exposure 已生效。");
        if(profile.Controls is {Length:>0})issues.Add("当前 MSMF 后端无法可靠回读相机控制值；实验绑定不执行未经验证的控制设置。");
        var feature=camera.Attr("feature","photometric").ToLowerInvariant();
        if(feature is not("photometric" or "spectroscopy"))issues.Add("不支持的相机分析类型："+feature);
        if(camera.Attr("aeStrategy","mean").ToLowerInvariant()!="mean")issues.Add("尚未实现相机自动曝光策略："+camera.Attr("aeStrategy"));
        if(camera.Attr("locked").Length>0)issues.Add("相机 locked 物理设置尚无经过验证的 Windows 控制映射："+camera.Attr("locked"));
        if(camera.Attr("aeFPSTarget","0")!="0")issues.Add("相机 aeFPSTarget 自动曝光/帧率联动尚未实现。");
        try {_=CameraAnalysis.ResolveRoi(NormalizedRoi(camera),profile.Width,profile.Height);}catch(Exception ex)when(ex is ArgumentException or FormatException){issues.Add("相机 ROI 无效："+ex.Message);}
        var kinds=camera.Elements().Select(x=>Mapping(x,"")).ToArray();
        foreach(var output in camera.Elements()) {
            CheckAttributes(output,["component","as"],issues);
            if(output.Name.LocalName!="output"||Mapping(output,"") is not("t" or "luma" or "luminance" or "hue" or "saturation" or "value" or "shutterspeed" or "iso" or "aperture" or "pixelposition"))issues.Add("不支持的相机输出映射："+Mapping(output,""));
        }
        if(kinds.Contains("t")&&!hasTimeMapper)issues.Add("相机时间输出尚未绑定实验单调时钟。");
        if(kinds.Contains("pixelposition")&&feature!="spectroscopy")issues.Add("pixelPosition 仅支持 spectroscopy。");
        if(feature=="spectroscopy"&&(!kinds.Contains("pixelposition")||!kinds.Contains("luminance")))issues.Add("光谱需要 pixelPosition 与 luminance 输出。");
        bool physical=kinds.Any(x=>x is "luminance" or "shutterspeed" or "iso" or "aperture");
        if(physical&&(profile.Exposure==null||!profile.FixedExposureVerified||camera.Flag("auto_exposure",true)))issues.Add("曝光归一化亮度/光谱或物理曝光输出需要已验证固定曝光配置且 auto_exposure=false；不能使用相对亮度或虚构 ISO。");
        if(!camera.Flag("auto_exposure",true)&&!profile.FixedExposureVerified)issues.Add("手动曝光需要经过真机验证的固定曝光配置。");
        return issues.Distinct().ToArray();
    }
    private static NormalizedCameraRoi NormalizedRoi(XElement input)=>new(XmlUtil.Number(input.Attr("x1","0.4")),XmlUtil.Number(input.Attr("y1","0.4")),XmlUtil.Number(input.Attr("x2","0.6")),XmlUtil.Number(input.Attr("y2","0.6")));
    public static CameraBufferUpdate MapCameraFrame(XElement input,CameraFrame frame,double time,ExposureMetadata? exposure) {
        var append=new Dictionary<string,double[]>();var replace=new Dictionary<string,double[]>();bool spectrum=input.Attr("feature","photometric").Equals("spectroscopy",StringComparison.OrdinalIgnoreCase);
        foreach(var output in input.Elements()) {
            string key=Mapping(output,"");double[] data=key switch {
                "t"=>[time],"luma"=>[frame.Analysis.Luma],"hue"=>[frame.Analysis.HueDegrees],"saturation"=>[frame.Analysis.Saturation],"value"=>[frame.Analysis.Value],
                "luminance"=>spectrum?frame.Analysis.ExposureCorrectedSpectrum??throw new InvalidOperationException("Missing exposure-corrected spectrum."):[frame.Analysis.ExposureCorrectedLuminance??throw new InvalidOperationException("Missing exposure-corrected luminance.")],
                "pixelposition"=>frame.Analysis.SpectrumPixelPositions,
                "iso"=>[exposure?.Iso??throw new InvalidOperationException("Missing ISO.")],"aperture"=>[exposure?.ApertureValue??throw new InvalidOperationException("Missing aperture.")],"shutterspeed"=>[(exposure?.ShutterNanoseconds??throw new InvalidOperationException("Missing shutter"))/1e9],
                _=>throw new NotSupportedException("Unknown camera mapping: "+key)};
            if(spectrum&&(key is "luminance" or "pixelposition"))replace.Add(output.Value.Trim(),data);else append.Add(output.Value.Trim(),data);
        }
        return new(append,replace);
    }
    private static void CheckAttributes(XElement element,string[] allowed,List<string> issues) {
        foreach(var attr in element.Attributes().Where(x=>!x.IsNamespaceDeclaration && x.Name.NamespaceName.Length==0))if(!allowed.Contains(attr.Name.LocalName))issues.Add($"尚未实现媒体属性 {element.Name.LocalName}.{attr.Name.LocalName}。");
    }
    private static string Mapping(XElement element,string fallback)=>element.Attr("as",element.Attr("component",fallback)).ToLowerInvariant();
    public async Task StartAsync(ExperimentDefinition definition,Func<string,double[]> readBuffer,Action<IReadOnlyDictionary<string,double[]>,bool> ingest,Action<string> fault,CancellationToken cancellationToken=default) {
        var issues=CapabilityIssues(definition);if(issues.Count>0)throw new NotSupportedException(string.Join("；",issues));
        await StopAsync();
        var input=definition.Inputs.FirstOrDefault(x=>x.Name.LocalName=="audio");var output=definition.Outputs.FirstOrDefault(x=>x.Name.LocalName=="audio");var camera=definition.Inputs.FirstOrDefault(x=>x.Name.LocalName=="camera");
        if(input==null&&output==null&&camera==null)return;
        var life=CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        lock(gate){lifetime=life;audioInput=input;audioOutput=output;cameraInput=camera;cameraFrames.Clear();LatestCameraFrame=null;this.readBuffer=readBuffer;this.ingest=ingest;reportFault=fault;pending.Clear();lastOutput=null;actualRate=0;recordingUsed=true;}
        var ready=new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var outputReady=new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var cameraReady=new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        try {
            if(input!=null)captureTask=Capture(input,ready,life.Token);
            if(camera!=null)cameraTask=CaptureCamera(camera,cameraReady,life.Token);
            if(output!=null) {
                outputUpdates=Channel.CreateBounded<AudioPlaybackRequest>(new BoundedChannelOptions(1){SingleReader=true,FullMode=BoundedChannelFullMode.DropOldest});
                var initial=SnapshotOutput(output,readBuffer);
                _=new AudioMixer(initial);
                lastOutput=initial;
                outputTask=OutputLoop(outputUpdates.Reader,outputReady,life.Token);
                outputUpdates.Writer.TryWrite(initial);
            }
            if(input!=null)await ready.Task.WaitAsync(TimeSpan.FromSeconds(10),cancellationToken);
            if(output!=null)await outputReady.Task.WaitAsync(TimeSpan.FromSeconds(10),cancellationToken);
            if(camera!=null)await cameraReady.Task.WaitAsync(TimeSpan.FromSeconds(15),cancellationToken);
        }catch{await StopAsync();throw;}
    }
    private async Task CaptureCamera(XElement input,TaskCompletionSource ready,CancellationToken token) {
        try {
            var profile=cameraProfile;bool auto=input.Flag("auto_exposure",true);
            if(profile.VerifiedAutoExposureMode!=auto)throw new NotSupportedException("Camera exposure mode has not been externally verified.");
            var request=new CameraCaptureRequest(profile.Index,profile.Width,profile.Height,profile.FramesPerSecond,SpectrumAxis:profile.SpectrumAxis,Exposure:profile.Exposure,NormalizedRoi:NormalizedRoi(input));
            Notice?.Invoke($"相机索引 {profile.Index}（不自动切换其他设备）；t 使用服务接收时间，非硬件曝光时间。预览坐标为 Windows 原始帧坐标。");
            await foreach(var frame in cameraService.CaptureAsync(request,token)) {
                lock(gate){if(cameraFrames.Count>=120)throw new IOException("相机输入积压超过 120 帧，停止采集。");cameraFrames.Enqueue(frame);LatestCameraFrame=frame;}
                ready.TrySetResult();
            }
            if(!token.IsCancellationRequested)throw new IOException("相机采集意外结束。");
        }catch(OperationCanceledException)when(token.IsCancellationRequested){ready.TrySetCanceled(token);}
        catch(Exception ex){ready.TrySetException(ex);reportFault?.Invoke("相机采集失败："+ex.Message);}
    }
    private void FlushCameraInputs() {
        CameraFrame[] frames;XElement? input;Action<IReadOnlyDictionary<string,double[]>,bool>? callback;Func<long,double>? clock;ExposureMetadata? exposure;
        lock(gate){frames=cameraFrames.ToArray();cameraFrames.Clear();input=cameraInput;callback=ingest;clock=cameraTimeMapper;exposure=cameraProfile.Exposure;}
        if(input==null||callback==null)return;
        foreach(var frame in frames){var data=MapCameraFrame(input,frame,clock?.Invoke(frame.ReceivedTimestamp)??double.NaN,exposure);if(data.Append.Count>0)callback(data.Append,false);if(data.Replace.Count>0)callback(data.Replace,true);}
    }
    private async Task Capture(XElement input,TaskCompletionSource ready,CancellationToken cancellationToken) {
        try {
            int requestedRate=int.Parse(input.Attr("rate","48000"),CultureInfo.InvariantCulture);
            bool hasRate=input.Elements().Any(x=>Mapping(x,"out")=="rate");
            await foreach(var batch in audio.CaptureAsync(cancellationToken:cancellationToken)) {
                if(!hasRate&&batch.SampleRate!=requestedRate)throw new NotSupportedException($"实际麦克风采样率 {batch.SampleRate} 与实验要求 {requestedRate} 不同，实验未映射 rate；拒绝错误时间基准。");
                lock(gate){if(pending.Count+batch.Samples.Length>batch.SampleRate*10L)throw new IOException("音频分析输入积压超过 10 秒，停止采集。");pending.AddRange(batch.Samples);actualRate=batch.SampleRate;}
                ready.TrySetResult();
            }
            if(!cancellationToken.IsCancellationRequested)throw new IOException("音频采集意外结束。");
        }catch(OperationCanceledException)when(cancellationToken.IsCancellationRequested){ready.TrySetCanceled(cancellationToken);}
        catch(Exception ex){ready.TrySetException(ex);reportFault?.Invoke("音频输入失败："+ex.Message);}
    }
    /// <summary>Must run just before analysis. Commit all PCM received since the preceding flush as one official input read.</summary>
    public void FlushInputs() {
        FlushCameraInputs();
        XElement? input;Action<IReadOnlyDictionary<string,double[]>,bool>? callback;double[] samples;int rate;bool replace;
        lock(gate){input=audioInput;callback=ingest;if(input==null||pending.Count==0)return;samples=pending.Select(x=>(double)x).ToArray();pending.Clear();rate=actualRate;replace=recordingUsed&&!input.Flag("append");recordingUsed=false;}
        string target=input.Elements().Single(x=>Mapping(x,"out")=="out").Value.Trim();
        callback?.Invoke(new Dictionary<string,double[]>{{target,samples}},replace);
        var rateMapping=input.Elements().FirstOrDefault(x=>Mapping(x,"out")=="rate");
        if(rateMapping!=null)callback?.Invoke(new Dictionary<string,double[]>{{rateMapping.Value.Trim(),[(double)rate]}},false);
    }
    public void UpdateOutputs() {
        XElement? output;Func<string,double[]>? read;Channel<AudioPlaybackRequest>? updates;
        lock(gate){recordingUsed=true;output=audioOutput;read=readBuffer;updates=outputUpdates;}
        if(output==null||read==null||updates==null)return;
        try {
            var request=SnapshotOutput(output,read);
            lock(gate){if(Same(lastOutput,request))return;lastOutput=request;}
            updates.Writer.TryWrite(request);
        }catch(Exception ex){reportFault?.Invoke("音频输出参数无效："+ex.Message);}
    }
    public static AudioPlaybackRequest SnapshotOutput(XElement output,Func<string,double[]> read) {
        double Parameter(XElement plugin,string name,double fallback){var e=plugin.Elements().LastOrDefault(x=>x.Attr("parameter").Equals(name,StringComparison.OrdinalIgnoreCase));if(e==null)return fallback;return e.Attr("type","buffer")=="value"?XmlUtil.Number(e.Value):read(e.Value.Trim()).LastOrDefault(double.NaN);}
        var direct=output.Elements().FirstOrDefault(x=>x.Name.LocalName=="input");
        var tones=output.Elements().Where(x=>x.Name.LocalName=="tone").Select(x=>new AudioTone(Parameter(x,"frequency",440),Parameter(x,"amplitude",1),Parameter(x,"pan",0),Parameter(x,"duration",1),Enum.Parse<AudioWaveform>(x.Attr("waveform","sine"),true))).ToArray();
        var noise=output.Elements().Where(x=>x.Name.LocalName=="noise").Select(x=>new AudioNoise(Parameter(x,"amplitude",1),Parameter(x,"pan",0),Parameter(x,"duration",1))).ToArray();
        return new(SampleRate:int.Parse(output.Attr("rate","48000"),CultureInfo.InvariantCulture),MonoSamples:direct==null?null:read(direct.Value.Trim()).Select(x=>(float)x).ToArray(),Loop:output.Flag("loop"),Normalize:output.Flag("normalize"),Tones:tones,Noise:noise);
    }
    public static bool Same(AudioPlaybackRequest? a,AudioPlaybackRequest b)=>a!=null&&a.SampleRate==b.SampleRate&&a.Loop==b.Loop&&a.Normalize==b.Normalize&&(a.MonoSamples??[]).SequenceEqual(b.MonoSamples??[])&&(a.Tones??[]).SequenceEqual(b.Tones??[])&&(a.Noise??[]).SequenceEqual(b.Noise??[]);
    private async Task OutputLoop(ChannelReader<AudioPlaybackRequest> updates,TaskCompletionSource ready,CancellationToken cancellationToken) {
        CancellationTokenSource? playing=null;Task? playback=null;bool first=true;
        try {
            await foreach(var request in updates.ReadAllAsync(cancellationToken)) {
                if(playing!=null){await playing.CancelAsync();if(playback!=null)try{await playback;}catch(OperationCanceledException){}playing.Dispose();}
                if(!first)Notice?.Invoke("音频参数变化：播放已重启，产生输出间隙并重置相位；尚不提供无缝相位连续。");first=false;
                playing=CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);playback=audio.PlayAsync(request,playing.Token);
                if(playback.IsCompleted)await playback;
                ready.TrySetResult();
                // Observe asynchronous device failures even if no subsequent parameter update arrives.
                _=ObservePlayback(playback,playing.Token);
            }
        }catch(OperationCanceledException)when(cancellationToken.IsCancellationRequested){ready.TrySetCanceled(cancellationToken);}
        catch(Exception ex){ready.TrySetException(ex);reportFault?.Invoke("音频输出失败："+ex.Message);throw;}
        finally {if(playing!=null){await playing.CancelAsync();if(playback!=null)try{await playback;}catch(OperationCanceledException){}playing.Dispose();}}
    }
    private async Task ObservePlayback(Task playback,CancellationToken token){try{await playback;}catch(OperationCanceledException)when(token.IsCancellationRequested){}catch(Exception ex){reportFault?.Invoke("音频播放设备失败："+ex.Message);}}
    public async Task StopAsync() {
        CancellationTokenSource? life;Task? capture,output,camera;
        lock(gate){life=lifetime;lifetime=null;capture=captureTask;output=outputTask;camera=cameraTask;cameraTask=null;cameraInput=null;cameraFrames.Clear();captureTask=null;outputTask=null;audioInput=null;audioOutput=null;outputUpdates?.Writer.TryComplete();outputUpdates=null;pending.Clear();}
        if(life==null)return;await life.CancelAsync();
        try{await Task.WhenAll(new[]{capture,output,camera}.OfType<Task>());}catch(OperationCanceledException){}finally{life.Dispose();}
    }
    public async ValueTask DisposeAsync()=>await StopAsync();
}
