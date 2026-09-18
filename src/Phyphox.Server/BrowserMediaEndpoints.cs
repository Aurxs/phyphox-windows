// SPDX-License-Identifier: GPL-3.0-only
using System.Text.Json;
namespace Phyphox.Server;

public static class BrowserMediaEndpoints
{
    // One upload may be decoded at a time: excess concurrent uploads are rejected, never queued.
    static readonly SemaphoreSlim upload = new(1,1);
    public static void MapBrowserMediaEndpoints(this WebApplication app)
    {
        app.MapPost("/api/v1/session/media/browser/configure", async (HttpRequest request,SessionService session,CancellationToken ct)=>
            session.ConfigureBrowserMedia(await Read<BrowserMediaConfiguration>(request,4096,ct)));
        app.MapPost("/api/v1/session/media/browser/stop", async (HttpRequest request,SessionService session,CancellationToken ct)=>
            await session.StopBrowserMediaAsync(await Read<BrowserMediaStop>(request,4096,ct),ct));
        app.MapPost("/api/v1/session/media/browser/audio", (HttpRequest request,SessionService session,CancellationToken ct)=>
            Upload(async()=>session.ReceiveBrowserAudio(await Read<BrowserAudioPacket>(request,3_000_000,ct))));
        app.MapPost("/api/v1/session/media/browser/frame", (HttpRequest request,SessionService session,CancellationToken ct)=>
            Upload(async()=>session.ReceiveBrowserFrame(await Read<BrowserFramePacket>(request,4_010_000,ct))));
    }
    static async Task<IResult> Upload(Func<Task<object>> action)
    {
        if(!await upload.WaitAsync(0))return Results.Json(new {error="媒体上传正在处理；请丢弃此批次，不堆积请求。"},statusCode:429);
        try{return Results.Json(await action());}finally{upload.Release();}
    }
    static async Task<T> Read<T>(HttpRequest request,int maximum,CancellationToken ct)
    {
        // Program's API middleware still enforces same-origin, session token and client header.
        if(request.ContentLength>maximum)throw new BadHttpRequestException("媒体请求超过大小限制。",413);
        using var output=new MemoryStream();var buffer=new byte[16384];int count;
        while((count=await request.Body.ReadAsync(buffer,ct))>0)
        {if(output.Length+count>maximum)throw new BadHttpRequestException("媒体请求超过大小限制。",413);output.Write(buffer,0,count);}
        return JsonSerializer.Deserialize<T>(output.ToArray(),new JsonSerializerOptions(JsonSerializerDefaults.Web))??throw new ArgumentException("缺少媒体请求。");
    }
}
