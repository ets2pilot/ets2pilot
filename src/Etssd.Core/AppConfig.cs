using System.Text.Json;
using Microsoft.Extensions.Logging;
using Tomlyn;

namespace Etssd.Core;

/// <summary>config.toml 的内容。GUI 写入，CLI 只读，CLI 参数覆盖不落盘。</summary>
public sealed record AppConfig
{
    public const string DefaultModel =
        "https://github.com/ets2-self-driving/ets2-self-driving-model/releases/latest";

    private static readonly TomlSerializerOptions TomlOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
    };

    public string Model { get; set; } = DefaultModel;

    /// <summary>读取配置文件，文件缺失或损坏时返回默认值。</summary>
    public static AppConfig Load(ILogger logger)
    {
        if (!File.Exists(AppPaths.ConfigFile))
        {
            return new AppConfig();
        }
        try
        {
            var text = File.ReadAllText(AppPaths.ConfigFile);
            return TomlSerializer.Deserialize<AppConfig>(text, TomlOptions) ?? new AppConfig();
        }
        catch (Exception ex) when (ex is TomlException or IOException)
        {
            logger.LogWarning(ex, "{Path} 无法读取，使用默认配置", AppPaths.ConfigFile);
            return new AppConfig();
        }
    }

    public void Save()
    {
        Directory.CreateDirectory(AppPaths.Root);
        File.WriteAllText(AppPaths.ConfigFile, TomlSerializer.Serialize(this, TomlOptions));
    }
}
