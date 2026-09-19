// SPDX-License-Identifier: GPL-3.0-only
using System.Buffers.Binary;
using System.IO.Compression;
using System.Text;
namespace Phyphox.Server;

// The decoder does not apply ICC transforms. Accept only the sRGB matrix/TRC
// representation, never a profile's human-readable name or an asserted label.
public static class BrowserImageColor
{
    public static void RequireSrgb(ReadOnlySpan<byte> image,bool png)
    {
        if(png){CheckPng(image);return;}
        var parts=new SortedDictionary<int,byte[]>();int total=0,offset=2;
        while(offset<image.Length)
        {
            if(image[offset++]!=255)throw new ArgumentException("JPEG标记无效。");
            while(offset<image.Length&&image[offset]==255)offset++;
            if(offset>=image.Length)throw new ArgumentException("JPEG标记截断。");
            var marker=image[offset++];
            if(marker is 0xDA or 0xD9)break;
            if(marker==0x01||marker is >=0xD0 and <=0xD8)continue;
            if(offset+2>image.Length)throw new ArgumentException("JPEG段截断。");
            int length=BinaryPrimitives.ReadUInt16BigEndian(image[offset..]);
            if(length<2||length>image.Length-offset)throw new ArgumentException("JPEG段长度无效。");
            var segment=image.Slice(offset+2,length-2);offset+=length;
            if(marker!=0xE2||!segment.StartsWith("ICC_PROFILE\0"u8))continue;
            if(segment.Length<14||segment[12]==0||segment[13]==0||segment[13]>16||segment[12]>segment[13]||(total!=0&&total!=segment[13])||parts.ContainsKey(segment[12]))throw new NotSupportedException("JPEG ICC分段无效。");
            total=segment[13];parts.Add(segment[12],segment[14..].ToArray());
        }
        if(total==0)return;
        if(parts.Count!=total||parts.Sum(p=>p.Value.Length)>65536)throw new NotSupportedException("JPEG ICC配置不完整或超限。");
        RequireSrgbProfile(parts.Values.SelectMany(v=>v).ToArray());
    }
    static void CheckPng(ReadOnlySpan<byte> image)
    {
        int offset=8;bool profileSeen=false;
        while(offset+12<=image.Length)
        {
            uint length=BinaryPrimitives.ReadUInt32BigEndian(image[offset..]);
            if(length>image.Length-offset-12)throw new ArgumentException("PNG块长度无效。");
            var type=image.Slice(offset+4,4);var data=image.Slice(offset+8,(int)length);offset+=12+(int)length;
            if(type.SequenceEqual("iCCP"u8))
            {
                if(profileSeen)throw new NotSupportedException("PNG包含重复ICC配置。");profileSeen=true;
                int nameEnd=data.IndexOf((byte)0);
                if(nameEnd is <1 or >79||nameEnd+2>data.Length||data[nameEnd+1]!=0)throw new NotSupportedException("PNG ICC配置无效。");
                using var source=new MemoryStream(data[(nameEnd+2)..].ToArray());using var zlib=new ZLibStream(source,CompressionMode.Decompress);using var target=new MemoryStream();
                var buffer=new byte[4096];int count;
                while((count=zlib.Read(buffer))>0){if(target.Length+count>65536)throw new NotSupportedException("PNG ICC配置超限。");target.Write(buffer,0,count);}
                RequireSrgbProfile(target.ToArray());
            }
            if(type.SequenceEqual("IEND"u8))break;
        }
    }
    public static void RequireSrgbProfile(ReadOnlySpan<byte> profile)
    {
        const string message="相机ICC不是受支持的标准sRGB矩阵/TRC配置；请在手机使用sRGB画布。";
        void Reject()=>throw new NotSupportedException(message);
        if(profile.Length<132||profile.Length>65536||BinaryPrimitives.ReadUInt32BigEndian(profile)!=profile.Length||!profile.Slice(12,4).SequenceEqual("mntr"u8)||!profile.Slice(36,4).SequenceEqual("acsp"u8)||!profile.Slice(16,4).SequenceEqual("RGB "u8)||!profile.Slice(20,4).SequenceEqual("XYZ "u8))Reject();
        uint count=BinaryPrimitives.ReadUInt32BigEndian(profile[128..]);
        if(count>32||132+count*12>profile.Length)Reject();
        var tags=new Dictionary<string,(int Offset,int Length)>();
        for(int i=0;i<count;i++)
        {
            int entry=132+i*12;var name=Encoding.ASCII.GetString(profile.Slice(entry,4));uint offset=BinaryPrimitives.ReadUInt32BigEndian(profile[(entry+4)..]);uint length=BinaryPrimitives.ReadUInt32BigEndian(profile[(entry+8)..]);
            if(offset<132+count*12||offset>profile.Length||length>profile.Length-offset||!tags.TryAdd(name,((int)offset,(int)length)))Reject();
            // A LUT, alternate transform, or calibration tag could override the
            // matrix/TRC. Only known descriptive and matrix metadata is allowed.
            if(name is not("desc" or "cprt" or "wtpt" or "rXYZ" or "gXYZ" or "bXYZ" or "rTRC" or "gTRC" or "bTRC"))Reject();
        }
        foreach(var (name,expected) in new[]{("rXYZ",new[]{.4360747,.2225045,.0139322}),("gXYZ",new[]{.3850649,.7168786,.0971045}),("bXYZ",new[]{.1430804,.0606169,.7141733}),("wtpt",new[]{.9642,1.0,.8249})})
        {
            if(!tags.TryGetValue(name,out var tag)||tag.Length!=20){Reject();return;}
            var value=profile.Slice(tag.Offset,tag.Length);
            if(!value[..4].SequenceEqual("XYZ "u8))Reject();
            for(int j=0;j<3;j++)if(Math.Abs(Fixed(value[(8+j*4)..])-expected[j])>0.0002)Reject();
        }
        foreach(var name in new[]{"rTRC","gTRC","bTRC"})
        {
            if(!tags.TryGetValue(name,out var tag)||tag.Length<12){Reject();return;}
            var value=profile.Slice(tag.Offset,tag.Length);
            if(!value[..4].SequenceEqual("para"u8))Reject();
            int function=BinaryPrimitives.ReadUInt16BigEndian(value[8..]);
            if(function is not(3 or 4)||tag.Length!=(function==3?32:40))Reject();
            double[] expected=[2.4,1/1.055,.055/1.055,1/12.92,.04045,0,0];
            for(int j=0;j<(function==3?5:7);j++)if(Math.Abs(Fixed(value[(12+j*4)..])-expected[j])>0.00003)Reject();
        }
    }
    static double Fixed(ReadOnlySpan<byte> value)=>BinaryPrimitives.ReadInt32BigEndian(value)/65536.0;
}
