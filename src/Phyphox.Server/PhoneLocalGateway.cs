// SPDX-License-Identifier: GPL-3.0-only
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Security.AccessControl;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Security.Principal;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.Extensions.FileProviders;
using Phyphox.Pairing;

namespace Phyphox.Server;

// A separate listener with no experiment, file, bootstrap-token or management APIs.
public sealed class PhoneLocalGateway(string dataRoot, string webRoot) : IAsyncDisposable
{
    public sealed record NetworkAddress(string Address, string Name);
    private readonly SemaphoreSlim serial = new(1, 1);
    private WebApplication? app;
    private X509Certificate2? authority;
    private X509Certificate2? leaf;
    private Timer? sweep;
    private string? phoneOrigin, setupUrl, fingerprint;
    public PairingHub Hub { get; } = new();
    public bool Enabled => app is not null;
    public static NetworkAddress[] AvailableAddresses() => NetworkInterface.GetAllNetworkInterfaces()
        .Where(n => n.OperationalStatus == OperationalStatus.Up && n.NetworkInterfaceType != NetworkInterfaceType.Loopback)
        .SelectMany(n => n.GetIPProperties().UnicastAddresses.Where(a => PrivateAddress(a.Address)).Select(a => new NetworkAddress(a.Address.ToString(), n.Name)))
        .DistinctBy(a => a.Address).OrderBy(a => a.Name).ThenBy(a => a.Address).ToArray();
    public static bool PrivateAddress(IPAddress address)
    {
        if (address.AddressFamily != AddressFamily.InterNetwork) return false;
        var b = address.GetAddressBytes();
        return b[0] == 10 || b[0] == 172 && b[1] is >= 16 and <= 31 || b[0] == 192 && b[1] == 168;
    }
    public object Snapshot() => new { enabled = Enabled, setupUrl, phoneOrigin, httpCheckUrl = setupUrl is null ? null : new Uri(new Uri(setupUrl), "/sensor-check.html").AbsoluteUri, httpsCheckUrl = phoneOrigin is null ? null : phoneOrigin + "/sensor-check.html", fingerprint, expiresAt = authority?.NotAfter.ToUniversalTime(), addresses = AvailableAddresses(), certificateTrust = "browser-dependent", protocolVersion = 1, stage = "offline-prototype", singleInputOnly = true };
    public async Task<object> EnableAsync(string address, CancellationToken ct = default)
    {
        await serial.WaitAsync(ct);
        try
        {
            if (!AvailableAddresses().Any(a => a.Address == address)) throw new ArgumentException("请选择当前已连接网卡的局域网 IPv4 地址。");
            if (app is not null) throw new InvalidOperationException("请先关闭手机接入，再切换网络。");
            if (!File.Exists(Path.Combine(webRoot, "phone.html"))) throw new InvalidOperationException("缺少手机采集页面，请先构建 web/phone.html 入口后启用。 ");
            var ip = IPAddress.Parse(address);
            authority = PhoneCertificateStore.LoadOrCreate(dataRoot);
            fingerprint = Convert.ToHexString(SHA256.HashData(authority.RawData));
            leaf = PhoneCertificateStore.IssueServer(authority, ip);
            var builder = WebApplication.CreateSlimBuilder(new WebApplicationOptions { Args = [], EnvironmentName = "Production", ApplicationName = typeof(PhoneLocalGateway).Assembly.GetName().Name });
            builder.Logging.ClearProviders();
            builder.WebHost.ConfigureKestrel(options =>
            {
                options.Limits.MaxRequestBodySize = 65536;
                options.Limits.MaxConcurrentConnections = 128;
                options.Limits.MaxConcurrentUpgradedConnections = 128;
                options.Listen(ip, 0);
                options.Listen(ip, 0, listen => listen.UseHttps(leaf));
            });
            var server = builder.Build();
            server.Use(async (context, next) =>
            {
                if (phoneOrigin is null || setupUrl is null) { context.Response.StatusCode = 503; return; }
                var expected = new Uri(context.Request.IsHttps ? phoneOrigin : setupUrl).Authority;
                if (context.Request.Host.Value != expected) { context.Response.StatusCode = 403; return; }
                context.Response.Headers.CacheControl = "no-store";
                context.Response.Headers["X-Content-Type-Options"] = "nosniff";
                context.Response.Headers["Referrer-Policy"] = "no-referrer";
                context.Response.Headers["Content-Security-Policy"] = "default-src 'none'; script-src 'self'; style-src 'self'; img-src 'self' blob: data:; media-src 'self' blob:; connect-src 'self' " + phoneOrigin.Replace("https://", "wss://") + "; worker-src 'self' blob:; frame-ancestors 'none'; base-uri 'none'; form-action 'none'";
                context.Response.Headers["Permissions-Policy"] = "camera=(self), microphone=(self), accelerometer=(self), gyroscope=(self)";
                if (!context.Request.IsHttps) { await Setup(context); return; }
                await next();
            });
            server.UseWebSockets(new WebSocketOptions { KeepAliveInterval = TimeSpan.FromSeconds(20) });
            foreach (var file in new[] { "sensor-check.html", "sensor-check.js", "sensor-check.css" })
                server.MapGet("/" + file, async (HttpContext context) => { await Diagnostic(context); });
            var assets = Path.Combine(webRoot, "assets");
            if (Directory.Exists(assets)) server.UseStaticFiles(new StaticFileOptions { RequestPath = "/assets", FileProvider = new PhysicalFileProvider(assets) });
            foreach (var file in new[] { "phone.html", "phone-pcm-worklet.js", "browser-pcm-worklet.js" })
            {
                var path = Path.Combine(webRoot, file);
                server.MapGet("/" + file, () => File.Exists(path) ? Results.File(path, file.EndsWith(".html") ? "text/html; charset=utf-8" : "text/javascript; charset=utf-8") : Results.NotFound());
            }
            server.MapGet("/health", () => Results.Ok(new { status = "ok", protocolVersion = 1, offline = true }));
            server.Map("/signal", async context =>
            {
                if (!context.WebSockets.IsWebSocketRequest || context.Request.Headers.Origin.ToString() != phoneOrigin) { context.Response.StatusCode = 403; return; }
                using var socket = await context.WebSockets.AcceptWebSocketAsync();
                await PairingSockets.RunAsync(socket, Hub, new Peer(false), context.RequestAborted);
            });
            try
            {
                await server.StartAsync(ct);
                var addresses = server.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()!.Addresses;
                phoneOrigin = addresses.Single(a => a.StartsWith("https://", StringComparison.Ordinal));
                setupUrl = addresses.Single(a => a.StartsWith("http://", StringComparison.Ordinal)) + "/setup";
                app = server;
                sweep = new Timer(_ => Hub.Sweep(), null, TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(1));
                return Snapshot();
            }
            catch { await server.DisposeAsync(); phoneOrigin = setupUrl = null; leaf.Dispose(); leaf = null; authority.Dispose(); authority = null; throw; }
        }
        finally { serial.Release(); }
    }
    public async Task DisableAsync(CancellationToken ct = default)
    {
        await serial.WaitAsync(ct);
        try
        {
            sweep?.Dispose(); sweep = null; Hub.Reset();
            var server = app; app = null;
            phoneOrigin = setupUrl = null;
            if (server is not null)
            {
                using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct); timeout.CancelAfter(TimeSpan.FromSeconds(3));
                try { await server.StopAsync(timeout.Token); } finally { await server.DisposeAsync(); }
            }
            leaf?.Dispose(); leaf = null; authority?.Dispose(); authority = null;
        }
        finally { serial.Release(); }
    }
    private async Task Setup(HttpContext context)
    {
        if (context.Request.Method != "GET") { context.Response.StatusCode = 405; return; }
        if (await Diagnostic(context)) return;
        if (context.Request.Path == "/phone-ca.cer")
        {
            context.Response.ContentType = "application/x-x509-ca-cert";
            context.Response.Headers.ContentDisposition = "attachment; filename=phyphox-phone-ca.cer";
            await context.Response.Body.WriteAsync(authority!.RawData, context.RequestAborted); return;
        }
        if (context.Request.Path == "/phone-ca.mobileconfig")
        {
            context.Response.ContentType = "application/x-apple-aspen-config";
            context.Response.Headers.ContentDisposition = "attachment; filename=phyphox-phone-ca.mobileconfig";
            await context.Response.WriteAsync(Profile(), context.RequestAborted); return;
        }
        if (context.Request.Path == "/setup.js")
        {
            context.Response.ContentType = "text/javascript; charset=utf-8";
            await context.Response.WriteAsync("const fragment=location.hash;history.replaceState(null,'',location.pathname);document.getElementById('continue').href=" + JsonSerializer.Serialize(phoneOrigin + "/phone.html") + "+fragment;", context.RequestAborted); return;
        }
        if (context.Request.Path != "/setup") { context.Response.StatusCode = 404; return; }
        context.Response.ContentType = "text/html; charset=utf-8";
        await context.Response.WriteAsync($$"""
<!doctype html><html lang="zh-CN"><meta charset="utf-8"><meta name="viewport" content="width=device-width,initial-scale=1"><title>手机传感器 · 离线设置</title>
<body><main><h1>首次离线连接：浏览器连接帮助</h1><p>手机与电脑须连接同一局域网，全程不需要互联网。先尝试打开下面的 HTTPS 采集页；是否需要安装证书，取决于手机浏览器的实际行为，安装不是统一前置步骤。</p>
<h2>先在浏览器中尝试</h2><p>电脑使用本地签发的 HTTPS 证书，浏览器可能显示证书提示。确认地址与电脑软件显示的局域网地址一致、且确实是你自己的电脑后，若浏览器提供继续访问选项，可先打开页面进行检测。部分浏览器即使继续访问也可能不开放传感器；手机端兼容性仍需实测。</p><p><a id="continue" href="{{phoneOrigin}}/phone.html">打开 HTTPS 采集页</a> · <a href="{{phoneOrigin}}/sensor-check.html">检测传感器是否可用</a></p><p>采集页打开后仍需单独授权麦克风、相机或运动传感器。若配对已过期，请在电脑上刷新二维码并重新扫码。只看到接口或授权成功，不代表已经收到数据。</p>
<details><summary>浏览器无法继续或无法采集时：可选证书配置</summary><p>如果你希望始终只在浏览器里操作，可以先停止于此并记录检测结果。下面的系统证书配置是可选兼容方案。</p>
<p><strong>仅在你信任这台电脑、并决定使用此方案时安装。</strong>此安装会添加一个根证书。请先在电脑软件里核对下面完整 SHA-256 指纹，不能只相信手机页面显示的值。证书私钥始终保留在电脑，不会下载到手机。</p><pre>{{fingerprint}}</pre>
<h2>Android / Chrome</h2><p>下载 <a href="/phone-ca.cer">CA 证书</a>。打开设置 → 安全与隐私 → 更多安全设置 → 加密与凭据 → 安装证书 → CA 证书，选择下载的文件并确认。不同厂商菜单名称可能不同，可在设置中搜索“CA 证书”。</p>
<h2>iPhone / Safari</h2><p>下载 <a href="/phone-ca.mobileconfig">证书描述文件</a>。打开设置 → 通用 → VPN 与设备管理，安装已下载的 Phyphox 描述文件。然后到设置 → 通用 → 关于本机 → 证书信任设置，为 Phyphox Offline Phone Sensors 开启完全信任。</p>
<p>配置完成后返回电脑刷新二维码并重新扫码。若仍提示证书错误，请核对证书信任、电脑和手机时间、当前 IP 以及电脑显示的指纹。停用本功能后，可在上述系统设置中移除本证书或描述文件。单位管理的手机可能禁止用户安装根证书。</p></details></main><script src="/setup.js"></script></body></html>
""", context.RequestAborted);
    }
    private async Task<bool> Diagnostic(HttpContext context)
    {
        var contentType = context.Request.Path.Value switch
        {
            "/sensor-check.html" => "text/html; charset=utf-8",
            "/sensor-check.js" => "text/javascript; charset=utf-8",
            "/sensor-check.css" => "text/css; charset=utf-8",
            _ => null
        };
        if (contentType is null) return false;
        var path = Path.Combine(webRoot, context.Request.Path.Value![1..]);
        if (!File.Exists(path)) { context.Response.StatusCode = 404; return true; }
        context.Response.ContentType = contentType;
        await context.Response.SendFileAsync(path, context.RequestAborted);
        return true;
    }
    private string Profile()
    {
        var id = fingerprint!;
        return $$"""
<?xml version="1.0" encoding="UTF-8"?><!DOCTYPE plist PUBLIC "-//Apple//DTD PLIST 1.0//EN" "http://www.apple.com/DTDs/PropertyList-1.0.dtd"><plist version="1.0"><dict>
<key>PayloadType</key><string>Configuration</string><key>PayloadVersion</key><integer>1</integer><key>PayloadIdentifier</key><string>org.phyphox.phone.{{id}}</string><key>PayloadUUID</key><string>{{Guid.NewGuid()}}</string><key>PayloadDisplayName</key><string>Phyphox Offline Phone Sensors</string><key>PayloadDescription</key><string>仅包含此电脑的本地根证书；安装后需在证书信任设置开启完全信任。</string><key>PayloadContent</key><array><dict>
<key>PayloadType</key><string>com.apple.security.root</string><key>PayloadVersion</key><integer>1</integer><key>PayloadIdentifier</key><string>org.phyphox.phone.root.{{id}}</string><key>PayloadUUID</key><string>{{Guid.NewGuid()}}</string><key>PayloadDisplayName</key><string>Phyphox Offline Phone Sensors</string><key>PayloadCertificateFileName</key><string>phyphox-phone-ca.cer</string><key>PayloadContent</key><data>{{Convert.ToBase64String(authority!.RawData)}}</data></dict></array></dict></plist>
""";
    }
    public async ValueTask DisposeAsync() { await DisableAsync(); serial.Dispose(); }
}

