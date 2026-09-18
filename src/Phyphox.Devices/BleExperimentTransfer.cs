using System.Buffers.Binary;
namespace Phyphox.Devices;

/// <summary>Official phyphox BLE file-transfer framing. Receives already framed GATT values, verifies size and CRC32; never treats bytes as an approved experiment.</summary>
public sealed class BleExperimentTransfer
{
    private byte[]? payload;
    private int offset;
    private uint expectedCrc;
    public int Received=>offset;
    public int? Expected=>payload?.Length;
    public bool Complete=>payload!=null && offset==payload.Length;
    public void Receive(ReadOnlySpan<byte> packet) {
        if(payload==null) {
            if(packet.Length<15 || !packet[..7].SequenceEqual("phyphox"u8)) throw new InvalidDataException("Invalid phyphox BLE header.");
            var size=BinaryPrimitives.ReadUInt32BigEndian(packet[7..11]);
            if(size>10_000_000) throw new InvalidDataException("BLE experiment exceeds official 10 MB limit.");
            expectedCrc=BinaryPrimitives.ReadUInt32BigEndian(packet[11..15]); payload=new byte[(int)size]; return;
        }
        if(Complete) throw new InvalidOperationException("Transfer already complete.");
        var n=Math.Min(packet.Length,payload.Length-offset); packet[..n].CopyTo(payload.AsSpan(offset)); offset+=n;
    }
    public byte[] GetVerifiedPayload() {
        if(!Complete || payload==null) throw new InvalidOperationException("Transfer incomplete.");
        uint crc=0xffffffff;
        foreach(var value in payload) { crc^=value; for(int bit=0;bit<8;bit++) crc=(crc>>1)^((crc&1)==1?0xedb88320u:0u); }
        if((crc^0xffffffff)!=expectedCrc) throw new InvalidDataException("BLE experiment CRC32 mismatch.");
        return payload.ToArray();
    }
}
