using Phyphox.Camera;
static void Assert(bool value,string message){if(!value)throw new Exception(message);}
var red=CameraAnalysis.AnalyzeBgr([0,0,255],1,1,3);
Assert(Math.Abs(red.Luma-.2126)<1e-12 && Math.Abs(red.LinearLuminance-.2126)<1e-12,"Rec709 red");
Assert(red.HueDegrees==0 && red.Saturation==1 && red.ExposureCorrectedLuminance==null,"HSV and unknown physical exposure");
var green=CameraAnalysis.AnalyzeBgr([0,255,0],1,1,3);Assert(Math.Abs(green.HueDegrees-120)<1e-10,"hue degrees");
var gray=CameraAnalysis.AnalyzeBgr([128,128,128],1,1,3);Assert(Math.Abs(gray.LinearLuminance-.21586050011389926)<1e-12 && gray.Saturation==0,"sRGB transfer function");
var roi=CameraAnalysis.AnalyzeBgr([0,0,255,0,255,0,255,0,0,255,255,255],2,2,6,new(1,0,1,2));
Assert(roi.SpectrumPixelPositions.SequenceEqual(new double[]{1}) && Math.Abs(roi.LinearSpectrum[0]-(.7152+1)/2)<1e-12,"ROI column spectrum");
var corrected=CameraAnalysis.AnalyzeBgr([255,255,255],1,1,3,exposure:new(1,100,1e9/60));Assert(Math.Abs(corrected.ExposureCorrectedLuminance!.Value-1)<1e-12,"official exposure correction reference");
try{CameraAnalysis.AnalyzeBgr([0,0,0],1,1,3,new(0,0,2,1));throw new Exception("invalid ROI accepted");}catch(ArgumentException){}
if(!OperatingSystem.IsWindows()){var service=new WindowsCameraService();Assert(!service.IsAvailable,"platform guard");try{await service.ProbeAsync();throw new Exception("camera must not activate on Mac");}catch(PlatformNotSupportedException){}}
Console.WriteLine("Camera pure image-analysis tests passed. No camera was opened or displayed; native Windows capture remains unverified.");
