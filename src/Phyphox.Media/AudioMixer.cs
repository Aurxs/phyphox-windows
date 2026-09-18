namespace Phyphox.Media;

/// <summary>Service-side stereo generator following Android AudioOutput's direct/4096-entry tone/noise mixing and linear pan.</summary>
public sealed class AudioMixer
{
    private const int LookupSize=4096;
    private static readonly float[] Sine=Enumerable.Range(0,LookupSize).Select(i=>(float)Math.Sin(2*Math.PI*i/LookupSize)).ToArray();
    private readonly AudioPlaybackRequest request;
    private readonly double[] phases;
    private readonly Random random;
    private readonly long totalFrames;
    private long frameIndex;
    public int SampleRate=>request.SampleRate;
    public AudioMixer(AudioPlaybackRequest request,int? testNoiseSeed=null) {
        if(request.SampleRate is <8000 or >384000)throw new ArgumentOutOfRangeException(nameof(request.SampleRate));
        var tones=request.Tones??[];var noise=request.Noise??[];
        if(tones.Any(x=>!double.IsFinite(x.Frequency)||x.Frequency<0||x.Frequency>request.SampleRate/2d||!double.IsFinite(x.DurationSeconds)||x.DurationSeconds<0||x.DurationSeconds>86400))throw new ArgumentException("Invalid tone frequency/duration.");
        if(noise.Any(x=>!double.IsFinite(x.DurationSeconds)||x.DurationSeconds<0||x.DurationSeconds>86400))throw new ArgumentException("Invalid noise duration.");
        this.request=request with {MonoSamples=request.MonoSamples?.ToArray(),Tones=tones.ToArray(),Noise=noise.ToArray()};
        phases=new double[tones.Length];random=testNoiseSeed.HasValue?new Random(testNoiseSeed.Value):Random.Shared;
        totalFrames=Math.Max(request.MonoSamples?.LongLength??0,tones.Select(x=>(long)(x.DurationSeconds*request.SampleRate)).Concat(noise.Select(x=>(long)(x.DurationSeconds*request.SampleRate))).DefaultIfEmpty(0).Max());
        if(totalFrames==0)throw new ArgumentException("At least one non-empty audio source is required.");
    }
    /// <returns>Number of interleaved stereo samples written; zero indicates finite playback is complete.</returns>
    public int Read(Span<float> destination) {
        int frames=destination.Length/2;
        if(!request.Loop)frames=(int)Math.Min(frames,Math.Max(0,totalFrames-frameIndex));
        destination.Clear();
        double amplitude=request.MonoSamples is {Length:>0}?1:0;
        foreach(var tone in request.Tones!)amplitude+=Finite(tone.Amplitude);
        foreach(var noise in request.Noise!)amplitude+=Finite(noise.Amplitude);
        var factor=request.Normalize && amplitude>0?1/amplitude:1;
        for(int i=0;i<frames;i++) {
            long index=frameIndex+i;double left=0,right=0;
            if(request.MonoSamples is {Length:>0} data && (request.Loop || index<data.Length)) {left=right=Finite(data[(int)(index%data.Length)]);}
            for(int t=0;t<request.Tones!.Length;t++) {
                var tone=request.Tones[t];if(!request.Loop && index>=(long)(tone.DurationSeconds*SampleRate))continue;
                int lookup=(int)(phases[t]*LookupSize)%LookupSize;
                double value=Finite(tone.Amplitude)*(tone.Waveform switch {AudioWaveform.Square=>2*lookup>LookupSize?1:-1,AudioWaveform.Sawtooth=>2d*lookup/LookupSize-1,_=>Sine[lookup]});
                AddPan(ref left,ref right,value,tone.Pan);phases[t]+=tone.Frequency/SampleRate;if(phases[t]>100000)phases[t]-=100000;
            }
            foreach(var noise in request.Noise!) {
                if(!request.Loop && index>=(long)(noise.DurationSeconds*SampleRate))continue;
                AddPan(ref left,ref right,Finite(noise.Amplitude)*(2*random.NextDouble()-1),noise.Pan);
            }
            destination[2*i]=(float)Math.Clamp(left*factor,-1,1);destination[2*i+1]=(float)Math.Clamp(right*factor,-1,1);
        }
        frameIndex+=frames;return frames*2;
    }
    private static double Finite(double value)=>double.IsFinite(value)?value:0;
    private static void AddPan(ref double left,ref double right,double value,double pan) {
        double p=Finite(pan);left+=(p>0?1-p:1)*value;right+=(p<0?1+p:1)*value;
    }
}
