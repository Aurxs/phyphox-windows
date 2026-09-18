using Phyphox.Media;
static void Assert(bool condition,string message){if(!condition)throw new Exception(message);}
var decoded=PcmDecoder.DecodeMono([0,128,255,127],2,16,false);
Assert(decoded.Length==1 && Math.Abs(decoded[0]+1/65536f)<1e-8,"PCM stereo downmix");
Assert(PcmDecoder.DecodeMono([0,0,128],1,24,false)[0]==-1,"PCM 24-bit signed");
Assert(PcmDecoder.DecodeMono([0,0,128,63],1,32,true)[0]==1,"IEEE float input");
var direct=new AudioMixer(new(MonoSamples:[.25f,-.5f]));var output=new float[8];
Assert(direct.Read(output)==4 && output.Take(4).SequenceEqual(new[]{.25f,.25f,-.5f,-.5f}),"direct mono duplicated stereo");
Assert(direct.Read(output)==0,"finite output ends");
var loop=new AudioMixer(new(MonoSamples:[.5f],Loop:true));Assert(loop.Read(output)==8 && output.All(x=>x==.5f),"explicit loop");
var tone=new AudioMixer(new(SampleRate:8000,Tones:[new(Frequency:2000,Pan:1,DurationSeconds:.001)]));
tone.Read(output);Assert(output[0]==0 && output[1]==0 && output[2]==0 && Math.Abs(output[3]-1)<1e-6,"lookup tone and right pan");
var noise=new AudioMixer(new(SampleRate:8000,Noise:[new(Pan:-1,DurationSeconds:.001)]),7);noise.Read(output);Assert(output.Where((x,i)=>i%2==1).All(x=>x==0),"noise left pan");
if(!OperatingSystem.IsWindows()){var service=new WasapiAudioService();Assert(!service.IsAvailable,"platform capability");try{service.EnumerateDevices();throw new Exception("must not fabricate audio devices");}catch(PlatformNotSupportedException){}}
Console.WriteLine("Audio PCM/mixer tests passed. No Windows capture/playback or hardware tests executed.");
