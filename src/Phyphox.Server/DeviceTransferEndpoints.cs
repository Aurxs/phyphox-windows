// SPDX-License-Identifier: GPL-3.0-only
using System.Buffers.Binary;
using System.IO.Compression;
namespace Phyphox.Server;

public sealed record BleExperimentDownloadRequest(string DeviceId);
public static class DeviceTransferEndpoints
{
    public static void MapDeviceTransferEndpoints(this WebApplication app) {
        app.MapPost("/api/v1/devices/ble/experiment/download",async(BleExperimentDownloadRequest request,DeviceManager devices,CancellationToken ct)=> {
            var payload=NormalizePackage(await devices.DownloadExperimentAsync(request.DeviceId,ct));
            return Results.File(payload.Bytes,"application/octet-stream",payload.FileName);
        });
        app.MapPost("/api/v1/devices/ble/experiment/import",async(BleExperimentDownloadRequest request,DeviceManager devices,LibraryService library,CancellationToken ct)=> {
            var payload=NormalizePackage(await devices.DownloadExperimentAsync(request.DeviceId,ct));
            using var input=new MemoryStream(payload.Bytes,false);
            var imported=await library.ImportAsync(input,payload.FileName,ct);
            return Results.Ok(new {items=imported,source="physicalBleTransfer",crcVerified=true,hardwareCompatibilityVerified=false,started=false});
        });
    }
    /// <summary>Official BLE partial ZIP: one stored file followed by PK0708 CRC/size descriptor. Build a valid bounded archive, never an unchecked path.</summary>
    public static (byte[] Bytes,string FileName) NormalizePackage(byte[] bytes) {
        if(bytes.Length==0||bytes.Length>10_000_000)throw new InvalidDataException("BLE experiment size invalid.");
        if(bytes.Length>=4 && bytes.AsSpan(0,4).SequenceEqual(new byte[]{0x50,0x4b,3,4}))return(bytes,"ble-experiment.zip");
        if(bytes.Length>=16 && bytes.AsSpan(bytes.Length-16,4).SequenceEqual(new byte[]{0x50,0x4b,7,8})) {
            var descriptor=bytes.AsSpan(bytes.Length-12);var crc=BinaryPrimitives.ReadUInt32LittleEndian(descriptor);var compressed=BinaryPrimitives.ReadUInt32LittleEndian(descriptor[4..]);var size=BinaryPrimitives.ReadUInt32LittleEndian(descriptor[8..]);
            if(compressed!=bytes.Length-16||size!=compressed)throw new InvalidDataException("Unsupported or inconsistent partial ZIP sizes.");
            uint actual=0xffffffff;foreach(var b in bytes.AsSpan(0,bytes.Length-16)){actual^=b;for(int bit=0;bit<8;bit++)actual=(actual>>1)^((actual&1)==1?0xedb88320u:0);}
            if((actual^0xffffffff)!=crc)throw new InvalidDataException("Partial ZIP CRC mismatch.");
            using var output=new MemoryStream();using(var archive=new ZipArchive(output,ZipArchiveMode.Create,true)){var entry=archive.CreateEntry("a.phyphox",CompressionLevel.NoCompression);using var stream=entry.Open();stream.Write(bytes,0,bytes.Length-16);}
            return(output.ToArray(),"ble-experiment.zip");
        }
        return(bytes,"ble-experiment.phyphox");
    }
}
