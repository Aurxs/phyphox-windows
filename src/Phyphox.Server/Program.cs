// SPDX-License-Identifier: GPL-3.0-only
using System.Net;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Phyphox.Core;

namespace Phyphox.Server;
public static class Program
{
    public static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        NumberHandling = JsonNumberHandling.AllowNamedFloatingPointLiterals,
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase) }
    };
    [STAThread]
    public static void Main(string[] args)
    {
#if WINDOWS
        if (!args.Contains("--headless"))
        {
            System.Windows.Forms.Application.EnableVisualStyles();
            System.Windows.Forms.Application.SetCompatibleTextRenderingDefault(false);
            System.Windows.Forms.Application.Run(new DesktopWindow(args));
            return;
        }
#endif
        RunServerAsync(args).GetAwaiter().GetResult();
    }
    internal static async Task RunServerAsync(string[] args, Action<string, string>? ready = null, CancellationToken stopping = default)
    {
        string? Option(string name) { var i = Array.IndexOf(args, name); return i >= 0 && i + 1 < args.Length ? args[i + 1] : null; }
        var portableRoot = AppContext.BaseDirectory;
#if WINDOWS
        // The user-facing launcher lives above app/. Keep measurements beside
        // that EXE, while web assets and runtime files remain in app/.
        if (Environment.ProcessPath is { } executable && Path.GetFileName(executable).Equals("phyphox.exe", StringComparison.OrdinalIgnoreCase))
            portableRoot = Path.GetDirectoryName(executable)!;
#endif
        var dataRoot = Path.GetFullPath(Option("--data-dir") ?? Path.Combine(portableRoot, "data"));
        var assetRoot = Path.GetFullPath(Option("--assets") ?? Path.Combine(AppContext.BaseDirectory, "assets"));
        var port = int.TryParse(Option("--port"), out var parsed) ? parsed : 0;
        if (port is < 0 or > 65535) throw new ArgumentException("端口必须在 0..65535 之间。");
        Directory.CreateDirectory(dataRoot);
        using var dataLock = new FileStream(Path.Combine(dataRoot, ".service.lock"), FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
        if (!Directory.Exists(assetRoot)) throw new DirectoryNotFoundException("找不到实验资源目录，请保留完整程序文件夹或使用 --assets 指定路径。");
        var builder = WebApplication.CreateBuilder(new WebApplicationOptions { Args = [], ContentRootPath = AppContext.BaseDirectory, WebRootPath = Path.Combine(AppContext.BaseDirectory, "wwwroot") });
        builder.Logging.AddFilter("Microsoft.AspNetCore", LogLevel.Warning);
        builder.WebHost.ConfigureKestrel(options => { options.Listen(IPAddress.Loopback, port); options.Limits.MaxRequestBodySize = LibraryService.MaximumImportBytes + 1024 * 1024; });
        builder.Services.Configure<FormOptions>(o => o.MultipartBodyLengthLimit = LibraryService.MaximumImportBytes);
        builder.Services.ConfigureHttpJsonOptions(o => { o.SerializerOptions.NumberHandling = JsonOptions.NumberHandling; o.SerializerOptions.Converters.Add(new JsonStringEnumConverter(JsonNamingPolicy.CamelCase)); });
        var library = new LibraryService(assetRoot, dataRoot);
        var deviceManager = new DeviceManager();
        var session = new SessionService(library, dataRoot, deviceManager);
        builder.Services.AddSingleton(library); builder.Services.AddSingleton(session); builder.Services.AddHostedService(_ => session);
        builder.Services.AddSingleton(deviceManager);
        builder.Services.AddSingleton(_ => new PhoneLocalGateway(dataRoot, Path.Combine(AppContext.BaseDirectory, "wwwroot")));
        builder.Services.AddSingleton<PhoneBridgeLease>();
        builder.Services.AddHostedService(provider => provider.GetRequiredService<PhoneBridgeLease>());
        await using var app = builder.Build();
        app.UseWebSockets();
        var token = Convert.ToHexString(RandomNumberGenerator.GetBytes(32));
        app.Use(async (context, next) =>
        {
            context.Response.Headers["X-Content-Type-Options"] = "nosniff";
            context.Response.Headers["Referrer-Policy"] = "no-referrer";
            context.Response.Headers["Content-Security-Policy"] = "default-src 'self'; img-src 'self' blob: data:; style-src 'self' 'unsafe-inline'; script-src 'self'; connect-src 'self' ws://127.0.0.1:* ws://localhost:*; frame-ancestors 'none'; base-uri 'self'";
            try
            {
                var host = context.Request.Host.Host;
                if (host != "127.0.0.1" && !host.Equals("localhost", StringComparison.OrdinalIgnoreCase)) { context.Response.StatusCode = 403; await context.Response.WriteAsJsonAsync(new { error = "仅允许本机访问。" }); return; }
                if (context.Request.Path.StartsWithSegments("/api"))
                {
                    context.Response.Headers.CacheControl = "no-store";
                    var origin = context.Request.Headers.Origin.ToString();
                    var fetchSite = context.Request.Headers["Sec-Fetch-Site"].ToString();
                    if (fetchSite == "cross-site" || (origin.Length > 0 && origin != $"{context.Request.Scheme}://{context.Request.Host}"))
                    { context.Response.StatusCode = 403; await context.Response.WriteAsJsonAsync(new { error = "拒绝外部网页访问本地实验服务。" }); return; }
                    if (context.Request.Path != "/api/v1/bootstrap" && context.Request.Path != "/api/v1/health" && context.Request.Headers["X-Phyphox-Token"] != token)
                    { context.Response.StatusCode = 401; await context.Response.WriteAsJsonAsync(new { error = "请重新打开本机实验页面。" }); return; }
                    if (context.Request.Method is "POST" or "DELETE" && context.Request.Headers["X-Phyphox-Client"] != "browser")
                    { context.Response.StatusCode = 403; await context.Response.WriteAsJsonAsync(new { error = "缺少本地操作标识。" }); return; }
                    if (context.Request.Path.StartsWithSegments("/api/v1/session/phone"))
                    {
                        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(context.RequestAborted);
                        deadline.CancelAfter(TimeSpan.FromSeconds(3));
                        context.RequestAborted = deadline.Token;
                        await app.Services.GetRequiredService<PhoneBridgeLease>().RunOwned(context.Request.Headers["X-Phyphox-Phone-Lease"].ToString(), () => next(), deadline.Token);
                        return;
                    }
                }
                await next();
            }
            catch (Exception ex) when (!context.Response.HasStarted)
            {
                context.Response.StatusCode = ex is BadHttpRequestException badRequest ? badRequest.StatusCode : ex is KeyNotFoundException ? 404 : ex is NotSupportedException ? 422 : ex is IOException ? 409 : 400;
                await context.Response.WriteAsJsonAsync(new { error = ex.Message });
            }
        });
        app.MapGet("/api/v1/bootstrap", () => new { token });
        app.MapGet("/api/v1/health", () => new { version = "0.1.0-dev", platform = System.Runtime.InteropServices.RuntimeInformation.OSDescription,
            processArchitecture = System.Runtime.InteropServices.RuntimeInformation.ProcessArchitecture.ToString(), osArchitecture = System.Runtime.InteropServices.RuntimeInformation.OSArchitecture.ToString(),
            processorCount = Environment.ProcessorCount, formatVersion = "1.20", verification = new { hardware = "notExecuted", performance = "notExecuted", note = "See supplied validation records for tested build/environment." }, portable = true });
        app.MapGet("/api/v1/capabilities", () => new { formatVersion = "1.20", implementation = "inProgress", windows10 = "targeted-not-verified", windows11 = "targeted-not-verified", offline = true, hardwareVerified = false });
        app.MapGet("/api/v1/library", () => new { items = library.List() });
        app.MapPost("/api/v1/library/import", async (HttpRequest request, CancellationToken ct) =>
        {
            var form = await request.ReadFormAsync(ct); var file = form.Files.GetFile("file") ?? throw new InvalidOperationException("请选择实验文件。");
            await using var stream = file.OpenReadStream(); return new { items = await library.ImportAsync(stream, file.FileName, ct) };
        });
        app.MapDelete("/api/v1/library/{id}", (string id) => { if (session.CurrentId == id) throw new InvalidOperationException("不能删除当前打开的实验。"); library.Delete(id); return Results.Ok(new { deleted = true }); });
        app.MapGet("/api/v1/library/{id}/resource", (string id, string src) =>
        {
            var path = library.Resource(id, src); if (path is null) return Results.NotFound(new { error = "资源不可用。" });
            var mime = Path.GetExtension(path).ToLowerInvariant() switch { ".png" => "image/png", ".jpg" or ".jpeg" => "image/jpeg", ".svg" => "image/svg+xml", ".gif" => "image/gif", _ => "application/octet-stream" };
            return Results.File(path, mime);
        });
        app.MapPost("/api/v1/session/load", (LoadRequest request, CancellationToken ct) => session.LoadAsync(request.Id, ct));
        app.MapGet("/api/v1/session", () => session.Snapshot());
        app.MapPost("/api/v1/session/commands", (SessionCommand command, CancellationToken ct) => session.CommandAsync(command, ct));
        app.MapPost("/api/v1/session/bindings", (InputBinding binding, CancellationToken ct) => session.BindInputAsync(binding, ct));
        app.MapGet("/api/v1/exports", (string format) => { var file = ExportService.Create(session.ExportSnapshot(), format); return Results.File(file.Bytes, file.ContentType, file.FileName); });
        app.MapGet("/api/v1/devices", (DeviceManager devices) => devices.List());
        app.MapPost("/api/v1/devices/scan", (ScanRequest request, DeviceManager devices, CancellationToken ct) => devices.Scan(request.Kind, ct));
        app.MapPost("/api/v1/devices/connect", (ConnectRequest request, DeviceManager devices, CancellationToken ct) => devices.Connect(request, ct));
        app.MapGet("/api/v1/devices/{id}/frames", (string id, DeviceManager devices) => devices.Frames(id));
        app.MapPost("/api/v1/devices/{id}/disconnect", async (string id, DeviceManager devices) => { await devices.Disconnect(id); return Results.Ok(new { disconnected = true }); });
        app.MapPost("/api/v1/devices/{id}/write", async (string id, WriteRequest request, DeviceManager devices, CancellationToken ct) => { await devices.Write(id, request.Hex, ct); return Results.Ok(new { sent = true }); });
        app.MapPost("/api/v1/session/output-bindings", (DeviceOutputBinding binding) => session.BindOutput(binding));
        app.MapPost("/api/v1/session/ble-input-bindings", (BleInputBinding binding, CancellationToken ct) => session.BindBleInputAsync(binding, ct));
        app.MapDeviceTransferEndpoints();
        app.MapStorageEndpoints();
        app.MapMediaEndpoints();
        app.MapBrowserMediaEndpoints();
        app.MapBrowserAudioOutputEndpoints();
        app.MapPhoneBridgeEndpoints();
        app.MapPhoneMotionEndpoints();
        app.MapPhoneLocalSignal();
        app.UseDefaultFiles(); app.UseStaticFiles();
        app.Map("/api/{**path}", () => Results.NotFound(new { error = "接口不存在。" }));
        app.MapFallbackToFile("index.html");
        await app.StartAsync(stopping);
        var addresses = app.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()!.Addresses;
        Console.WriteLine("\nphyphox Windows 实验工作台（开发版本）\n" + string.Join('\n', addresses.Select(a => "浏览器打开：" + a)) + "\n数据目录：" + dataRoot + "\nCtrl+C 停止服务。设备和 Windows 真机验收尚未执行。\n");
        ready?.Invoke(addresses.First(), dataRoot);
        await app.WaitForShutdownAsync(stopping);
    }
    public sealed record LoadRequest(string Id);
}
