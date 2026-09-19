using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Threading.Channels;

namespace Phyphox.Pairing;

public sealed class Peer(bool desktop)
{
    public bool Desktop { get; } = desktop;
    public Channel<string> Outbox { get; } = Channel.CreateBounded<string>(128);
    public CancellationTokenSource Stop { get; } = new();
    internal int Count;
    internal DateTimeOffset Window;
    internal int Signals;
    internal void Send(object message)
    {
        if (!Outbox.Writer.TryWrite(JsonSerializer.Serialize(message))) Stop.Cancel();
    }
}

// All state transitions and queue insertion share one lock; writers serialize socket sends.
public sealed class PairingHub(TimeProvider? clock = null)
{
    private sealed class Room(string id, byte[] token, Peer owner, DateTimeOffset deadline)
    {
        public string Id = id;
        public byte[] Token = token;
        public Peer Owner = owner;
        public Peer? Phone;
        public DateTimeOffset Deadline = deadline;
        public bool Paired;
    }
    private readonly TimeProvider clock = clock ?? TimeProvider.System;
    private readonly object gate = new();
    private readonly Dictionary<string, Room> rooms = [];
    private readonly Dictionary<Peer, Room> membership = [];

    public void Handle(Peer peer, string text)
    {
        lock (gate)
        {
            try
            {
                SweepCore();
                var now = clock.GetUtcNow();
                if (now - peer.Window >= TimeSpan.FromSeconds(1)) { peer.Window = now; peer.Count = 0; }
                if (++peer.Count > 40 || Encoding.UTF8.GetByteCount(text) > 65536) throw new ArgumentException();
                using var document = JsonDocument.Parse(text, new JsonDocumentOptions { MaxDepth = 8 });
                var root = document.RootElement;
                var type = root.GetProperty("type").GetString();
                membership.TryGetValue(peer, out var room);
                switch (type)
                {
                    case "create":
                        if (!peer.Desktop || room != null || rooms.Count >= 128 || root.GetProperty("version").GetInt32() != 1) throw new ArgumentException();
                        var id = Convert.ToHexString(RandomNumberGenerator.GetBytes(16)).ToLowerInvariant();
                        var secret = RandomNumberGenerator.GetBytes(32);
                        room = new Room(id, secret, peer, now.AddSeconds(120));
                        rooms.Add(id, room); membership.Add(peer, room);
                        peer.Send(new { type = "created", roomId = id, joinToken = Convert.ToHexString(secret).ToLowerInvariant(), expiresAt = room.Deadline });
                        break;
                    case "join":
                        if (peer.Desktop || room != null || root.GetProperty("version").GetInt32() != 1) throw new ArgumentException();
                        var roomId = root.GetProperty("roomId").GetString() ?? "";
                        var token = root.GetProperty("joinToken").GetString() ?? "";
                        var name = root.GetProperty("name").GetString() ?? "";
                        if (roomId.Length != 32 || token.Length != 64 || name.Length is < 1 or > 80 || name.Any(char.IsControl)) throw new ArgumentException();
                        var supplied = Convert.FromHexString(token);
                        if (!rooms.TryGetValue(roomId, out room) || room.Phone != null || !CryptographicOperations.FixedTimeEquals(room.Token, supplied)) throw new ArgumentException();
                        CryptographicOperations.ZeroMemory(room.Token); room.Token = [];
                        room.Phone = peer; room.Deadline = now.AddSeconds(60); membership.Add(peer, room);
                        room.Owner.Send(new { type = "join-request", name });
                        break;
                    case "accept":
                        if (room == null || room.Owner != peer || room.Phone == null || room.Paired) throw new ArgumentException();
                        room.Paired = true; room.Deadline = now.AddMinutes(10);
                        room.Owner.Send(new { type = "paired" }); room.Phone.Send(new { type = "paired" });
                        break;
                    case "reject":
                        if (room == null || room.Owner != peer || room.Paired) throw new ArgumentException();
                        Remove(room);
                        break;
                    case "signal":
                        if (room == null || !room.Paired || ++peer.Signals > 256) throw new ArgumentException();
                        var data = root.GetProperty("data");
                        if (!ValidSignal(data)) throw new ArgumentException();
                        (peer == room.Owner ? room.Phone! : room.Owner).Send(new { type = "signal", data });
                        break;
                    default: throw new ArgumentException();
                }
            }
            catch (Exception ex) when (ex is JsonException or ArgumentException or InvalidOperationException or KeyNotFoundException or FormatException or OverflowException)
            {
                peer.Send(new { type = "error", message = "Invalid or expired pairing request." });
                DisconnectCore(peer);
                peer.Outbox.Writer.TryComplete();
            }
        }
    }

