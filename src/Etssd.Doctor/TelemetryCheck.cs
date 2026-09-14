using System.IO.MemoryMappedFiles;

namespace Etssd.Doctor;

/// <summary>映射只在游戏运行且插件已加载时存在，打不开时无法区分两种缺失。</summary>
public sealed class TelemetryCheck : IDoctorCheck
{
    private const string MapName = @"Local\SCSTelemetry";
    private const string DownloadUrl = "https://github.com/RenCloud/scs-sdk-plugin/releases";

    public string Name => "telemetry";

    public CheckResult Run()
    {
        try
        {
            using var map = MemoryMappedFile.OpenExisting(MapName, MemoryMappedFileRights.Read);
            return new(CheckStatus.Ok, $"已连接 {MapName}");
        }
        catch (FileNotFoundException)
        {
            return new(
                CheckStatus.Warning,
                $"未找到 {MapName}：游戏未运行，或未把 scs-telemetry.dll 装到 <游戏>\\bin\\win_x64\\plugins",
                DownloadUrl);
        }
    }
}
