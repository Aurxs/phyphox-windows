using System.Buffers.Binary;
using System.Globalization;
using System.Text;
namespace Phyphox.Devices;

/// <summary>Numeric input semantics ported from official Bluetooth/ConversionsInput.java (GPL-3.0).</summary>
public static class ByteConversions
{
    public static IReadOnlyList<double> Convert(ReadOnlySpan<byte> data,string conversion,int offset=0,int repeating=0,int length=0,string? decimalPoint=null) {
        if(offset<0 || repeating<0 || length<0) throw new ArgumentOutOfRangeException(nameof(offset));
        var result=new List<double>();
        for(var index=offset; index<data.Length;) {
            var n=length>0?Math.Min(length,data.Length-index):data.Length-index;
            try { result.Add(Read(data.Slice(index,n),conversion,decimalPoint)); }
            catch(ArgumentOutOfRangeException) { break; }
            catch(FormatException) { break; }
            if(repeating==0 || repeating>data.Length-index) break;
            index+=repeating;
        }
        return result;
    }
    public static double Read(ReadOnlySpan<byte> data,string conversion,string? decimalPoint=null) {
        var name=conversion.ToLowerInvariant();
        if(name=="string") {
            var s=Encoding.UTF8.GetString(data); if(!string.IsNullOrEmpty(decimalPoint)) s=s.Replace(decimalPoint,".",StringComparison.Ordinal);
            return double.Parse(s,NumberStyles.Float,CultureInfo.InvariantCulture);
        }
        if(name=="singlebyte") name="uint8";
        bool little=name.EndsWith("littleendian",StringComparison.Ordinal);
        var type=name.Replace("littleendian","",StringComparison.Ordinal).Replace("bigendian","",StringComparison.Ordinal);
        int size=type switch {"int8" or "uint8"=>1,"int16" or "uint16"=>2,"int24" or "uint24"=>3,"int32" or "uint32" or "float32"=>4,"float64"=>8,_=>throw new NotSupportedException($"Unknown conversion: {conversion}")};
        if(data.Length<size) throw new ArgumentOutOfRangeException(nameof(data));
        if(size>1 && !name.EndsWith("endian",StringComparison.Ordinal)) throw new NotSupportedException("Endian suffix required.");
        if(type=="float32") return BitConverter.Int32BitsToSingle(little?BinaryPrimitives.ReadInt32LittleEndian(data):BinaryPrimitives.ReadInt32BigEndian(data));
        if(type=="float64") return BitConverter.Int64BitsToDouble(little?BinaryPrimitives.ReadInt64LittleEndian(data):BinaryPrimitives.ReadInt64BigEndian(data));
        long value=0; for(int i=0;i<size;i++) value|=(long)data[i]<<(8*(little?i:size-1-i));
        if(type.StartsWith("int",StringComparison.Ordinal) && (value&(1L<<(size*8-1)))!=0) value-=1L<<(size*8);
        return value;
    }
    public static IReadOnlyList<double> FormattedString(ReadOnlySpan<byte> data,string? separator=null,string? label=null,int index=0) {
        if(index<0) throw new ArgumentOutOfRangeException(nameof(index));
        var text=Encoding.UTF8.GetString(data);
        var fields=string.IsNullOrEmpty(separator)?[text]:text.Split(separator,StringSplitOptions.None);
        // Java String.split removes trailing empty strings; these never produce valid numeric values.
        if(fields.Length<=index) return [];
        var value=string.IsNullOrEmpty(label)?fields[index]:fields.FirstOrDefault(x=>x.StartsWith(label,StringComparison.Ordinal))?[label.Length..];
        return double.TryParse(value,NumberStyles.Float,CultureInfo.InvariantCulture,out var number)?[number]:[];
    }
}

/// <summary>Serial reads have arbitrary chunk boundaries. Only explicit fixed-length or delimiter framing is supported.</summary>
public sealed class PacketFramer
{
    private readonly int fixedLength;
    private readonly byte[]? delimiter;
    private readonly int maximum;
    private readonly List<byte> pending=[];
    public PacketFramer(int fixedLength=0,byte[]? delimiter=null,int maximumFrameLength=65536) {
        if((fixedLength>0)==(delimiter is {Length:>0}) || fixedLength<0 || maximumFrameLength<1 || fixedLength>maximumFrameLength) throw new ArgumentException("Choose exactly one framing mode and a valid size limit.");
        this.fixedLength=fixedLength; this.delimiter=delimiter?.ToArray(); maximum=maximumFrameLength;
    }
    public IReadOnlyList<byte[]> Push(ReadOnlySpan<byte> bytes) {
        var result=new List<byte[]>();
        foreach(var b in bytes) {
            pending.Add(b);
            if(pending.Count>maximum) {pending.Clear();throw new InvalidDataException("Frame exceeds configured limit; data was not decoded.");}
            if(fixedLength>0 && pending.Count==fixedLength) {result.Add(pending.ToArray());pending.Clear();}
            else if(delimiter!=null && pending.Count>=delimiter.Length && pending.TakeLast(delimiter.Length).SequenceEqual(delimiter)) {result.Add(pending.Take(pending.Count-delimiter.Length).ToArray());pending.Clear();}
        }
        return result;
    }
    public void Reset()=>pending.Clear();
}
