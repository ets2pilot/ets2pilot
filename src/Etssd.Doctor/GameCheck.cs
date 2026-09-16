using Etssd.Bridge.Sensing;

namespace Etssd.Doctor;

public sealed class GameCheck : IDoctorCheck
{
    public string Name => "game";

    public CheckResult Run() => ScreenCapture.IsWindowPresent()
        ? new(CheckStatus.Ok, $"已找到窗口 {ScreenCapture.WindowTitle}")
        : new(CheckStatus.Warning, $"未找到窗口 {ScreenCapture.WindowTitle}");
}
