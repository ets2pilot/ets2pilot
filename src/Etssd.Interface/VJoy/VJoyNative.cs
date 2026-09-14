using System.Runtime.InteropServices;

namespace Etssd.Interface.VJoy;

/// <summary>vJoy SDK 的 VjdStat。</summary>
public enum VjdStat
{
    Own = 0,
    Free = 1,
    Busy = 2,
    Miss = 3,
    Unkn = 4,
}

/// <summary>vJoyInterface.dll 的 P/Invoke，dll 路径来自 <see cref="VJoyInstall"/>。</summary>
public static class VJoyNative
{
    private const string Lib = "vJoyInterface";

    // 每个程序集只能注册一次 resolver，本类静态构造是唯一注册点
    static VJoyNative()
    {
        NativeLibrary.SetDllImportResolver(typeof(VJoyNative).Assembly, (name, _, _) =>
            name == Lib && VJoyInstall.DllPath is { } path && File.Exists(path)
                ? NativeLibrary.Load(path)
                : IntPtr.Zero);
    }

    [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)]
    public static extern bool vJoyEnabled();

    [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)]
    public static extern VjdStat GetVJDStatus(uint rID);

    [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)]
    public static extern bool DriverMatch(out ushort dllVer, out ushort drvVer);
}
