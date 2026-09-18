// SPDX-License-Identifier: GPL-3.0-only
using MQTTnet;
using MQTTnet.Client;
using MQTTnet.Formatter;
using MQTTnet.Protocol;
using System.Security.Authentication;
using System.Security.Cryptography.X509Certificates;

namespace Phyphox.Network;
internal sealed record MqttSettings(string Address, bool Tls, string ReceiveTopic, string SendTopic, string? Username, string? Password, string? Certificate, bool Persistence);
internal sealed class MqttTransport : IDisposable
{
    readonly IMqttClient client = new MqttFactory().CreateMqttClient();
    readonly MqttSettings settings;
    readonly MqttClientOptions options;
    readonly X509Certificate2? ca;
    public bool Connected => client.IsConnected;
    public MqttTransport(MqttSettings settings, Func<string, byte[]>? resource, Action<byte[]> received, Action<string> fault, TimeSpan timeout)
    {
        this.settings = settings;
        var uri = ParseAddress(settings.Address, settings.Tls);
        var builder = new MqttClientOptionsBuilder().WithClientId("phyphox_windows_" + Guid.NewGuid().ToString("N"))
            .WithTcpServer(uri.Host, uri.Port > 0 ? uri.Port : settings.Tls ? 8883 : 1883)
            .WithProtocolVersion(MqttProtocolVersion.V311).WithCleanSession().WithKeepAlivePeriod(TimeSpan.FromSeconds(30)).WithTimeout(timeout);
        if (!string.IsNullOrEmpty(settings.Username)) builder.WithCredentials(settings.Username, settings.Password);
        if (settings.Tls)
        {
            if (!string.IsNullOrEmpty(settings.Certificate))
            {
                if (resource is null) throw new InvalidDataException("Certificate resource provider is required.");
                var bytes = resource(settings.Certificate);
                var text = System.Text.Encoding.UTF8.GetString(bytes);
                ca = text.Contains("-----BEGIN CERTIFICATE-----", StringComparison.Ordinal) ? X509Certificate2.CreateFromPem(text) : X509CertificateLoader.LoadCertificate(bytes);
            }
            builder.WithTlsOptions(tls =>
            {
                tls.UseTls().WithSslProtocols(SslProtocols.Tls12 | SslProtocols.Tls13);
                // CustomRootTrust still verifies hostname, validity and chain. Never disable certificate checks.
                if (ca is not null) tls.WithTrustChain(new X509Certificate2Collection(ca));
            });
        }
        options = builder.Build();
        client.ApplicationMessageReceivedAsync += e =>
        {
            try { received(e.ApplicationMessage.PayloadSegment.ToArray()); }
            catch (Exception ex) { fault(ex.Message); }
            return Task.CompletedTask;
        };
        client.DisconnectedAsync += e => { if (e.Exception is not null) fault("MQTT disconnected: " + e.Exception.Message); return Task.CompletedTask; };
    }
    internal static Uri ParseAddress(string address, bool tls)
    {
        var full = address.Contains("://", StringComparison.Ordinal) ? address : (tls ? "mqtts://" : "mqtt://") + address;
        if (!Uri.TryCreate(full, UriKind.Absolute, out var uri) || uri.Scheme is not ("mqtt" or "mqtts" or "tcp" or "ssl") || string.IsNullOrWhiteSpace(uri.Host) || uri.UserInfo.Length != 0 || uri.AbsolutePath is not ("" or "/")) throw new InvalidDataException("Invalid MQTT host/port address.");
        return uri;
    }
    public async Task ConnectAsync(CancellationToken token)
    {
        if (client.IsConnected) return;
        var result = await client.ConnectAsync(options, token).ConfigureAwait(false);
        if (result.ResultCode != MqttClientConnectResultCode.Success) throw new IOException("MQTT connection rejected: " + result.ResultCode);
        if (!string.IsNullOrEmpty(settings.ReceiveTopic))
        {
            var options = new MqttClientSubscribeOptionsBuilder().WithTopicFilter(f => f.WithTopic(settings.ReceiveTopic).WithQualityOfServiceLevel(MqttQualityOfServiceLevel.AtMostOnce)).Build();
            var subscribed = await client.SubscribeAsync(options, token).ConfigureAwait(false);
            if (subscribed.Items.Any(i => (int)i.ResultCode >= 128)) throw new IOException("MQTT subscription rejected.");
        }
    }
    public async Task PublishAsync(string topic, string payload, CancellationToken token)
    {
        var message = new MqttApplicationMessageBuilder().WithTopic(topic).WithPayload(payload)
            .WithQualityOfServiceLevel(settings.Persistence ? MqttQualityOfServiceLevel.AtLeastOnce : MqttQualityOfServiceLevel.AtMostOnce).Build();
        var result = await client.PublishAsync(message, token).ConfigureAwait(false);
        if ((int)result.ReasonCode >= 128) throw new IOException("MQTT publish rejected: " + result.ReasonCode);
    }
    public void Dispose() { client.Dispose(); ca?.Dispose(); }
}
