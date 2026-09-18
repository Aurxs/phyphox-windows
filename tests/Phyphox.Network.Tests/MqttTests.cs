// SPDX-License-Identifier: GPL-3.0-only
using MQTTnet;
using MQTTnet.Server;
using MQTTnet.Protocol;
using Phyphox.Network;
using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Xml.Linq;

internal static class MqttTests
{
    static int Port() { var s=new TcpListener(IPAddress.Loopback,0);s.Start();var p=((IPEndPoint)s.LocalEndpoint).Port;s.Stop();return p; }
    static void Check(bool condition,string message) { if(!condition)throw new Exception(message);Console.WriteLine("PASS "+message); }
    static async Task<NetworkBatch?> Batch(NetworkCoordinator adapter,int milliseconds=2000)
    {
        var deadline=DateTime.UtcNow.AddMilliseconds(milliseconds);
        while(DateTime.UtcNow<deadline) { await adapter.TickAsync(); if(adapter.Pending.TryDequeue(out var result))return result;await Task.Delay(25); }
        return null;
    }
    public static async Task Run(string docsRoot)
    {
        var factory=new MqttFactory();var port=Port();
        using var server=factory.CreateMqttServer(new MqttServerOptionsBuilder().WithDefaultEndpoint().WithDefaultEndpointPort(port).WithDefaultEndpointBoundIPAddress(IPAddress.Loopback).WithDefaultEndpointBoundIPV6Address(IPAddress.IPv6Loopback).Build());
        await server.StartAsync();
        var xml=XDocument.Parse(File.ReadAllText(Path.Combine(docsRoot,"fixtures/network/mqtt-json-roundtrip.phyphox")).Replace("FIXTURE-HOST:1883",$"127.0.0.1:{port}"));
        var network=xml.Root!.Elements().Single(e=>e.Name.LocalName=="network");
        network.Elements().Single().SetAttributeValue("persistence","true");
        using(var adapter=new NetworkCoordinator(network,_=>[7.25]))
        {
            Check(adapter.Issues.Count==0,"MQTT official fixture supported");adapter.Start();var result=await Batch(adapter);
            Check(result?.Writes.Single().Values.SequenceEqual([7.25])==true,"official MQTT JSON broker roundtrip QoS1");
            await server.StopAsync();await Task.Delay(100);Check(adapter.Status[0].Connected==false,"MQTT reports dropped broker connection");
            await server.StartAsync();var recovered=await Batch(adapter,3000);Check(recovered?.Writes.Single().Values.SequenceEqual([7.25])==true,"MQTT reconnects and restores subscription after broker restart");
            adapter.Stop();Check(adapter.Pending.IsEmpty,"MQTT stop discards parked messages");
        }
        var csv=XElement.Parse($"<network><connection address='127.0.0.1:{port}' service='mqtt/csv' conversion='csv' receiveTopic='fixture/csv' interval='0.1'><send id='fixture/csv' datatype='array'>out</send><receive id='1' append='false'>back</receive></connection></network>");
        using(var adapter=new NetworkCoordinator(csv,_=>[1,2.5,3]))
        {
            adapter.Start();var result=await Batch(adapter);Check(result?.Writes.Single().Values.SequenceEqual([2.5])==true&&!result.Writes.Single().Append,"MQTT CSV uses send ids as topics and receive column");
        }
        var receiveOnly=XElement.Parse($"<network><connection address='127.0.0.1:{port}' service='mqtt/csv' conversion='json' receiveTopic='fixture/bad'><receive id='v'>back</receive></connection></network>");
        using(var adapter=new NetworkCoordinator(receiveOnly,_=>[]))
        {
            adapter.Start();await adapter.TickAsync();
            await server.InjectApplicationMessage(new InjectedMqttApplicationMessage(new MqttApplicationMessageBuilder().WithTopic("fixture/bad").WithPayload("not-json").Build()));
            await Task.Delay(100);Check(adapter.Pending.IsEmpty&&adapter.Status[0].Error is not null,"malformed MQTT payload is contained");
            await server.InjectApplicationMessage(new InjectedMqttApplicationMessage(new MqttApplicationMessageBuilder().WithTopic("fixture/bad").WithPayload("{\"v\":5}").Build()));
            var result=await Batch(adapter);Check(result?.Writes.Single().Values.SequenceEqual([5d])==true,"MQTT interval-zero receive-only stays subscribed and recovers after bad payload");
        }
        await server.StopAsync();
        // Ephemeral CA and server key stay in memory; no global trust store changes.
        using var rootKey=RSA.Create(2048);var rootRequest=new CertificateRequest("CN=phyphox fixture CA",rootKey,HashAlgorithmName.SHA256,RSASignaturePadding.Pkcs1);
        rootRequest.CertificateExtensions.Add(new X509BasicConstraintsExtension(true,false,0,true));rootRequest.CertificateExtensions.Add(new X509KeyUsageExtension(X509KeyUsageFlags.KeyCertSign|X509KeyUsageFlags.CrlSign,true));
        using var root=rootRequest.CreateSelfSigned(DateTimeOffset.UtcNow.AddMinutes(-5),DateTimeOffset.UtcNow.AddDays(1));
        using var serverKey=RSA.Create(2048);var leafRequest=new CertificateRequest("CN=localhost",serverKey,HashAlgorithmName.SHA256,RSASignaturePadding.Pkcs1);
        leafRequest.CertificateExtensions.Add(new X509BasicConstraintsExtension(false,false,0,true));leafRequest.CertificateExtensions.Add(new X509KeyUsageExtension(X509KeyUsageFlags.DigitalSignature|X509KeyUsageFlags.KeyEncipherment,true));
        leafRequest.CertificateExtensions.Add(new X509EnhancedKeyUsageExtension(new OidCollection { new("1.3.6.1.5.5.7.3.1") },true));
        var san=new SubjectAlternativeNameBuilder();san.AddDnsName("localhost");leafRequest.CertificateExtensions.Add(san.Build());
        using var signed=leafRequest.Create(root,DateTimeOffset.UtcNow.AddMinutes(-1),DateTimeOffset.UtcNow.AddHours(1),RandomNumberGenerator.GetBytes(16));using var leaf=signed.CopyWithPrivateKey(serverKey);
        var tlsPort=Port();using var tlsServer=factory.CreateMqttServer(new MqttServerOptionsBuilder().WithoutDefaultEndpoint().WithEncryptedEndpoint().WithEncryptedEndpointPort(tlsPort).WithEncryptedEndpointBoundIPAddress(IPAddress.Loopback).WithEncryptedEndpointBoundIPV6Address(IPAddress.IPv6Loopback).WithEncryptionCertificate(leaf).Build());
        tlsServer.ValidatingConnectionAsync+=e=> { if(e.UserName!="fixture"||e.Password!="fixture-pass")e.ReasonCode=MqttConnectReasonCode.BadUserNameOrPassword;return Task.CompletedTask; };
        await tlsServer.StartAsync();
        XElement Tls(string host,string mode,bool certificate=true,string password="fixture-pass") => XElement.Parse($"<network><connection address='{host}:{tlsPort}' service='mqtts/{mode}' conversion='{mode}' username='fixture' password='{password}' {(certificate?"certificate='ca.pem'":"")} sendTopic='fixture/tls' receiveTopic='fixture/tls' interval='0.1'><send id='{(mode=="json"?"v":"fixture/tls")}' datatype='number'>out</send><receive id='{(mode=="json"?"v":"0")}'>back</receive></connection></network>");
        foreach(var mode in new[]{"json","csv"})
        {
            using var adapter=new NetworkCoordinator(Tls("localhost",mode),_=>[12.5],resource:_=>root.RawData){RequestTimeout=TimeSpan.FromSeconds(2)};
            adapter.Start();var result=await Batch(adapter,3000);Check(result?.Writes.Single().Values.SequenceEqual([12.5])==true,$"MQTTS {mode} custom-CA verified roundtrip");
        }
        foreach(var scenario in new[]{"untrusted","hostname","credentials"})
        {
            using var adapter=new NetworkCoordinator(Tls(scenario=="hostname"?"127.0.0.1":"localhost","json",scenario!="untrusted",scenario=="credentials"?"wrong":"fixture-pass"),_=>[1],resource:_=>root.RawData){RequestTimeout=TimeSpan.FromMilliseconds(500)};
            adapter.Start();await adapter.TickAsync();Check(adapter.Pending.IsEmpty&&adapter.Status[0].Error is not null,$"MQTTS rejects {scenario}");
        }
        await tlsServer.StopAsync();
        Console.WriteLine("MQTT/MQTTS protocol fixtures passed; no external broker or hardware acceptance claimed.");
    }
}
