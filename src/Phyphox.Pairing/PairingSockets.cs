using System.Net.WebSockets;
using System.Text;

namespace Phyphox.Pairing;

public static class PairingSockets
{
    private static readonly SemaphoreSlim Slots = new(256, 256);
    public static async Task RunAsync(WebSocket socket, PairingHub hub, Peer peer, CancellationToken requestAborted)
    {
        if (!Slots.Wait(0)) { socket.Abort(); return; }
        using var lifetime = CancellationTokenSource.CreateLinkedTokenSource(requestAborted, peer.Stop.Token);
        lifetime.CancelAfter(TimeSpan.FromMinutes(12));
        var cancellation = lifetime.Token;
        async Task Write()
        {
            await foreach (var message in peer.Outbox.Reader.ReadAllAsync(cancellation))
            {
                using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellation);
                timeout.CancelAfter(TimeSpan.FromSeconds(5));
                await socket.SendAsync(Encoding.UTF8.GetBytes(message), WebSocketMessageType.Text, true, timeout.Token);
            }
        }
        async Task Read()
        {
            var buffer = new byte[65537];
            while (!cancellation.IsCancellationRequested)
            {
                var length = 0;
                using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellation);
                timeout.CancelAfter(TimeSpan.FromSeconds(125));
                WebSocketReceiveResult result;
                do
                {
                    result = await socket.ReceiveAsync(new ArraySegment<byte>(buffer, length, buffer.Length - length), timeout.Token);
                    if (result.MessageType == WebSocketMessageType.Close) return;
                    if (result.MessageType != WebSocketMessageType.Text) throw new InvalidDataException();
                    length += result.Count;
                    if (length > 65536) throw new InvalidDataException();
                } while (!result.EndOfMessage);
                hub.Handle(peer, new UTF8Encoding(false, true).GetString(buffer, 0, length));
            }
        }
        var writer = Write(); var reader = Read();
        try { await await Task.WhenAny(writer, reader); }
        catch (Exception ex) when (Expected(ex)) { }
        finally
        {
            hub.Disconnect(peer); lifetime.Cancel();
            try { await Task.WhenAll(writer, reader); } catch (Exception ex) when (Expected(ex)) { }
            socket.Abort(); peer.Stop.Dispose(); Slots.Release();
        }
    }
    private static bool Expected(Exception exception) => exception is WebSocketException or OperationCanceledException or InvalidDataException or DecoderFallbackException;
}
