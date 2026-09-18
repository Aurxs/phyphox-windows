// SPDX-License-Identifier: GPL-3.0-only
using Phyphox.Camera;
using Phyphox.Media;
namespace Phyphox.Server;
public static class MediaEndpoints
{
    public static void MapMediaEndpoints(this WebApplication app)
    {
        app.MapGet("/api/v1/media/camera/backend", () => new WindowsCameraService().NativeBackendInfo());
        app.MapGet("/api/v1/media/audio/devices", () => new { items = new WasapiAudioService().EnumerateDevices(), hardwareVerified = false });
        app.MapPost("/api/v1/media/camera/probe", async (CameraProbeRequest request, CancellationToken ct) =>
        {
            if (request.MaximumIndex is < 1 or > 8) throw new InvalidOperationException("相机探测范围必须是 1..8。");
            return new { items = await new WindowsCameraService().ProbeAsync(request.MaximumIndex, ct), note = "显式探测会短暂打开相机；索引不是跨电脑稳定身份。", hardwareVerified = false };
        });
        app.MapPost("/api/v1/session/media/camera/configure", (CameraExperimentProfile profile, SessionService session) => session.ConfigureCamera(profile));
        app.MapGet("/api/v1/session/media/camera/frame", (SessionService session) =>
        {
            var frame = session.GetCameraFrame();
            return frame is null ? Results.NotFound(new { error = "尚无真实相机帧。" }) : Results.File(frame.JpegPreview, "image/jpeg");
        });
    }
    public sealed record CameraProbeRequest(int MaximumIndex = 4);
}
