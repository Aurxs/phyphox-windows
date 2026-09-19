// SPDX-License-Identifier: GPL-3.0-only
using System.Diagnostics;
using System.Security.Cryptography;
using System.Text.Json;

namespace Phyphox.Server;

// One local browser owns the bridge. All configuration transitions share the
// lease semaphore so a request cannot outlive its authority and rebind inputs.
public sealed class PhoneBridgeLease : BackgroundService
{
    readonly Func<bool> enabled;
    readonly Func<Task> stop;
    readonly TimeProvider clock;
    readonly SemaphoreSlim serial = new(1, 1);
    readonly object gate = new();
    string? id, ticket;
    long renewed, ticketIssued;
    CancellationTokenSource? ownerStop;
    public PhoneBridgeLease(SessionService session, PhoneLocalGateway gateway)
        : this(() => gateway.Enabled, () => session.StopPhoneBridgeAsync(), TimeProvider.System) { }
    public PhoneBridgeLease(Func<bool> enabled, Func<Task> stop, TimeProvider? clock=null)
    { this.enabled=enabled;this.stop=stop;this.clock=clock??TimeProvider.System; }
    bool Live => id is not null && enabled() && clock.GetElapsedTime(renewed) < TimeSpan.FromSeconds(5);
    CancellationTokenSource? RevokeLocked()
    { var previous=ownerStop;ownerStop=null;id=null;ticket=null;return previous; }
    static void Cancel(CancellationTokenSource? owner)
    { if(owner is null)return;owner.Cancel();owner.Dispose(); }
    public async Task<object> Claim()
    {
        await serial.WaitAsync();
        try
        {
            bool clean;CancellationTokenSource? previous;
            lock(gate)
            {
                if(!enabled())throw new InvalidOperationException("请先启用离线手机连接并选择局域网地址。");
                if(Live)throw new IOException("另一个工作台页面正在连接手机，请先断开或等待页面退出。");
                clean=id is not null;previous=RevokeLocked();
            }
            Cancel(previous);if(clean)await stop();
            lock(gate){id=Convert.ToHexString(RandomNumberGenerator.GetBytes(24));ownerStop=new();renewed=clock.GetTimestamp();return new{leaseId=id,timeoutSeconds=5};}
        }
        finally{serial.Release();}
    }
    public void Require(string? value)
    {lock(gate)if(!Live||string.IsNullOrEmpty(value)||value!=id)throw new InvalidOperationException("手机桥接所有权已失效，请重新连接。");}
    public async Task RunOwned(string value,Func<Task> work,CancellationToken ct)
    {
        await serial.WaitAsync(ct);
        try{Require(value);await work();}finally{serial.Release();}
    }
    public object Heartbeat(string value)
    {lock(gate){Require(value);renewed=clock.GetTimestamp();return new{connected=true};}}
    public object IssueSignalTicket(string value)
    {
        lock(gate)
        {
            Require(value);ticket=Convert.ToHexString(RandomNumberGenerator.GetBytes(32));ticketIssued=clock.GetTimestamp();
            return new{token=ticket,expiresInSeconds=10};
        }
    }
    public sealed record SignalAuthority(string LeaseId,CancellationToken Cancellation);
    public SignalAuthority ConsumeSignalTicket(string value)
    {
        lock(gate)
        {
            if(!Live||ticket is null||clock.GetElapsedTime(ticketIssued)>=TimeSpan.FromSeconds(10)||value.Length!=ticket.Length||!CryptographicOperations.FixedTimeEquals(System.Text.Encoding.ASCII.GetBytes(value),System.Text.Encoding.ASCII.GetBytes(ticket)))throw new InvalidOperationException("手机信令票据无效、已过期或已使用。");
            ticket=null;return new(id!,ownerStop!.Token);
        }
    }
    public async Task Release(string value)
    {
        await serial.WaitAsync();
        try
        {
            CancellationTokenSource? previous;
            lock(gate){if(id!=value)return;previous=RevokeLocked();}
            Cancel(previous);await stop();
        }
        finally{serial.Release();}
    }
    public async Task<object> ChangeGateway(Func<Task<object>> change,CancellationToken ct)
    {
        await serial.WaitAsync(ct);
        try
        {
            CancellationTokenSource? previous;lock(gate)previous=RevokeLocked();
            Cancel(previous);await stop();return await change();
        }
        finally{serial.Release();}
    }
    public async Task ExpireAsync(CancellationToken ct=default)
    {
        await serial.WaitAsync(ct);
        try
        {
            CancellationTokenSource? previous;bool expired;
            lock(gate){expired=id is not null&&!Live;previous=expired?RevokeLocked():null;}
            Cancel(previous);if(expired)await stop();
        }
        finally{serial.Release();}
    }
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer=new PeriodicTimer(TimeSpan.FromSeconds(1));
        try{while(await timer.WaitForNextTickAsync(stoppingToken))await ExpireAsync(stoppingToken);}
        catch(OperationCanceledException)when(stoppingToken.IsCancellationRequested){}
    }
}

