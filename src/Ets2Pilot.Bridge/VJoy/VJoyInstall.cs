using Microsoft.Win32;

namespace Ets2Pilot.Bridge.VJoy;

/// <summary>本机 vJoy 安装位置。不随程序分发 vJoyInterface.dll，避免与已装驱动版本不一致。</summary>
public static class VJoyInstall
{
    public const string DownloadUrl = "https://github.com/BrunnerInnovation/vJoy/releases";

    // vJoy 安装器（Inno Setup）的卸载键，pyvjoystick 也由此定位 dll
    private const string UninstallKey =
        @"SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall\{8E31F76F-74C3-47F1-9550-E041EEDC5FBB}_is1";

    /// <summary>存放 vJoyInterface.dll 的目录，未安装时为 null。</summary>
    public static string? DllDirectory { get; } = Locate();

    public static string? DllPath =>
        DllDirectory is null ? null : Path.Combine(DllDirectory, "vJoyInterface.dll");

    private static string? Locate()
    {
        foreach (var view in new[] { RegistryView.Registry64, RegistryView.Registry32 })
        {
            using var hklm = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, view);
            using var key = hklm.OpenSubKey(UninstallKey);
            if (key is null)
            {
                continue;
            }
            if (key.GetValue("DllX64Location") is string dllDir)
            {
                return dllDir;
            }
            if (key.GetValue("InstallLocation") is string root)
            {
                return Path.Combine(root, "x64");
            }
        }
        return null;
    }
}
