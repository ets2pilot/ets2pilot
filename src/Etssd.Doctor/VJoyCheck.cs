using Etssd.Bridge.VJoy;

namespace Etssd.Doctor;

public sealed class VJoyCheck : IDoctorCheck
{
    // 与 dataset 的 actuator.py 一致
    private const uint DeviceId = 1;

    public string Name => "vJoy";

    public CheckResult Run()
    {
        if (VJoyInstall.DllPath is not { } dll || !File.Exists(dll))
        {
            return new(CheckStatus.Failed, "未安装 vJoy 驱动", VJoyInstall.DownloadUrl);
        }
        bool enabled;
        try
        {
            enabled = VJoyNative.vJoyEnabled();
        }
        catch (Exception ex) when (ex is DllNotFoundException or BadImageFormatException)
        {
            return new(CheckStatus.Failed, "无法加载 vJoyInterface.dll", VJoyInstall.DownloadUrl);
        }
        if (!enabled)
        {
            return new(CheckStatus.Failed, "vJoy 驱动未启用", VJoyInstall.DownloadUrl);
        }
        if (!VJoyNative.DriverMatch(out var dllVer, out var drvVer))
        {
            return new(CheckStatus.Warning, $"vJoyInterface.dll 版本 {dllVer:X4} 与驱动版本 {drvVer:X4} 不一致");
        }
        return VJoyNative.GetVJDStatus(DeviceId) switch
        {
            VjdStat.Own or VjdStat.Free => new(CheckStatus.Ok, $"设备 {DeviceId} 可用"),
            VjdStat.Busy => new(CheckStatus.Warning, $"设备 {DeviceId} 被其他进程占用"),
            VjdStat.Miss => new(CheckStatus.Failed, $"设备 {DeviceId} 未配置，用 vJoyConf 启用", VJoyInstall.DownloadUrl),
            _ => new(CheckStatus.Failed, $"设备 {DeviceId} 状态未知"),
        };
    }
}
