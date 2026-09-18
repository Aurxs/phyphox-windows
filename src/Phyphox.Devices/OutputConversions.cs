using System.Globalization;
using System.Text;
namespace Phyphox.Devices;

public static class OutputConversions
{
    private static int JavaInt(double n)=>double.IsNaN(n)?0:n>=int.MaxValue?int.MaxValue:n<=int.MinValue?int.MinValue:(int)n;
    private static long JavaLong(double n)=>double.IsNaN(n)?0:n>=long.MaxValue?long.MaxValue:n<=long.MinValue?long.MinValue:(long)n;
    public static byte[] Convert(IReadOnlyList<double> values,string conversion) {
        if(conversion.Equals("bytearray",StringComparison.OrdinalIgnoreCase)) return values.Select(x=>unchecked((byte)JavaInt(x))).ToArray();
        return Encode(values.Count>0?values[^1]:double.NaN,conversion);
    }
    public static byte[] Encode(double value,string conversion) {
        var name=conversion.ToLowerInvariant();
        if(name=="string") return Encoding.UTF8.GetBytes(FormatDouble(value));
        if(name=="singlebyte") name="int8";
        var little=name.EndsWith("littleendian",StringComparison.Ordinal);
        var type=name.Replace("littleendian","",StringComparison.Ordinal).Replace("bigendian","",StringComparison.Ordinal);
        int size=type switch {"int8" or "uint8"=>1,"int16" or "uint16"=>2,"int24" or "uint24"=>3,"int32" or "uint32" or "float32"=>4,"float64"=>8,_=>throw new NotSupportedException($"Unknown output conversion: {conversion}")};
        if(size>1 && !name.EndsWith("endian",StringComparison.Ordinal)) throw new NotSupportedException("Endian suffix required.");
        long bits=type switch {"float32"=>double.IsNaN(value)?0x7fc00000:BitConverter.SingleToInt32Bits((float)value),"float64"=>double.IsNaN(value)?0x7ff8000000000000:BitConverter.DoubleToInt64Bits(value),"uint32"=>JavaLong(value),_=>JavaInt(value)};
        var result=new byte[size]; for(int i=0;i<size;i++) result[little?i:size-1-i]=unchecked((byte)(bits>>(i*8)));return result;
    }
    public static byte[]? Config(string value,string conversion) {
        var name=conversion.ToLowerInvariant();
        if(name=="string") return Encoding.UTF8.GetBytes(value);
        if(name=="hexadecimal") {
            var hex=value.Replace(" ","",StringComparison.Ordinal); var result=new byte[hex.Length/2];
            for(int i=0;i<result.Length;i++) if(!byte.TryParse(hex.AsSpan(i*2,2),NumberStyles.HexNumber,CultureInfo.InvariantCulture,out result[i])) return [];
            return result;
        }
        if(name is "singlebyte" or "int8" or "uint8") return int.TryParse(value,NumberStyles.AllowLeadingSign,CultureInfo.InvariantCulture,out var integer)?[unchecked((byte)integer)]:null;
        if(!double.TryParse(value,NumberStyles.Float,CultureInfo.InvariantCulture,out var n)) return null;
        return Encode(n,name);
    }
    private static string FormatDouble(double n) {
        if(double.IsNaN(n))return "NaN";if(double.IsPositiveInfinity(n))return "Infinity";if(double.IsNegativeInfinity(n))return "-Infinity";
        if(n==0)return BitConverter.DoubleToInt64Bits(n)<0?"-0.0":"0.0";
        var raw=Math.Abs(n).ToString("R",CultureInfo.InvariantCulture);
        var parts=raw.Split('E');var mantissa=parts[0];int exponent=parts.Length==2?int.Parse(parts[1],CultureInfo.InvariantCulture):0;
        int dot=mantissa.IndexOf('.');if(dot<0)dot=mantissa.Length;
        var all=mantissa.Replace(".","",StringComparison.Ordinal);int leading=0;while(leading<all.Length && all[leading]=='0')leading++;
        exponent+=dot-leading-1;var digits=all[leading..].TrimEnd('0');var sign=n<0?"-":"";
        if(exponent>=-3 && exponent<7) {
            int decimalIndex=exponent+1;
            if(decimalIndex<=0)return sign+"0."+new string('0',-decimalIndex)+digits;
            if(decimalIndex>=digits.Length)return sign+digits+new string('0',decimalIndex-digits.Length)+".0";
            return sign+digits[..decimalIndex]+"."+digits[decimalIndex..];
        }
        return sign+digits[0]+"."+(digits.Length>1?digits[1..]:"0")+"E"+exponent.ToString(CultureInfo.InvariantCulture);
    }
}
