// SPDX-License-Identifier: GPL-3.0-only
namespace Phyphox.Server;
public static class BrowserAudioOutputEndpoints
{
    public static void MapBrowserAudioOutputEndpoints(this WebApplication app)
    {
        app.MapPost("/api/v1/session/media/browser/speaker/configure",(BrowserSpeakerConfiguration body,SessionService session)=>session.ConfigureBrowserSpeaker(body));
        app.MapPost("/api/v1/session/media/browser/speaker/pcm",(BrowserSpeakerPull body,SessionService session)=>session.PullBrowserSpeaker(body));
        app.MapPost("/api/v1/session/media/browser/speaker/stop",(BrowserSpeakerStop body,SessionService session,CancellationToken ct)=>session.StopBrowserSpeakerAsync(body,ct));
    }
}
