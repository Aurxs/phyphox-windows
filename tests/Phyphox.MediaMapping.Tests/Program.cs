using System.Xml.Linq;
using Phyphox.Server;
using Phyphox.Camera;
using Phyphox.Core;
static void Assert(bool condition,string message){if(!condition)throw new Exception(message);}
var roi=CameraAnalysis.ResolveRoi(new(.4,.4,.6,.6),1280,720);Assert(roi==new CameraRoi(512,288,256,144),"normalized ROI against actual dimensions");
var input=XElement.Parse("<camera><output component='hue'>h</output><output component='luma'>l</output><output component='t'>t</output></camera>");
Assert(MediaCoordinator.CameraCapabilityIssues(input,new(VerifiedAutoExposureMode:true),true).Count==0,"relative camera supported without fabricating exposure");
Assert(MediaCoordinator.CameraCapabilityIssues(input,new(VerifiedAutoExposureMode:true),false).Any(x=>x.Contains("时钟")),"clock required");
var analysis=CameraAnalysis.AnalyzeBgr([0,255,0],1,1,3);
var frame=new CameraFrame(0,1,1,30,0,DateTimeOffset.UtcNow,[],analysis,[]);
var mapped=MediaCoordinator.MapCameraFrame(input,frame,2.5,null);Assert(mapped.Replace.Count==0&&mapped.Append["t"][0]==2.5&&Math.Abs(mapped.Append["h"][0]-120)<1e-10,"camera scalar/time map");
var physical=XElement.Parse("<camera auto_exposure='false' feature='spectroscopy'><output component='pixelPosition'>x</output><output component='luminance'>y</output></camera>");
Assert(MediaCoordinator.CameraCapabilityIssues(physical,new(),true).Any(x=>x.Contains("固定曝光")),"physical metadata not fabricated");
var metadata=new ExposureMetadata(1,100,1e9/60);var profile=new CameraExperimentProfile(Exposure:metadata,FixedExposureVerified:true,VerifiedAutoExposureMode:false);
Assert(MediaCoordinator.CameraCapabilityIssues(physical,profile,true).Count==0,"explicit calibrated profile accepted");
var calibrated=frame with {Analysis=CameraAnalysis.AnalyzeBgr([255,255,255],1,1,3,exposure:metadata)};
var spectrum=MediaCoordinator.MapCameraFrame(physical,calibrated,0,metadata);Assert(spectrum.Append.Count==0&&spectrum.Replace["y"][0]==1,"spectrum replaces containers");
await using var coordinator=new MediaCoordinator();
var assetRoot=Path.GetFullPath(args.FirstOrDefault() ?? "assets/experiments");
foreach(var name in new[]{"audio_scope","audio_spectrum","tone_generator"}) {
 var d=ExperimentParser.Parse(File.ReadAllText(Path.Combine(assetRoot,name+".phyphox")));var issues=coordinator.CapabilityIssues(d);
 Assert(issues.Count==(OperatingSystem.IsWindows()?0:1),"official audio mapping "+name+": "+string.Join(";",issues));
}
Console.WriteLine("Media mapping tests passed; no microphone, speaker, or camera activated.");
