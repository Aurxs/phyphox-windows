using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Threading.Channels;
using OpenCvSharp;
namespace Phyphox.Camera;

public sealed class WindowsCameraService
{
    private readonly SemaphoreSlim access=new(1,1);
    public bool IsAvailable=>OperatingSystem.IsWindows();
    private void RequireWindows(){if(!IsAvailable)throw new PlatformNotSupportedException("Camera capture requires Windows Media Foundation and the native Windows camera package. No browser camera or depth substitute is used.");}
    /// <summary>Loads native code only. Does not enumerate or open any camera.</summary>
    public object NativeBackendInfo(){RequireWindows();return new { version=Cv2.GetVersionString(),buildInformation=Cv2.GetBuildInformation(),backend="MSMF",deviceOpened=false,hardwareVerified=false };}
    /// <summary>Opt-in probing opens devices and can illuminate camera indicators. Index identity is not stable across hardware changes.</summary>
    public async Task<IReadOnlyList<CameraDescriptor>> ProbeAsync(int maximumIndex=4,CancellationToken cancellationToken=default) {
        RequireWindows();if(maximumIndex is <1 or >16)throw new ArgumentOutOfRangeException(nameof(maximumIndex));
        await access.WaitAsync(cancellationToken);
        try {
            return await Task.Run<IReadOnlyList<CameraDescriptor>>(()=> {
                var devices=new List<CameraDescriptor>();
                for(int index=0;index<maximumIndex;index++) {
                    cancellationToken.ThrowIfCancellationRequested();using var camera=new VideoCapture(index,VideoCaptureAPIs.MSMF);
                    if(!camera.IsOpened())continue;
                    using var frame=new Mat();if(!camera.Read(frame)||frame.Empty())continue;
                    devices.Add(new(index,$"Media Foundation camera {index}",frame.Width,frame.Height,ReportedFps(camera)));
                }
                return devices;
            },cancellationToken);
        } finally {access.Release();}
    }
    public async IAsyncEnumerable<CameraFrame> CaptureAsync(CameraCaptureRequest request,[EnumeratorCancellation] CancellationToken cancellationToken=default) {
        RequireWindows();Validate(request);await access.WaitAsync(cancellationToken);
        using var lifetime=CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        var frames=Channel.CreateBounded<CameraFrame>(new BoundedChannelOptions(4){SingleReader=true,SingleWriter=true,FullMode=BoundedChannelFullMode.Wait});
        var worker=Task.Run(()=> {
            try {CaptureLoop(request,frames.Writer,lifetime.Token);frames.Writer.TryComplete();}
            catch(Exception error){frames.Writer.TryComplete(error);}
            finally {access.Release();}
        });
        try {await foreach(var frame in frames.Reader.ReadAllAsync(cancellationToken))yield return frame;}
        finally {
            await lifetime.CancelAsync();
            // Do not dispose a VideoCapture concurrently with a blocked native Read. Its worker owns it until it returns.
            await worker.WaitAsync(TimeSpan.FromSeconds(5));
        }
    }
    private static void Validate(CameraCaptureRequest request) {
        if(request.Index is <0 or >255||request.Width is <1 or >4096||request.Height is <1 or >4096||!double.IsFinite(request.FramesPerSecond)||request.FramesPerSecond is <=0 or >240||request.JpegQuality is <1 or >100)throw new ArgumentException("Invalid camera request.");
        if(request.Controls?.Any(x=>!Enum.IsDefined(x.Control)||!double.IsFinite(x.Value))==true)throw new ArgumentException("Invalid camera control.");
    }
    private static double ReportedFps(VideoCapture camera){var fps=camera.Get(VideoCaptureProperties.Fps);return double.IsFinite(fps)&&fps>0?fps:0;}
    private static void CaptureLoop(CameraCaptureRequest request,ChannelWriter<CameraFrame> writer,CancellationToken cancellationToken) {
        using var camera=new VideoCapture(request.Index,VideoCaptureAPIs.MSMF);
        if(!camera.IsOpened())throw new IOException("Media Foundation could not open the selected camera; check permission, device availability and native dependencies.");
        camera.Set(VideoCaptureProperties.FrameWidth,request.Width);camera.Set(VideoCaptureProperties.FrameHeight,request.Height);camera.Set(VideoCaptureProperties.Fps,request.FramesPerSecond);
        var controls=new List<CameraControlResult>();
        foreach(var change in request.Controls??[]) {
            var property=(VideoCaptureProperties)(int)change.Control;double before=camera.Get(property);bool accepted=camera.Set(property,change.Value);double actual=camera.Get(property);
            // OpenCV 4.13 MSMF readComplexPropery returns GetRange default values, not current state.
            string status=!accepted?"unsupported-or-rejected":"backend-default-only-unverified";
            controls.Add(new(change.Control,change.Value,before,actual,accepted,status));
            if(change.Required)throw new NotSupportedException($"Required camera control {change.Control}: {status}.");
        }
        using var raw=new Mat();long sequence=0;
        while(!cancellationToken.IsCancellationRequested) {
            if(!camera.Read(raw)||raw.Empty())throw new IOException("Camera capture stopped or returned an empty frame.");
            long timestamp=Stopwatch.GetTimestamp();var receivedAt=DateTimeOffset.UtcNow;
            cancellationToken.ThrowIfCancellationRequested();
            using var bgr=new Mat();
            if(raw.Type()==MatType.CV_8UC3)raw.CopyTo(bgr);
            else if(raw.Type()==MatType.CV_8UC4)Cv2.CvtColor(raw,bgr,ColorConversionCodes.BGRA2BGR);
            else if(raw.Type()==MatType.CV_8UC1)Cv2.CvtColor(raw,bgr,ColorConversionCodes.GRAY2BGR);
            else throw new NotSupportedException($"Unsupported camera pixel type {raw.Type()}.");
            int stride=checked((int)bgr.Step());var pixels=new byte[checked(stride*bgr.Height)];Marshal.Copy(bgr.Data,pixels,0,pixels.Length);
            var analysis=CameraAnalysis.AnalyzeBgr(pixels,bgr.Width,bgr.Height,stride,request.NormalizedRoi==null?request.Roi:CameraAnalysis.ResolveRoi(request.NormalizedRoi,bgr.Width,bgr.Height),request.SpectrumAxis,request.Exposure);
            Cv2.ImEncode(".jpg",bgr,out var preview,new ImageEncodingParam(ImwriteFlags.JpegQuality,request.JpegQuality));
            if(preview.Length==0)throw new IOException("Camera preview JPEG encoding failed.");
            var frame=new CameraFrame(sequence++,bgr.Width,bgr.Height,ReportedFps(camera),timestamp,receivedAt,preview,analysis,controls);
            if(!writer.TryWrite(frame))throw new IOException("Camera analysis queue overflow. Capture stopped; frames are not silently discarded.");
        }
    }
}
