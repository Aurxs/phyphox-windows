using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;
namespace Phyphox.Devices;

internal static class WindowsNative
{
    [DllImport("kernel32.dll", CharSet=CharSet.Unicode, SetLastError=true)] internal static extern SafeFileHandle CreateFile(string name,uint access,uint share,IntPtr security,uint creation,uint flags,IntPtr template);
    [DllImport("kernel32.dll", SetLastError=true)] internal static extern bool GetCommState(SafeFileHandle file,ref Dcb state);
    [DllImport("kernel32.dll", SetLastError=true)] internal static extern bool SetCommState(SafeFileHandle file,ref Dcb state);
    [DllImport("kernel32.dll", SetLastError=true)] internal static extern bool SetCommTimeouts(SafeFileHandle file,ref CommTimeouts timeouts);
    [DllImport("hid.dll",SetLastError=true)] [return:MarshalAs(UnmanagedType.U1)] internal static extern bool HidD_GetFeature(SafeFileHandle handle, [Out] byte[] bytes,int length);
    [DllImport("hid.dll",SetLastError=true)] [return:MarshalAs(UnmanagedType.U1)] internal static extern bool HidD_SetFeature(SafeFileHandle handle,byte[] bytes,int length);
    [DllImport("hid.dll",SetLastError=true)] [return:MarshalAs(UnmanagedType.U1)] internal static extern bool HidD_GetPreparsedData(SafeFileHandle handle,out IntPtr pointer);
    [DllImport("hid.dll")] [return:MarshalAs(UnmanagedType.U1)] internal static extern bool HidD_FreePreparsedData(IntPtr pointer);
    [DllImport("hid.dll")] internal static extern int HidP_GetCaps(IntPtr pointer,ref HidCaps caps);
    [StructLayout(LayoutKind.Sequential)] internal struct HidCaps {
        public ushort Usage,UsagePage,InputReportByteLength,OutputReportByteLength,FeatureReportByteLength;
        [MarshalAs(UnmanagedType.ByValArray,SizeConst=17)] public ushort[] Reserved;
        public ushort NumberLinkCollectionNodes,NumberInputButtonCaps,NumberInputValueCaps,NumberInputDataIndices,NumberOutputButtonCaps,NumberOutputValueCaps,NumberOutputDataIndices,NumberFeatureButtonCaps,NumberFeatureValueCaps,NumberFeatureDataIndices;
    }
    [DllImport("hid.dll")] internal static extern void HidD_GetHidGuid(out Guid guid);
    [DllImport("setupapi.dll",CharSet=CharSet.Unicode,SetLastError=true)] internal static extern IntPtr SetupDiGetClassDevs(ref Guid guid,string? enumerator,IntPtr parent,uint flags);
    [DllImport("setupapi.dll",SetLastError=true)] internal static extern bool SetupDiEnumDeviceInterfaces(IntPtr set,IntPtr info,ref Guid guid,uint index,ref InterfaceData data);
    [DllImport("setupapi.dll",CharSet=CharSet.Unicode,SetLastError=true)] internal static extern bool SetupDiGetDeviceInterfaceDetail(IntPtr set,ref InterfaceData data,IntPtr detail,uint size,out uint required,IntPtr info);
    [DllImport("setupapi.dll")] internal static extern bool SetupDiDestroyDeviceInfoList(IntPtr set);
    [StructLayout(LayoutKind.Sequential)] internal struct InterfaceData { public int Size; public Guid ClassGuid; public int Flags; public IntPtr Reserved; }
    [StructLayout(LayoutKind.Sequential)] internal struct Dcb { public uint Length,BaudRate,Flags; public ushort Reserved,XonLim,XoffLim; public byte ByteSize,Parity,StopBits; public sbyte XonChar,XoffChar,ErrorChar,EofChar,EvtChar; public ushort Reserved1; }
    [StructLayout(LayoutKind.Sequential)] internal struct CommTimeouts { public uint ReadInterval,ReadMultiplier,ReadConstant,WriteMultiplier,WriteConstant; }
    internal static void Check(bool ok) { if (!ok) throw new System.ComponentModel.Win32Exception(Marshal.GetLastWin32Error()); }
    internal static SafeFileHandle Open(string path) {
        var h=CreateFile(path,0xC0000000,3,IntPtr.Zero,3,0x40000000,IntPtr.Zero);
        if(h.IsInvalid) { h.Dispose(); throw new System.ComponentModel.Win32Exception(Marshal.GetLastWin32Error()); }
        return h;
    }
}
