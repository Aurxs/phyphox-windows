using System.Text.Json;
using Phyphox.Pairing;

static void Assert(bool condition, string message) { if (!condition) throw new Exception(message); }
static JsonElement Read(Peer peer)
{
    Assert(peer.Outbox.Reader.TryRead(out var value), "Expected outbound message");
    return JsonDocument.Parse(value!).RootElement.Clone();
}
static void Send(PairingHub hub, Peer peer, object value) => hub.Handle(peer, JsonSerializer.Serialize(value));
static JsonElement Create(PairingHub hub, Peer peer) { Send(hub, peer, new { type = "create", version = 1 }); return Read(peer); }
static void Join(PairingHub hub, Peer peer, JsonElement room) => Send(hub, peer, new { type = "join", version = 1, roomId = room.GetProperty("roomId").GetString(), joinToken = room.GetProperty("joinToken").GetString(), name = "Phone" });
var time = new TestClock();
var hub = new PairingHub(time);
var desktop = new Peer(true); var phone = new Peer(false);
var credentials = Create(hub, desktop);
Assert(credentials.GetProperty("joinToken").GetString()!.Length == 64, "256-bit token");
Join(hub, phone, credentials);
Assert(Read(desktop).GetProperty("type").GetString() == "join-request", "Consent gate");
var replay = new Peer(false); Join(hub, replay, credentials);
Assert(Read(replay).GetProperty("type").GetString() == "error", "Single use");
Send(hub, desktop, new { type = "accept" });
Assert(Read(desktop).GetProperty("type").GetString() == "paired" && Read(phone).GetProperty("type").GetString() == "paired", "Paired both");
Send(hub, phone, new { type = "signal", data = new { candidate = new { candidate = "candidate:1 1 UDP 1 192.168.1.2 1234 typ host", sdpMid = "0", sdpMLineIndex = 0 } } });
Assert(Read(desktop).GetProperty("type").GetString() == "signal", "Relay after consent");
Send(hub, phone, new { type = "signal", data = new { audio = "not signaling" } });
Assert(Read(phone).GetProperty("type").GetString() == "error", "Arbitrary payload rejected");
Assert(Read(desktop).GetProperty("type").GetString() == "peer-left", "Invalid peer revoked");
var expiryOwner = new Peer(true); var expiryRoom = Create(hub, expiryOwner);
time.Advance(121); hub.Sweep();
Assert(Read(expiryOwner).GetProperty("type").GetString() == "peer-left", "QR expires");
var late = new Peer(false); Join(hub, late, expiryRoom);
Assert(Read(late).GetProperty("type").GetString() == "error", "Expired credential rejected");
var confirmationOwner = new Peer(true); var confirmationPhone = new Peer(false);
Join(hub, confirmationPhone, Create(hub, confirmationOwner)); Read(confirmationOwner);
time.Advance(61); hub.Sweep();
Assert(Read(confirmationOwner).GetProperty("type").GetString() == "peer-left" && Read(confirmationPhone).GetProperty("type").GetString() == "peer-left", "Confirmation timeout");
foreach (var invalid in new[] { "{\"candidate\":{\"candidate\":\"candidate:1 1 UDP 1 1.2.3.4 1 typ relay\"}}", "{\"description\":{\"type\":\"offer\",\"sdp\":\"arbitrary data\"}}", JsonSerializer.Serialize(new { description = new { type = "offer", sdp = "v=0\r\n" + new string('a', 49152) } }) })
    Assert(!PairingHub.ValidSignal(JsonDocument.Parse(invalid).RootElement), "Signal limits");
Console.WriteLine("Pairing focused checks passed: consent, single use, relay whitelist, expiry, host candidates, bounds.");
sealed class TestClock : TimeProvider
{
    private DateTimeOffset now = DateTimeOffset.Parse("2026-09-19T00:00:00Z");
    public override DateTimeOffset GetUtcNow() => now;
    public void Advance(int seconds) => now = now.AddSeconds(seconds);
}
