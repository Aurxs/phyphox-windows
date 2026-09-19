// SPDX-License-Identifier: GPL-3.0-only
using System.Diagnostics;
using System.Security.Cryptography;
using System.Xml.Linq;
using System.Text.Json.Serialization;
using Phyphox.Core;
namespace Phyphox.Server;

public sealed record PhoneMotionConfiguration(string SessionId,int InputIndex,string Kind);
public sealed record PhoneMotionSample([property:JsonRequired] double T,[property:JsonRequired] double X,[property:JsonRequired] double Y,[property:JsonRequired] double Z);
public sealed record PhoneMotionPacket(string SessionId,string CaptureId,string RunId,long Sequence,PhoneMotionSample[] Samples);
public sealed record PhoneMotionStop(string SessionId,string CaptureId);
public sealed partial class SessionService
{
    sealed class PhoneCapture(PhoneMotionConfiguration configuration,XElement input)
    {
        public readonly string Id=PhoneToken();
        public readonly PhoneMotionConfiguration Configuration=configuration;
        public readonly XElement Input=input;
        public long Sequence=-1, LastReceived, ReceivedCount, RateOrigin=Stopwatch.GetTimestamp();
        public double LastTime=-1, Origin=double.NaN, ExperimentOrigin;
    }
    static string PhoneToken()=>Convert.ToHexString(RandomNumberGenerator.GetBytes(24));
    string phoneRunId=PhoneToken();
    readonly Dictionary<string,PhoneCapture> phoneCaptures=[];
    void RotatePhoneRun()
    {
        phoneRunId=PhoneToken();
        foreach(var capture in phoneCaptures.Values){capture.Sequence=-1;capture.LastTime=-1;capture.Origin=double.NaN;capture.ReceivedCount=0;capture.RateOrigin=Stopwatch.GetTimestamp();}
        foreach(var capture in browserCaptures.Values.Where(c=>c.Configuration.Source=="phone")){capture.Sequence=-1;capture.Pending.Clear();capture.SampleCount=0;capture.LastFrame=0;capture.Started=Stopwatch.GetTimestamp();}
    }
    void InvalidatePhoneInputs(){phoneCaptures.Clear();RotatePhoneRun();}
    bool PhoneReady(PhoneCapture c)=>c.LastReceived!=0&&Stopwatch.GetElapsedTime(c.LastReceived).TotalSeconds<=5;
    bool PhoneInputReady(int index)=>phoneCaptures.Values.Any(c=>c.Configuration.InputIndex==index&&PhoneReady(c));
    object[] PhoneMotionSnapshot()=>phoneCaptures.Values.Select(c=>(object)new{captureId=c.Id,inputIndex=c.Configuration.InputIndex,kind=c.Configuration.Kind,runId=phoneRunId,ready=PhoneReady(c),timeQuality="relative-segment",units=c.Configuration.Kind=="gyroscope"?"rad/s":"m/s²"}).ToArray();
    public object ConfigurePhoneMotion(PhoneMotionConfiguration config)
    {
        lock(gate)
        {
            if(runtime==null||entry?.Item.Id!=config.SessionId)throw new InvalidOperationException("手机目标实验已改变。");
            if(runtime.State=="running"||starting)throw new InvalidOperationException("请先暂停测量。");
            if(config.InputIndex<0||config.InputIndex>=runtime.Definition.Inputs.Count)throw new ArgumentException("无效输入槽位。");
            var input=runtime.Definition.Inputs[config.InputIndex];
            if(config.Kind is not("accelerometer" or "gyroscope" or "linear_acceleration")||input.Name.LocalName!="sensor"||input.Attr("type")!=config.Kind)throw new NotSupportedException("手机运动类型与实验输入不匹配。");
            if(input.Attributes().Any(a=>a.Name.LocalName is not("type" or "rate" or "average"))||input.Attr("rate","0")!="0"||input.Attr("average","false")!="false")throw new NotSupportedException("首版手机运动仅支持原始零/默认速率，不实现指定速率、平均或校准属性。");
            var mappings=input.Elements().ToArray();
            if(mappings.Length==0||mappings.Any(e=>e.Name.LocalName!="output"||BrowserMapping(e) is not("x" or "y" or "z" or "t" or "abs")||e.Attributes().Any(a=>a.Name.LocalName is not("as" or "component"))||!runtime.Buffers.ContainsKey(e.Value.Trim()))||mappings.Select(e=>e.Value.Trim()).Distinct().Count()!=mappings.Length)throw new NotSupportedException("手机运动只支持有效且唯一的x/y/z/t/abs输出，不提供accuracy。");
            var targets=mappings.Select(e=>e.Value.Trim()).ToHashSet();
            if(bindings.Any(b=>b.Definition.InputIndex==config.InputIndex||b.Definition.Mappings.Any(m=>targets.Contains(m.Buffer))||targets.Contains(b.Definition.TimeBuffer??""))||bleInputs.Bindings.Any(b=>b.InputIndex==config.InputIndex))throw new InvalidOperationException("输入已有设备绑定，请先解除绑定。");
            if(phoneCaptures.Values.Any(c=>c.Configuration.InputIndex!=config.InputIndex&&c.Input.Elements().Any(e=>targets.Contains(e.Value.Trim())))||browserCaptures.Values.Any(c=>c.Input.Elements().Any(e=>targets.Contains(e.Value.Trim()))))throw new InvalidOperationException("手机输入不能共享写入容器。");
            foreach(var old in phoneCaptures.Values.Where(c=>c.Configuration.InputIndex==config.InputIndex).ToArray())phoneCaptures.Remove(old.Id);
            var capture=new PhoneCapture(config,input);phoneCaptures.Add(capture.Id,capture);error=null;revision++;
            return new{captureId=capture.Id,runId=phoneRunId,session=SnapshotUnsafe(),maximumSamples=128,timeoutSeconds=5,timeQuality="relative-segment"};
        }
    }
    public object ReceivePhoneMotion(PhoneMotionPacket packet)
    {
        lock(gate)
        {
            if(entry?.Item.Id!=packet.SessionId||!phoneCaptures.TryGetValue(packet.CaptureId,out var c)||packet.RunId!=phoneRunId)throw new InvalidOperationException("手机采集或运行令牌已失效。");
            try
            {
            if(packet.Sequence<0||packet.Sequence!=c.Sequence+1)throw new ArgumentException("手机运动序号缺失、重复或倒序。");
            if(packet.Samples is not{Length:>0 and <=128})throw new ArgumentException("手机运动每批需要1至128个样本。");
            if(c.ReceivedCount+packet.Samples.Length>(Stopwatch.GetElapsedTime(c.RateOrigin).TotalSeconds+1)*500)throw new ArgumentException("手机运动输入超过500Hz预算。");
            double previous=c.LastTime;
            foreach(var s in packet.Samples){if(s==null||!double.IsFinite(s.T)||s.T<0||s.T<=previous||!double.IsFinite(s.X)||!double.IsFinite(s.Y)||!double.IsFinite(s.Z)||Math.Abs(s.X)>1e6||Math.Abs(s.Y)>1e6||Math.Abs(s.Z)>1e6)throw new ArgumentException("手机运动必须为有限SI单位数值及严格递增的单调秒时间。");previous=s.T;}
            if(packet.Samples[^1].T-packet.Samples[0].T>1)throw new ArgumentException("手机运动批次不能跨越超过1秒。");
            if(runtime?.State=="running")
            {
                if(double.IsNaN(c.Origin)){c.Origin=packet.Samples[0].T;c.ExperimentOrigin=runtime.ExperimentTime;}
                if(c.ExperimentOrigin+packet.Samples[^1].T-c.Origin>runtime.ExperimentTime+1)throw new ArgumentException("手机采样时间超前，不能加速注入。");
                var writes=c.Input.Elements().ToDictionary(e=>e.Value.Trim(),e=>packet.Samples.Select(s=>BrowserMapping(e) switch{"x"=>s.X,"y"=>s.Y,"z"=>s.Z,"abs"=>Math.Sqrt(s.X*s.X+s.Y*s.Y+s.Z*s.Z),"t"=>c.ExperimentOrigin+s.T-c.Origin,_=>throw new InvalidOperationException()}).ToArray());
                Record(new(){Sequence=0,Kind="input",Buffers=writes,SourceMetadata=new(){["source"]="phone-browser",["sensor"]=c.Configuration.Kind,["timeQuality"]="relative-segment",["units"]=c.Configuration.Kind=="gyroscope"?"rad/s":"m/s²",["axes"]="device",["hardwareVerified"]="false"}});
                foreach(var (name,values) in writes)runtime.ReceiveSamples(name,values,true);
                revision++;
            }
            c.ReceivedCount+=packet.Samples.Length;c.Sequence=packet.Sequence;c.LastTime=previous;c.LastReceived=Stopwatch.GetTimestamp();
            return new{accepted=true,sequence=packet.Sequence};
            }
            catch(Exception ex){if(runtime?.State=="running")MediaFault("手机运动输入失败："+ex.Message);throw;}
        }
    }
    void CheckPhoneFreshness(){if(phoneCaptures.Values.Any(c=>!PhoneReady(c)))throw new IOException("手机运动超过5秒无真实数据，测量已停止。");}
    public async Task StopPhoneBridgeAsync(CancellationToken ct=default)
    {
        await commandGate.WaitAsync(ct);
        try
        {
            bool stop;
            lock(gate)
            {
                var hasPhone=phoneCaptures.Count>0||browserCaptures.Values.Any(c=>c.Configuration.Source=="phone");
                stop=hasPhone&&(runtime?.State=="running"||starting);
                if(runtime?.State=="running")FlushBrowserMedia(false);
                InvalidatePhoneInputs();
                foreach(var c in browserCaptures.Values.Where(c=>c.Configuration.Source=="phone").ToArray())browserCaptures.Remove(c.Id);
                revision++;
            }
            if(stop){await StopAcquisitionAsync();Command(new("stop"));}
        }
        finally{commandGate.Release();}
    }
    public async Task<object> StopPhoneMotionAsync(PhoneMotionStop request,CancellationToken ct)
    {
        await commandGate.WaitAsync(ct);
        try{lock(gate){if(entry?.Item.Id!=request.SessionId||!phoneCaptures.ContainsKey(request.CaptureId))throw new InvalidOperationException("手机采集已经失效。");}await StopAcquisitionAsync();return Command(new("stop"));}finally{commandGate.Release();}
    }
}
