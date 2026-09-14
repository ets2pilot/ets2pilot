namespace Etssd.Doctor;

public enum CheckStatus
{
    Ok,
    Warning,
    Failed,
}

/// <param name="Link">缺失依赖的下载页，无则为 null。</param>
public sealed record CheckResult(CheckStatus Status, string Message, string? Link = null);

/// <summary>一次性依赖检查，对应 CLI 的 doctor 子命令与 GUI 的依赖检查页。</summary>
public interface IDoctorCheck
{
    string Name { get; }

    CheckResult Run();
}

public static class Checks
{
    public static IReadOnlyList<IDoctorCheck> All { get; } =
    [
        new VJoyCheck(),
        new TelemetryCheck(),
        new GpuCheck(),
    ];
}
