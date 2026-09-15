using System.Text.Json;
using Microsoft.Extensions.Logging;
using Tomlyn;

namespace Etssd.Core;

/// <summary>config.toml 的内容。只有 GUI 设置页写入，其余进程只读。</summary>
public sealed record AppConfig
{
    private static readonly TomlSerializerOptions TomlOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
    };

    /// <summary>hub 上的模型仓库，形如 owner/name。</summary>
    public string ModelRepo { get; set; } = "ets2-self-driving/ets2-self-driving-model";

    /// <summary>分支、标签或 commit sha，空表示 main。</summary>
    public string ModelRevision { get; set; } = "";

    /// <summary>hub 地址，空表示 https://huggingface.co。</summary>
    public string HfEndpoint { get; set; } = "";

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