public static class PhoneCertificateStore
{
    private sealed record StoredAuthority(string Password, string Pfx);
    public static X509Certificate2 LoadOrCreate(string dataRoot)
    {
        var directory = Path.Combine(Path.GetFullPath(dataRoot), "phone-certificates");
        Directory.CreateDirectory(directory);
        if (OperatingSystem.IsWindows())
        {
            using var identity = WindowsIdentity.GetCurrent();
            var security = new DirectorySecurity(); security.SetAccessRuleProtection(true, false);
            security.AddAccessRule(new FileSystemAccessRule(identity.User!, FileSystemRights.FullControl, InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit, PropagationFlags.None, AccessControlType.Allow));
            new DirectoryInfo(directory).SetAccessControl(security);
        }
        else File.SetUnixFileMode(directory, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        var path = Path.Combine(directory, "authority.json");
        if (!File.Exists(path))
        {
            using var key = RSA.Create(3072);
            var request = new CertificateRequest("CN=Phyphox Offline Phone Sensors " + Convert.ToHexString(RandomNumberGenerator.GetBytes(4)), key, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
            request.CertificateExtensions.Add(new X509BasicConstraintsExtension(true, true, 0, true));
            request.CertificateExtensions.Add(new X509KeyUsageExtension(X509KeyUsageFlags.KeyCertSign | X509KeyUsageFlags.CrlSign, true));
            request.CertificateExtensions.Add(new X509SubjectKeyIdentifierExtension(request.PublicKey, false));
            using var root = request.CreateSelfSigned(DateTimeOffset.UtcNow.AddMinutes(-5), DateTimeOffset.UtcNow.AddYears(2));
            var password = Convert.ToHexString(RandomNumberGenerator.GetBytes(32));
            var content = JsonSerializer.Serialize(new StoredAuthority(password, Convert.ToBase64String(root.Export(X509ContentType.Pfx, password))));
            var temp = Path.Combine(directory, ".authority-" + Guid.NewGuid().ToString("N"));
            try
            {
                using (var file = new FileStream(temp, FileMode.CreateNew, FileAccess.Write, FileShare.None))
                {
                    if (!OperatingSystem.IsWindows()) File.SetUnixFileMode(temp, UnixFileMode.UserRead | UnixFileMode.UserWrite);
                    var bytes = Encoding.UTF8.GetBytes(content); file.Write(bytes); file.Flush(true);
                }
                try { File.Move(temp, path, false); } catch (IOException) when (File.Exists(path)) { }
            }
            finally { if (File.Exists(temp)) File.Delete(temp); }
        }
        try
        {
            if (!OperatingSystem.IsWindows()) File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite);
            var stored = JsonSerializer.Deserialize<StoredAuthority>(File.ReadAllText(path)) ?? throw new InvalidDataException();
            // Apple uses a temporary keychain; EphemeralKeySet is unsupported there. No PersistKeySet or trust-store installation.
            var flags = (OperatingSystem.IsMacOS() ? X509KeyStorageFlags.DefaultKeySet : X509KeyStorageFlags.EphemeralKeySet) | X509KeyStorageFlags.Exportable;
            var cert = X509CertificateLoader.LoadPkcs12(Convert.FromBase64String(stored.Pfx), stored.Password, flags);
            if (!cert.HasPrivateKey || cert.NotAfter.ToUniversalTime() <= DateTime.UtcNow.AddDays(1) || cert.NotBefore.ToUniversalTime() > DateTime.UtcNow || !cert.Extensions.OfType<X509BasicConstraintsExtension>().Any(e => e.CertificateAuthority)) { cert.Dispose(); throw new InvalidDataException(); }
            return cert;
        }
        catch (Exception ex) when (ex is CryptographicException or JsonException or FormatException or InvalidDataException or ArgumentException)
        { throw new InvalidOperationException("本地手机证书已损坏或过期，未自动覆盖。请停用手机接入并由电脑所有者检查 phone-certificates/authority.json；更换证书后所有手机需重新安装信任。", ex); }
    }
    public static X509Certificate2 IssueServer(X509Certificate2 authority, IPAddress address)
    {
        if (!PhoneLocalGateway.PrivateAddress(address)) throw new ArgumentException("仅允许局域网 IPv4 地址。");
        using var key = RSA.Create(2048);
        var request = new CertificateRequest("CN=Phyphox Local Phone Gateway", key, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        request.CertificateExtensions.Add(new X509BasicConstraintsExtension(false, false, 0, true));
        request.CertificateExtensions.Add(new X509KeyUsageExtension(X509KeyUsageFlags.DigitalSignature | X509KeyUsageFlags.KeyEncipherment, true));
        request.CertificateExtensions.Add(new X509EnhancedKeyUsageExtension(new OidCollection { new("1.3.6.1.5.5.7.3.1") }, false));
        var san = new SubjectAlternativeNameBuilder(); san.AddIpAddress(address); request.CertificateExtensions.Add(san.Build());
        request.CertificateExtensions.Add(new X509SubjectKeyIdentifierExtension(request.PublicKey, false));
        var end = DateTimeOffset.UtcNow.AddDays(30); if (end.UtcDateTime > authority.NotAfter.ToUniversalTime()) end = authority.NotAfter.ToUniversalTime();
        using var certificate = request.Create(authority, DateTimeOffset.UtcNow.AddMinutes(-2), end, RandomNumberGenerator.GetBytes(16));
        return certificate.CopyWithPrivateKey(key);
    }
}
