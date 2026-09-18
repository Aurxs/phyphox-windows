using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Threading.Channels;
using NAudio.CoreAudioApi;
using NAudio.Wave;
namespace Phyphox.Media;

public sealed class WasapiAudioService
{
    public bool IsAvailable=>OperatingSystem.IsWindows();
    private void RequireWindows(){if(!IsAvailable)throw new PlatformNotSupportedException("WASAPI requires Windows; browser microphone and simulated audio are not substituted.");}
    public IReadOnlyList<AudioDeviceDescriptor> EnumerateDevices() {
        RequireWindows();using var enumerator=new MMDeviceEnumerator();var result=new List<AudioDeviceDescriptor>();
        foreach(var flow in new[]{DataFlow.Capture,DataFlow.Render}) {
            string? defaultId=null;
            try {using var device=enumerator.GetDefaultAudioEndpoint(flow,Role.Multimedia);defaultId=device.ID;}catch(System.Runtime.InteropServices.COMException){}
            foreach(var device in enumerator.EnumerateAudioEndPoints(flow,DeviceState.Active)) {
                using(device)result.Add(new(device.ID,device.FriendlyName,flow==DataFlow.Capture,device.ID==defaultId));
            }
        }
        return result;
    }
    public async IAsyncEnumerable<AudioSampleBatch> CaptureAsync(string? deviceId=null,[EnumeratorCancellation] CancellationToken cancellationToken=default) {
        RequireWindows();cancellationToken.ThrowIfCancellationRequested();
        using var enumerator=new MMDeviceEnumerator();
        using var device=string.IsNullOrEmpty(deviceId)?enumerator.GetDefaultAudioEndpoint(DataFlow.Capture,Role.Multimedia):enumerator.GetDevice(deviceId);
        if(device.DataFlow!=DataFlow.Capture)throw new ArgumentException("Device is not an audio capture endpoint.");
        using var capture=new WasapiCapture(device,true,50);
        var format=capture.WaveFormat;
        bool ieeeFloat=format.Encoding==WaveFormatEncoding.IeeeFloat || format is WaveFormatExtensible ext && ext.SubFormat==new Guid("00000003-0000-0010-8000-00aa00389b71");
        bool pcm=format.Encoding==WaveFormatEncoding.Pcm || format is WaveFormatExtensible pcmExt && pcmExt.SubFormat==new Guid("00000001-0000-0010-8000-00aa00389b71");
        if(!ieeeFloat && !pcm)throw new NotSupportedException($"Unsupported audio mix encoding: {format.Encoding}.");
        var batches=Channel.CreateBounded<AudioSampleBatch>(new BoundedChannelOptions(64){SingleReader=true,SingleWriter=true,FullMode=BoundedChannelFullMode.Wait});
        long sampleIndex=0;
        capture.DataAvailable+=(sender,args)=> {
            try {
                long received=Stopwatch.GetTimestamp();var at=DateTimeOffset.UtcNow;
                var mono=PcmDecoder.DecodeMono(args.Buffer.AsSpan(0,args.BytesRecorded),format.Channels,format.BitsPerSample,ieeeFloat);
                var batch=new AudioSampleBatch(mono,format.SampleRate,sampleIndex,received,at);sampleIndex+=mono.Length;
                if(!batches.Writer.TryWrite(batch))batches.Writer.TryComplete(new IOException("Audio capture queue overflow; samples were not silently discarded. Restart acquisition."));
            }catch(Exception ex){batches.Writer.TryComplete(ex);}
        };
        capture.RecordingStopped+=(sender,args)=>batches.Writer.TryComplete(args.Exception);
        capture.StartRecording();
        try {await foreach(var batch in batches.Reader.ReadAllAsync(cancellationToken))yield return batch;}
        finally {capture.StopRecording();batches.Writer.TryComplete();}
    }
    public async Task<AudioPlaybackResult> PlayAsync(AudioPlaybackRequest request,CancellationToken cancellationToken=default) {
        RequireWindows();cancellationToken.ThrowIfCancellationRequested();
        var mixer=new AudioMixer(request);
        using var enumerator=new MMDeviceEnumerator();
        using var device=string.IsNullOrEmpty(request.DeviceId)?enumerator.GetDefaultAudioEndpoint(DataFlow.Render,Role.Multimedia):enumerator.GetDevice(request.DeviceId);
        if(device.DataFlow!=DataFlow.Render)throw new ArgumentException("Device is not an audio render endpoint.");
        using var client=device.AudioClient;var mix=client.MixFormat;
        var result=new AudioPlaybackResult(request.SampleRate,mix.SampleRate,mix.Channels);
        using var output=new WasapiOut(device,AudioClientShareMode.Shared,true,100);
        var finished=new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        output.PlaybackStopped+=(sender,args)=> {if(args.Exception!=null)finished.TrySetException(args.Exception);else finished.TrySetResult();};
        output.Init(new MixerProvider(mixer));output.Play();
        try {await finished.Task.WaitAsync(cancellationToken);return result;}
        finally {output.Stop();}
    }
    private sealed class MixerProvider : WaveProvider32
    {
        private readonly AudioMixer mixer;
        public MixerProvider(AudioMixer mixer):base(mixer.SampleRate,2){this.mixer=mixer;}
        public override int Read(float[] buffer,int offset,int sampleCount)=>mixer.Read(buffer.AsSpan(offset,sampleCount));
    }
}
