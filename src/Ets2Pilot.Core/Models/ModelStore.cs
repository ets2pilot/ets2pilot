using System.Text.Json;

namespace Ets2Pilot.Core.Models;

/// <summary>模型目录的解析进度。</summary>
public abstract record ModelState
{
    public sealed record Checking : ModelState;

    /// <param name="Total">字节，响应未给长度时为 null。</param>
    public sealed record Downloading(string File, long Received, long? Total) : ModelState;

    /// <param name="Warning">缓存可用但未能连上 hub 时的原因。</param>
    public sealed record Ready(string Dir, string Sha, DateTimeOffset? LastModified, string? Warning) : ModelState;

    public sealed record Error(string Message) : ModelState;
}

/// <summary>%LOCALAPPDATA%\ets2pilot\models 下的模型缓存，按仓库与 revision 分目录。</summary>
public static class ModelStore
{
    public static readonly IReadOnlyList<string> RequiredFiles =
        ["telemetry_encoder.onnx", "image_encoder.onnx", "decoder.onnx"];

    /// <summary>取得可用的模型目录，缓存与 revision 指向的 commit 不一致时从 hub 下载。</summary>
    public static async Task<ModelState> ResolveAsync(
        AppConfig config, IProgress<ModelState> progress, CancellationToken ct)
    {
        var endpoint = config.HfEndpoint.Length == 0 ? HfHub.DefaultEndpoint : config.HfEndpoint;
        var revision = config.ModelRevision.Length == 0 ? "main" : config.ModelRevision;
        var dir = Path.Combine(AppPaths.ModelsDir, config.ModelRepo.Replace("/", "--"), revision);
        // 内容为 sha，存在即表示 RequiredFiles 已全部落盘
        var complete = Path.Combine(dir, ".complete");

        progress.Report(new ModelState.Checking());

        using var http = HfHub.CreateClient();
        RevisionInfo info;
        try
        {
            info = await HfHub.GetRevisionAsync(http, endpoint, config.ModelRepo, revision, ct);
        }
        // endpoint 是用户输入，可能不是合法的绝对地址，也可能返回非 hub 格式的 JSON 或 HTML
        catch (Exception ex) when (ex is HfException or HttpRequestException or IOException or UriFormatException or InvalidOperationException or JsonException)
        {
            return File.Exists(complete)
                ? new ModelState.Ready(dir, File.ReadAllText(complete), null, ex.Message)
                : new ModelState.Error(ex.Message);
        }

        if (File.Exists(complete) && File.ReadAllText(complete) == info.Sha)
        {
            return new ModelState.Ready(dir, info.Sha, info.LastModified, null);
        }

        var missing = RequiredFiles.Except(info.Files).ToList();
        if (missing.Count > 0)
        {
            return new ModelState.Error($"仓库 {config.ModelRepo} 缺少 {string.Join("、", missing)}");
        }

        try
        {
            Directory.CreateDirectory(dir);
            File.Delete(complete);
            var downloading = new DownloadProgress(progress);
            foreach (var file in RequiredFiles)
            {
                var part = Path.Combine(dir, $"{file}.part");
                await HfHub.DownloadAsync(http, endpoint, config.ModelRepo, info.Sha, file, part, downloading, ct);
                File.Move(part, Path.Combine(dir, file), overwrite: true);
            }
        }
        catch (Exception ex) when (ex is HttpRequestException or IOException)
        {
            return new ModelState.Error(ex.Message);
        }

        File.WriteAllText(complete, info.Sha);
        return new ModelState.Ready(dir, info.Sha, info.LastModified, null);
    }

    private sealed class DownloadProgress(IProgress<ModelState> progress)
        : IProgress<(string File, long Received, long? Total)>
    {
        public void Report((string File, long Received, long? Total) value) =>
            progress.Report(new ModelState.Downloading(value.File, value.Received, value.Total));
    }
}
