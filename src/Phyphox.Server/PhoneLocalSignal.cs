// SPDX-License-Identifier: GPL-3.0-only
using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using Phyphox.Pairing;
namespace Phyphox.Server;

public static class PhoneLocalSignal
{
    static readonly SemaphoreSlim slots=new(4,4);
    public static void MapPhoneLocalSignal(this WebApplication app)
    {
        app.Map("/phone-signal",async context=>
        {
            var request=context.Request;
            if(!context.WebSockets.IsWebSocketRequest||request.Headers.Origin.ToString()!=request.Scheme+"://"+request.Host.Value||request.QueryString.HasValue)
            {context.Response.StatusCode=403;return;}
            var gateway=context.RequestServices.GetRequiredService<PhoneLocalGateway>();
            if(!gateway.Enabled){context.Response.StatusCode=409;return;}
            if(!slots.Wait(0)){context.Response.StatusCode=429;return;}
            try
            {
                using var socket=await context.WebSockets.AcceptWebSocketAsync();
                var lease=context.RequestServices.GetRequiredService<PhoneBridgeLease>();
                var hub=gateway.Hub;
                Peer? peer=null;
                PhoneBridgeLease.SignalAuthority? authority=null;
                try
                {
                    using var authTimeout=CancellationTokenSource.CreateLinkedTokenSource(context.RequestAborted);
                    authTimeout.CancelAfter(TimeSpan.FromSeconds(5));
                    using var authentication=JsonDocument.Parse(await Read(socket,512,authTimeout.Token),new JsonDocumentOptions{MaxDepth=3});
                    var fields=authentication.RootElement;
                    if(fields.ValueKind!=JsonValueKind.Object||fields.EnumerateObject().Count()!=2||fields.GetProperty("type").GetString()!="authenticate")throw new InvalidDataException();
                    authority=lease.ConsumeSignalTicket(fields.GetProperty("token").GetString()??"");
                    peer=new Peer(true);
                    using var lifetime=CancellationTokenSource.CreateLinkedTokenSource(context.RequestAborted,authority.Cancellation,peer.Stop.Token);
                    lifetime.CancelAfter(TimeSpan.FromMinutes(12));
                    async Task Write()
                    {
                        await foreach(var message in peer.Outbox.Reader.ReadAllAsync(lifetime.Token))
                        {
                            using var timeout=CancellationTokenSource.CreateLinkedTokenSource(lifetime.Token);timeout.CancelAfter(TimeSpan.FromSeconds(5));
                            await socket.SendAsync(Encoding.UTF8.GetBytes(message),WebSocketMessageType.Text,true,timeout.Token);
                        }
                    }
                    async Task Receive()
                    {
                        while(!lifetime.IsCancellationRequested)
                        {
                            using var timeout=CancellationTokenSource.CreateLinkedTokenSource(lifetime.Token);timeout.CancelAfter(TimeSpan.FromSeconds(125));
                            var message=await Read(socket,65536,timeout.Token);
                            await lease.RunOwned(authority.LeaseId,()=>{hub.Handle(peer,message);return Task.CompletedTask;},lifetime.Token);
                        }
                    }
                    var writer=Write();var reader=Receive();
                    try{await await Task.WhenAny(writer,reader);}
                    finally
                    {
                        hub.Disconnect(peer);await lifetime.CancelAsync();
                        try{await Task.WhenAll(writer,reader);}catch(Exception ex)when(Expected(ex)){}
                    }
                }
                catch(Exception ex)when(Expected(ex)){}
                finally{if(peer is not null){hub.Disconnect(peer);peer.Stop.Dispose();}socket.Abort();}
            }
            finally{slots.Release();}
        });
    }
    static bool Expected(Exception ex)=>ex is WebSocketException or OperationCanceledException or InvalidDataException or DecoderFallbackException or JsonException or InvalidOperationException or KeyNotFoundException;
    static async Task<string> Read(WebSocket socket,int maximum,CancellationToken ct)
    {
        var buffer=new byte[maximum+1];int length=0;
        WebSocketReceiveResult result;
        do
        {
            result=await socket.ReceiveAsync(new ArraySegment<byte>(buffer,length,buffer.Length-length),ct);
            if(result.MessageType!=WebSocketMessageType.Text)throw new InvalidDataException();
            length+=result.Count;if(length>maximum)throw new InvalidDataException();
        }while(!result.EndOfMessage);
        return new UTF8Encoding(false,true).GetString(buffer,0,length);
    }
}
