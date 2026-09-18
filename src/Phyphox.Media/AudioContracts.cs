namespace Phyphox.Media;

public sealed record AudioDeviceDescriptor(string Id,string Name,bool IsInput,bool IsDefault);
public sealed record AudioSampleBatch(float[] Samples,int SampleRate,long FirstSampleIndex,long ReceivedTimestamp,DateTimeOffset ReceivedAt);
public enum AudioWaveform { Sine,Square,Sawtooth }
public sealed record AudioTone(double Frequency=440,double Amplitude=1,double Pan=0,double DurationSeconds=1,AudioWaveform Waveform=AudioWaveform.Sine);
public sealed record AudioNoise(double Amplitude=1,double Pan=0,double DurationSeconds=1);
public sealed record AudioPlaybackRequest(string? DeviceId=null,int SampleRate=48000,float[]? MonoSamples=null,bool Loop=false,bool Normalize=true,AudioTone[]? Tones=null,AudioNoise[]? Noise=null);
public sealed record AudioPlaybackResult(int SourceSampleRate,int DeviceMixSampleRate,int DeviceMixChannels);
