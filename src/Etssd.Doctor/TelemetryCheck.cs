using System.IO.MemoryMappedFiles;
using Etssd.Bridge.Sensing;

namespace Etssd.Doctor;

/// <summary>映射由插件在游戏启动时创建，游戏未运行时得不出插件是否装好的结论。</summary>
public sealed class TelemetryCheck : IDoctorCheck
{
    private const string DownloadUrl = "https://github.com/RenCloud/scs-sdk-plugin/releases";
    private const string InstallHint = @"scs-telemetry.dll 需装到 <游戏>\bin\win_x64\plugins";

    public string Name => "telemetry";

    public CheckResult Run()
    {
        try
        {
            using var map = MemoryMappedFile.OpenExisting(ScsTelemetry.MapName, MemoryMappedFileRights.Read);
            return new(CheckStatus.Ok, $"已连接 {ScsTelemetry.MapName}");
        }
        catch (FileNotFoundException)
        {
            return ScreenCapture.IsWindowPresent()
                ? new(CheckStatus.Failed, $"无法打开 {ScsTelemetry.MapName}：{InstallHint}", DownloadUrl)
                : new(CheckStatus.Unknown, "游戏未运行", DownloadUrl);
        }
    }
}
