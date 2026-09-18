namespace Ets2Pilot.Core;

/// <summary>用户数据目录 %LOCALAPPDATA%\ets2pilot 下的固定路径。</summary>
public static class AppPaths
{
    public static string Root { get; } = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "ets2pilot");

    public static string ConfigFile { get; } = Path.Combine(Root, "config.toml");

    public static string LogDir { get; } = Path.Combine(Root, "logs");

    public static string ModelsDir { get; } = Path.Combine(Root, "models");

    public static string EngineCacheDir { get; } = Path.Combine(Root, "engine-cache");

    /// <summary>日志文件按进程角色分开，同时运行的 CLI 子命令与 GUI 不共用文件。</summary>
    public static string LogFile(string role) => Path.Combine(LogDir, $"ets2pilot-{role}.log");
}
