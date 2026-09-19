// SPDX-License-Identifier: GPL-3.0-only
using System.Text.Json;
namespace Phyphox.Server;
public static class PhoneMotionEndpoints
{
    public static void MapPhoneMotionEndpoints(this WebApplication app)
    {
        app.MapPost("/api/v1/session/phone/motion/configure",async(HttpRequest request,SessionService session,CancellationToken ct)=>session.ConfigurePhoneMotion(await Read<PhoneMotionConfiguration>(request,4096,ct)));
        app.MapPost("/api/v1/session/phone/motion/samples",async(HttpRequest request,SessionService session,CancellationToken ct)=>session.ReceivePhoneMotion(await Read<PhoneMotionPacket>(request,32768,ct)));
        app.MapPost("/api/v1/session/phone/motion/stop",async(HttpRequest request,SessionService session,CancellationToken ct)=>await session.StopPhoneMotionAsync(await Read<PhoneMotionStop>(request,4096,ct),ct));
    }
    static async Task<T> Read<T>(HttpRequest request,int maximum,CancellationToken ct)
    {
        if(request.ContentLength>maximum)throw new BadHttpRequestException("手机运动请求超过限制。",413);
        using var output=new MemoryStream();var buffer=new byte[4096];int count;
        while((count=await request.Body.ReadAsync(buffer,ct))>0){if(output.Length+count>maximum)throw new BadHttpRequestException("手机运动请求超过限制。",413);output.Write(buffer,0,count);}
        return JsonSerializer.Deserialize<T>(output.ToArray(),new JsonSerializerOptions(JsonSerializerDefaults.Web))??throw new ArgumentException("缺少手机运动请求。");
    }
}