public static class PhoneBridgeEndpoints
{
    public sealed record LeaseRequest(string LeaseId);
    public sealed record EnableRequest(string Address);
    static object Config(PhoneLocalGateway gateway)
    {
        var config=JsonSerializer.SerializeToElement(gateway.Snapshot(),Program.JsonOptions).EnumerateObject().ToDictionary(p=>p.Name,p=>(object)p.Value.Clone());
        config["protocolVersion"]=1;config["stage"]="prototype";config["singleInputOnly"]=true;return config;
    }
    static readonly SemaphoreSlim mediaUpload = new(1, 1);
    public static void MapPhoneBridgeEndpoints(this WebApplication app)
    {
        app.MapGet("/api/v1/phone/config", (PhoneLocalGateway gateway) => Config(gateway));
        app.MapPost("/api/v1/phone/enable", (EnableRequest body,PhoneLocalGateway gateway,PhoneBridgeLease lease,CancellationToken ct) => lease.ChangeGateway(async()=>{await gateway.EnableAsync(body.Address,ct);return Config(gateway);},ct));
        app.MapPost("/api/v1/phone/disable", (PhoneLocalGateway gateway,PhoneBridgeLease lease,CancellationToken ct) => lease.ChangeGateway(async()=>{await gateway.DisableAsync(ct);return Config(gateway);},ct));
        app.MapPost("/api/v1/phone/signal-ticket", (LeaseRequest body,PhoneBridgeLease lease) => lease.IssueSignalTicket(body.LeaseId));
        app.MapPost("/api/v1/phone/claim", (PhoneBridgeLease lease) => lease.Claim());
        app.MapPost("/api/v1/phone/heartbeat", (LeaseRequest body, PhoneBridgeLease lease) => lease.Heartbeat(body.LeaseId));
        app.MapPost("/api/v1/phone/release", async (LeaseRequest body, PhoneBridgeLease lease) => { await lease.Release(body.LeaseId); return Results.Ok(new { disconnected = true }); });
        app.MapPost("/api/v1/session/phone/reset", async (SessionService session) => { await session.StopPhoneBridgeAsync(); return session.Snapshot(); });
        app.MapPost("/api/v1/session/phone/media/configure", async (HttpRequest request, SessionService session, CancellationToken ct) =>
        {
            var body = await Read<BrowserMediaConfiguration>(request, 4096, ct);
            return session.ConfigureBrowserMedia(body with { Source = "phone" });
        });
        app.MapPost("/api/v1/session/phone/media/audio", (HttpRequest request, SessionService session, CancellationToken ct) => Upload(async () => session.ReceiveBrowserAudio(await Read<BrowserAudioPacket>(request, 65536, ct))));
        app.MapPost("/api/v1/session/phone/media/frame", (HttpRequest request, SessionService session, CancellationToken ct) => Upload(async () => session.ReceiveBrowserFrame(await Read<BrowserFramePacket>(request, 65536, ct))));
    }
    static async Task<IResult> Upload(Func<Task<object>> action)
    {
        if (!await mediaUpload.WaitAsync(0)) return Results.Json(new { error = "手机媒体处理拥塞，请停止或降低图像帧率。" }, statusCode: 429);
        try { return Results.Json(await action()); }
        finally { mediaUpload.Release(); }
    }
    static async Task<T> Read<T>(HttpRequest request, int maximum, CancellationToken ct)
    {
        if (request.ContentLength > maximum) throw new BadHttpRequestException("手机请求过大。", 413);
        using var data = new MemoryStream();
        var buffer = new byte[8192];
        int count;
        while ((count = await request.Body.ReadAsync(buffer, ct)) > 0)
        {
            if (data.Length + count > maximum) throw new BadHttpRequestException("手机请求过大。", 413);
            data.Write(buffer, 0, count);
        }
        return JsonSerializer.Deserialize<T>(data.ToArray(), Program.JsonOptions) ?? throw new ArgumentException("缺少手机请求。");
    }
}
