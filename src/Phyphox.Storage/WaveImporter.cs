// SPDX-License-Identifier: GPL-3.0-or-later
using System.Buffers.Binary;
using System.Diagnostics;
using System.Text;

namespace Phyphox.Storage;
public sealed record WaveImportResult(int SampleRate, int Channels, int BitsPerSample, string Encoding, ImportedData Data);
public static class WaveImporter
{
    public static WaveImportResult Import(Stream source, string bufferPrefix = "audio")
    {
        if (!source.CanRead || !source.CanSeek)
            throw new ArgumentException("WAV stream must be readable and seekable.");
        if (string.IsNullOrWhiteSpace(bufferPrefix))
            throw new ArgumentException("Buffer prefix is required.");
        using var reader = new BinaryReader(source, Encoding.ASCII, true);
        long start = source.Position;
        if (Text(reader, 4) != "RIFF")
            throw new FormatException("Only little-endian RIFF WAV is supported.");
        uint riffSize = reader.ReadUInt32();
        if (Text(reader, 4) != "WAVE")
            throw new FormatException("Not a WAVE file.");
        long end = start + 8 + riffSize;
        if (end > source.Length || end < source.Position)
            throw new FormatException("Truncated RIFF payload.");
        byte[]? format = null;
        using var data = new MemoryStream();
        while (source.Position + 8 <= end)
        {
            string tag = Text(reader, 4);
            uint size = reader.ReadUInt32();
            long next = source.Position + size;
            if (next > end)
                throw new FormatException($"Truncated WAV chunk {tag}.");
            if (size > 128_000_000)
                throw new FormatException("WAV chunk exceeds import memory budget.");
            if (tag == "fmt ")
            {
                if (format != null)
                    throw new FormatException("Duplicate WAV format chunk.");
                format = Read(reader, (int)size);
            }
            else if (tag == "data")
            {
                if (data.Length + size > 128_000_000)
                    throw new FormatException("WAV audio exceeds import memory budget.");
                data.Write(Read(reader, (int)size));
            }
            else
                source.Position = next;
            source.Position = next + (size & 1);
            if (source.Position > end)
                throw new FormatException("Missing WAV chunk alignment byte.");
        }

        if (format == null || format.Length < 16 || data.Length == 0)
            throw new FormatException("WAV requires format and nonempty audio data.");
        ushort encoding = U16(format, 0), channels = U16(format, 2), alignment = U16(format, 12), bits = U16(format, 14);
        uint rate = U32(format, 4), byteRate = U32(format, 8);
        if (encoding == 0xfffe)
        {
            if (format.Length < 40 || U16(format, 16) < 22 || U16(format, 18) != bits)
                throw new NotSupportedException("Unsupported WAV extensible valid-bit layout.");
            var guid = new Guid(format.AsSpan(24, 16));
            if (guid == new Guid("00000001-0000-0010-8000-00aa00389b71"))
                encoding = 1;
            else if (guid == new Guid("00000003-0000-0010-8000-00aa00389b71"))
                encoding = 3;
            else
                throw new NotSupportedException("Unsupported WAV extensible codec.");
        }

        if (encoding is not (1 or 3) || encoding == 1 && bits is not (8 or 16 or 24 or 32) || encoding == 3 && bits is not (32 or 64))
            throw new NotSupportedException("Only PCM 8/16/24/32 and IEEE float 32/64 WAV are supported.");
        if (channels == 0 || channels > 32 || rate == 0 || rate > 768000 || alignment != channels * (bits / 8) || byteRate != (long)rate * alignment || data.Length % alignment != 0)
            throw new FormatException("Inconsistent WAV frame metadata.");
        int count = checked((int)(data.Length / alignment));
        if ((long)count * (channels + 1) > 32_000_000)
            throw new InvalidOperationException("Decoded WAV exceeds sample memory budget.");
        byte[] pcm = data.ToArray();
        var buffers = Enumerable.Range(0, channels).ToDictionary(c => $"{bufferPrefix}{c + 1}", _ => new double[count]);
        var units = buffers.Keys.ToDictionary(k => k, _ => (string? )"normalized PCM");
        for (int i = 0; i < count; i++)
            for (int c = 0; c < channels; c++)
            {
                int offset = i * alignment + c * (bits / 8);
                double value;
                if (encoding == 3)
                    value = bits == 32 ? BitConverter.Int32BitsToSingle(BinaryPrimitives.ReadInt32LittleEndian(pcm.AsSpan(offset, 4))) : BitConverter.Int64BitsToDouble(BinaryPrimitives.ReadInt64LittleEndian(pcm.AsSpan(offset, 8)));
                else
                    value = bits switch
                    {
                        8 => (pcm[offset] - 128) / 128.0,
                        16 => BinaryPrimitives.ReadInt16LittleEndian(pcm.AsSpan(offset, 2)) / 32768.0,
                        24 => ((pcm[offset] | pcm[offset + 1] << 8 | pcm[offset + 2] << 16) << 8 >> 8) / 8388608.0,
                        32 => BinaryPrimitives.ReadInt32LittleEndian(pcm.AsSpan(offset, 4)) / 2147483648.0,
                        _ => throw new UnreachableException()};
                buffers[$"{bufferPrefix}{c + 1}"][i] = value;
            }

        buffers.Add("time", Enumerable.Range(0, count).Select(i => i / (double)rate).ToArray());
        units.Add("time", "s");
        return new((int)rate, channels, bits, encoding == 1 ? "PCM" : "IEEE_FLOAT", new(buffers, count, "wav-sample-rate", units));
    }

    static byte[] Read(BinaryReader r, int count)
    {
        var value = r.ReadBytes(count);
        if (value.Length != count)
            throw new EndOfStreamException("Truncated WAV data.");
        return value;
    }

    static string Text(BinaryReader r, int count) => Encoding.ASCII.GetString(Read(r, count));
    static ushort U16(byte[] data, int index) => BinaryPrimitives.ReadUInt16LittleEndian(data.AsSpan(index, 2));
    static uint U32(byte[] data, int index) => BinaryPrimitives.ReadUInt32LittleEndian(data.AsSpan(index, 4));
}
