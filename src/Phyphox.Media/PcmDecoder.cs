using System.Buffers.Binary;
namespace Phyphox.Media;

public static class PcmDecoder
{
    public static float[] DecodeMono(ReadOnlySpan<byte> bytes,int channels,int bitsPerSample,bool ieeeFloat) {
        if(channels<1 || bitsPerSample is not (8 or 16 or 24 or 32 or 64) || (ieeeFloat && bitsPerSample is not(32 or 64)) || (!ieeeFloat && bitsPerSample==64))throw new NotSupportedException("Unsupported PCM encoding.");
        int sampleBytes=bitsPerSample/8,block=channels*sampleBytes;
        if(bytes.Length%block!=0)throw new InvalidDataException("Audio packet is not frame-aligned.");
        var output=new float[bytes.Length/block];
        for(int i=0;i<output.Length;i++) {
            double value=0;
            for(int c=0;c<channels;c++) {
                var sample=bytes.Slice(i*block+c*sampleBytes,sampleBytes);
                if(ieeeFloat)value+=bitsPerSample==32?BitConverter.Int32BitsToSingle(BinaryPrimitives.ReadInt32LittleEndian(sample)):BitConverter.Int64BitsToDouble(BinaryPrimitives.ReadInt64LittleEndian(sample));
                else value+=bitsPerSample switch {8=>(sample[0]-128)/128d,16=>BinaryPrimitives.ReadInt16LittleEndian(sample)/32768d,24=>((sample[0]|sample[1]<<8|sample[2]<<16)<<8>>8)/8388608d,32=>BinaryPrimitives.ReadInt32LittleEndian(sample)/2147483648d,_=>0};
            }
            output[i]=(float)(value/channels);
        }
        return output;
    }
}
