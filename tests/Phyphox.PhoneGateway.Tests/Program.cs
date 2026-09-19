using System.Formats.Asn1;
using System.Net;
using System.Net.Security;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text.Json;
using Phyphox.Server;

static void Assert(bool condition, string message) { if (!condition) throw new Exception(message); }
var root = Path.Combine(Path.GetTempPath(), "phyphox-gateway-test-" + Guid.NewGuid().ToString("N"));
Directory.CreateDirectory(root);
try
{
    using var ca = PhoneCertificateStore.LoadOrCreate(root);
    using var reused = PhoneCertificateStore.LoadOrCreate(root);
    Assert(ca.RawData.SequenceEqual(reused.RawData), "CA identity must persist across restart");
    Assert(ca.HasPrivateKey && ca.Extensions.OfType<X509BasicConstraintsExtension>().Single().CertificateAuthority, "Root signing key");
    var ip = IPAddress.Parse("192.168.1.42");
    using var leaf = PhoneCertificateStore.IssueServer(ca, ip);
    var san = leaf.Extensions.Single(e => e.Oid!.Value == "2.5.29.17");
    var reader = new AsnReader(san.RawData, AsnEncodingRules.DER).ReadSequence();
    Assert(reader.ReadOctetString(new Asn1Tag(TagClass.ContextSpecific, 7)).SequenceEqual(ip.GetAddressBytes()) && !reader.HasData, "Exact selected IP SAN");
    using var chain = new X509Chain();
    chain.ChainPolicy.TrustMode = X509ChainTrustMode.CustomRootTrust;
    chain.ChainPolicy.CustomTrustStore.Add(ca);
    chain.ChainPolicy.RevocationMode = X509RevocationMode.NoCheck;
    chain.ChainPolicy.DisableCertificateDownloads = true;
    Assert(chain.Build(leaf), "Leaf chains to local root without network");
    var addresses = PhoneLocalGateway.AvailableAddresses();
    Assert(!PhoneLocalGateway.PrivateAddress(IPAddress.Loopback) && !PhoneLocalGateway.PrivateAddress(IPAddress.Parse("8.8.8.8")), "No loopback/public interface exposure");
    var web = Path.Combine(root, "web"); Directory.CreateDirectory(web); await File.WriteAllTextAsync(Path.Combine(web, "phone.html"), "<!doctype html><title>Phone fixture</title>");
    if (addresses.Length == 0) Console.WriteLine("SKIP listener integration: no selected private IPv4 interface available.");
    else
    {
        await using var gateway = new PhoneLocalGateway(root, web);
        var snapshot = JsonSerializer.SerializeToElement(await gateway.EnableAsync(addresses[0].Address));
        var setup = snapshot.GetProperty("setupUrl").GetString()!;
        var origin = snapshot.GetProperty("phoneOrigin").GetString()!;
        Assert(snapshot.GetProperty("fingerprint").GetString() == Convert.ToHexString(SHA256.HashData(ca.RawData)), "Desktop fingerprint matches downloaded CA");
        using var client = new HttpClient(new HttpClientHandler { UseProxy = false }) { Timeout = TimeSpan.FromSeconds(5) };
        Assert((await client.GetStringAsync(setup)).Contains("首次离线连接"), "HTTP onboarding page");
        var bootstrap = new Uri(setup).GetLeftPart(UriPartial.Authority);
        Assert((await client.GetByteArrayAsync(bootstrap + "/phone-ca.cer")).SequenceEqual(ca.RawData), "Only public certificate downloadable");
        Assert((await client.GetStringAsync(bootstrap + "/phone-ca.mobileconfig")).Contains("com.apple.security.root"), "iOS root-only profile");
        foreach (var path in new[] { "/phone.html", "/signal", "/api/v1/bootstrap", "/phone-certificates/authority.json" })
            Assert((await client.GetAsync(bootstrap + path)).StatusCode == HttpStatusCode.NotFound, "HTTP exposes setup only: " + path);
        using var handler = new HttpClientHandler { UseProxy = false, ServerCertificateCustomValidationCallback = (_, certificate, _, errors) => certificate != null && (errors & SslPolicyErrors.RemoteCertificateNameMismatch) == 0 && chain.Build(certificate) };
        using var secure = new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(5) };
        Assert((await secure.GetStringAsync(origin + "/phone.html")).Contains("Phone fixture"), "Trusted TLS phone page");
        Assert((await secure.GetAsync(origin + "/api/v1/bootstrap")).StatusCode == HttpStatusCode.NotFound, "No management APIs on LAN HTTPS");
        Assert((await secure.GetAsync(origin + "/phone-ca.cer")).StatusCode == HttpStatusCode.NotFound, "HTTPS surface stays minimal");
        await gateway.DisableAsync(); Assert(!gateway.Enabled, "Gateway explicitly shuts down");
        Console.WriteLine("Offline gateway listener checks passed (HTTP isolation + custom-root HTTPS; no OS trust changes).");
    }
    var privatePath = Path.Combine(root, "phone-certificates", "authority.json");
    await File.WriteAllTextAsync(privatePath, "invalid authority");
    try { using var _ = PhoneCertificateStore.LoadOrCreate(root); throw new Exception("Invalid CA must not regenerate silently"); } catch (InvalidOperationException) { }
    Assert(await File.ReadAllTextAsync(privatePath) == "invalid authority", "Corrupt trust identity preserved for manual repair");
    Console.WriteLine("Offline certificate checks passed: persistent CA, selected-IP SAN, local trust chain, invalid store preserved.");
}
finally { Directory.Delete(root, true); }
