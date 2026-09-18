using System.Security.Cryptography;
using Ets2Pilot.Core;
using Microsoft.Extensions.Logging;

namespace Ets2Pilot.Inference;

/// <summary>TensorRT-RTX 的 EP context model 缓存，按源模型内容的 hash 命名。</summary>
internal static class EngineCache
{
    static EngineCache() => Directory.CreateDirectory(Dir);

    public static string Dir => AppPaths.EngineCacheDir;

    public static string PathFor(string modelPath)
    {
        using var model = File.OpenRead(modelPath);
        var hash = Convert.ToHexString(SHA256.HashData(model))[..16];
        return Path.Combine(Dir, $"{Path.GetFileNameWithoutExtension(modelPath)}-{hash}_ctx.onnx");
    }

    /// <summary>删除失效的缓存文件。</summary>
    /// <returns>文件已不存在时为 true。ORT 抛错后 native 侧可能仍持有句柄，删不掉时为 false。</returns>
    public static bool Discard(string path, ILogger log)
    {
        if (!File.Exists(path))
        {
            return true;
        }
        try
        {
            File.Delete(path);
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            log.LogWarning("删除 {Cached} 失败: {Message}", Path.GetFileName(path), ex.Message);
            return false;
        }
    }
}