    public static bool ValidSignal(JsonElement data)
    {
        if (data.ValueKind != JsonValueKind.Object || data.EnumerateObject().Count() != 1) return false;
        if (data.TryGetProperty("description", out var d))
        {
            if (d.ValueKind != JsonValueKind.Object || d.EnumerateObject().Any(p => p.Name is not ("type" or "sdp"))) return false;
            if (!d.TryGetProperty("type", out var type) || type.ValueKind != JsonValueKind.String || type.GetString() is not ("offer" or "answer")) return false;
            if (!d.TryGetProperty("sdp", out var sdp) || sdp.ValueKind != JsonValueKind.String || sdp.GetString() is not { Length: > 0 and <= 49152 } value) return false;
            return value.StartsWith("v=0\r\n", StringComparison.Ordinal) && value.Split('\n').Where(l => l.StartsWith("a=candidate:", StringComparison.Ordinal)).All(l => HostCandidate(l[2..].TrimEnd('\r')));
        }
        if (!data.TryGetProperty("candidate", out var c)) return false;
        if (c.ValueKind == JsonValueKind.Null) return true;
        if (c.ValueKind != JsonValueKind.Object || c.EnumerateObject().Any(p => p.Name is not ("candidate" or "sdpMid" or "sdpMLineIndex" or "usernameFragment"))) return false;
        if (!c.TryGetProperty("candidate", out var candidate) || candidate.ValueKind != JsonValueKind.String || candidate.GetString() is not { Length: <= 2048 } line || (line.Length > 0 && !HostCandidate(line))) return false;
        foreach (var name in new[] { "sdpMid", "usernameFragment" })
            if (c.TryGetProperty(name, out var s) && s.ValueKind != JsonValueKind.Null && (s.ValueKind != JsonValueKind.String || s.GetString()!.Length > 256)) return false;
        return !c.TryGetProperty("sdpMLineIndex", out var i) || i.ValueKind == JsonValueKind.Null || (i.TryGetInt32(out var index) && index is >= 0 and <= 32);
    }
    private static bool HostCandidate(string value) => value.StartsWith("candidate:", StringComparison.Ordinal) && !value.Contains('\r') && !value.Contains('\n') && value.Split(' ', StringSplitOptions.RemoveEmptyEntries) is { Length: >= 8 } parts && parts[6] == "typ" && parts[7] == "host";
    public void Reset() { lock (gate) foreach (var room in rooms.Values.ToArray()) Remove(room); }
    public void Sweep() { lock (gate) SweepCore(); }
    private void SweepCore() { foreach (var room in rooms.Values.Where(r => r.Deadline <= clock.GetUtcNow()).ToArray()) Remove(room); }
    public void Disconnect(Peer peer) { lock (gate) DisconnectCore(peer); }
    private void DisconnectCore(Peer peer) { if (membership.TryGetValue(peer, out var room)) Remove(room); }
    private void Remove(Room room)
    {
        rooms.Remove(room.Id); membership.Remove(room.Owner);
        CryptographicOperations.ZeroMemory(room.Token); room.Token = [];
        room.Owner.Send(new { type = "peer-left" }); room.Owner.Outbox.Writer.TryComplete();
        if (room.Phone is { } phone) { membership.Remove(phone); phone.Send(new { type = "peer-left" }); phone.Outbox.Writer.TryComplete(); }
    }
}
