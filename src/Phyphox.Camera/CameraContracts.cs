namespace Phyphox.Camera;

public sealed record CameraDescriptor(int Index,string Name,int Width,int Height,double ReportedFramesPerSecond);
public sealed record CameraRoi(int X,int Y,int Width,int Height);
public sealed record NormalizedCameraRoi(double X1,double Y1,double X2,double Y2);
public enum SpectrumAxis { Horizontal,Vertical }
// Numeric values are OpenCV CAP_PROP identifiers; units are backend/device specific, never interpreted as physical exposure seconds.
public enum CameraControl { Brightness=10,Contrast=11,Saturation=12,Hue=13,Gain=14,Exposure=15,AutoExposure=21,Focus=28,AutoFocus=39,AutoWhiteBalance=44,WhiteBalanceTemperature=45 }
public sealed record CameraControlRequest(CameraControl Control,double Value,bool Required=false);
public sealed record CameraControlResult(CameraControl Control,double Requested,double Before,double Actual,bool SetAccepted,string Status);
/// <summary>Explicit metadata matching official Android exposure formula, not OpenCV exposure property values.</summary>
public sealed record ExposureMetadata(double ApertureValue,double Iso,double ShutterNanoseconds);
public sealed record CameraCaptureRequest(int Index=0,int Width=640,int Height=480,double FramesPerSecond=30,CameraRoi? Roi=null,SpectrumAxis SpectrumAxis=SpectrumAxis.Horizontal,CameraControlRequest[]? Controls=null,ExposureMetadata? Exposure=null,int JpegQuality=75,NormalizedCameraRoi? NormalizedRoi=null);
public sealed record CameraAnalysisResult(double Red,double Green,double Blue,double Luma,double LinearLuminance,double HueDegrees,double Saturation,double Value,double? ExposureCorrectedLuminance,double[] SpectrumPixelPositions,double[] LinearSpectrum,double[]? ExposureCorrectedSpectrum);
public sealed record CameraFrame(long Sequence,int Width,int Height,double ReportedFramesPerSecond,long ReceivedTimestamp,DateTimeOffset ReceivedAt,byte[] JpegPreview,CameraAnalysisResult Analysis,IReadOnlyList<CameraControlResult> Controls);
