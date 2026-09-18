// SPDX-License-Identifier: GPL-3.0-only
using System.Diagnostics;
using System.Security.Cryptography;
using System.Xml.Linq;
using Phyphox.Core;
using Phyphox.Media;
namespace Phyphox.Server;

public sealed record BrowserSpeakerConfiguration(string SessionId,int OutputIndex);
public sealed record BrowserSpeakerPull(string SessionId,string StreamId,long Sequence);
public sealed record BrowserSpeakerStop(string SessionId,string StreamId);
public sealed partial class SessionService
{
    sealed class BrowserSpeaker(string owner,int index,XElement output)
    {
        public readonly string Id=Convert.ToHexString(RandomNumberGenerator.GetBytes(24));
        public readonly string Owner=owner;
        public readonly int Index=index;
        public readonly XElement Output=output;
        public long Sequence=-1,LastPull=Stopwatch.GetTimestamp(),Started=Stopwatch.GetTimestamp(),Frames;
        public AudioPlaybackRequest? Request;
        public AudioMixer? Mixer;
    }
    BrowserSpeaker? browserSpeaker;
    IReadOnlySet<int> BrowserAudioOutputIndices=>browserSpeaker is {} s?new HashSet<int>{s.Index}:new HashSet<int>();
    object[] BrowserAudioOutputSnapshot()=>browserSpeaker is {} s?[new {streamId=s.Id,outputIndex=s.Index,sampleRate=int.Parse(s.Output.Attr("rate","48000")),channels=2}]:[];
    void InvalidateBrowserAudioOutputLocked()=>browserSpeaker=null;
    void PauseBrowserAudioOutputLocked(){if(browserSpeaker is {} s){s.Mixer=null;s.Request=null;s.Frames=0;}}
    void StartBrowserAudioOutputLocked(){if(browserSpeaker is {} s){s.LastPull=s.Started=Stopwatch.GetTimestamp();s.Frames=0;s.Mixer=null;s.Request=null;}}
    void UpdateBrowserAudioOutputLocked()
    {
        if(browserSpeaker is not {} s||runtime?.State!="running")return;
        if(Stopwatch.GetElapsedTime(s.LastPull).TotalSeconds>3)throw new IOException("浏览器扬声器连接超过3秒无响应；实验已停止，请重新启用。");
        var next=MediaCoordinator.SnapshotOutput(s.Output,name=>runtime.Buffers[name].Values);
        if(!MediaCoordinator.Same(s.Request,next)) {s.Mixer=new AudioMixer(next);s.Request=next;}
    }
    public object ConfigureBrowserSpeaker(BrowserSpeakerConfiguration config)
    {
        lock(gate)
        {
            if(runtime==null||entry?.Item.Id!=config.SessionId)throw new InvalidOperationException("实验已经改变，请重新配置扬声器。");
            if(runtime.State=="running"||starting)throw new InvalidOperationException("请暂停实验后启用浏览器扬声器。");
            if(config.OutputIndex<0||config.OutputIndex>=runtime.Definition.Outputs.Count)throw new ArgumentException("无效音频输出索引。");
            var output=runtime.Definition.Outputs[config.OutputIndex];
            if(output.Name.LocalName!="audio")throw new ArgumentException("所选输出不是音频。");
            var issues=MediaCoordinator.AudioOutputCapabilityIssues(output,runtime.Buffers.Keys);
            if(issues.Count>0)throw new NotSupportedException(string.Join("；",issues));
            var sampleRate=int.Parse(output.Attr("rate","48000"));
            if(sampleRate is <8000 or >192000)throw new NotSupportedException("浏览器PCM输出采样率范围8–192kHz。");
            browserSpeaker=new(config.SessionId,config.OutputIndex,output);error=null;revision++;
            return new {streamId=browserSpeaker.Id,sampleRate,channels=2,maximumAheadSeconds=.2,timeoutSeconds=3,session=SnapshotUnsafe()};
        }
    }
    BrowserSpeaker SpeakerOwner(string owner,string id)
    {
        if(runtime==null||entry?.Item.Id!=owner||browserSpeaker is not {} s||s.Owner!=owner||s.Id!=id)throw new InvalidOperationException("扬声器流已失效；停止、清空或更换实验后请重新启用。");
        return s;
    }
    public object PullBrowserSpeaker(BrowserSpeakerPull packet)
    {
        lock(gate)
        {
            var s=SpeakerOwner(packet.SessionId,packet.StreamId);
            if(packet.Sequence!=s.Sequence+1)throw new ArgumentException("PCM请求序号必须连续且不可重放。");
            s.Sequence=packet.Sequence;s.LastPull=Stopwatch.GetTimestamp();
            int rate=int.Parse(s.Output.Attr("rate","48000"));
            if(runtime!.State!="running")return new {streamId=s.Id,sequence=s.Sequence,sampleRate=rate,channels=2,running=false,samples=Array.Empty<float>()};
            UpdateBrowserAudioOutputLocked();
            int frames=rate/10;
            if(s.Frames+frames>(Stopwatch.GetElapsedTime(s.Started).TotalSeconds+.2)*rate)return new {streamId=s.Id,sequence=s.Sequence,sampleRate=rate,channels=2,running=true,samples=Array.Empty<float>()};
            var samples=new float[frames*2];s.Mixer!.Read(samples);s.Frames+=frames;
            return new {streamId=s.Id,sequence=s.Sequence,sampleRate=rate,channels=2,running=true,samples};
        }
    }
    public async Task<object> StopBrowserSpeakerAsync(BrowserSpeakerStop packet,CancellationToken ct)
    {
        await commandGate.WaitAsync(ct);
        try
        {
            lock(gate)SpeakerOwner(packet.SessionId,packet.StreamId);
            await StopAcquisitionAsync();
            return Command(new("stop"));
        }
        finally {commandGate.Release();}
    }
}
