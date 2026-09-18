using System.Net;
using System.Net.Http.Json;
using System.Text.Json;

namespace Ets2Pilot.Core.Models;

/// <summary>hub 返回了无法继续的结果。</summary>
public sealed class HfException(string message) : Exception(message);

/// <param name="Files">revision 下全部文件的仓库内相对路径。</param>
public sealed record RevisionInfo(string Sha, DateTimeOffset? LastModified, IReadOnlyList<string> Files);

/// <summary>Hugging Face hub 的只读访问。</summary>
public static class HfHub
{
    public const string DefaultEndpoint = "https://huggingface.co";

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        RespectNullableAnnotations = true,
        RespectRequiredConstructorParameters = true,
    };

    public static HttpClient CreateClient()
    {
        // 单个 onnx 的下载会超过默认的 100 秒，只由 CancellationToken 终止
        var http = new HttpClient { Timeout = Timeout.InfiniteTimeSpan };
        http.DefaultRequestHeaders.UserAgent.ParseAdd("ets2pilot/1.0");
        return http;
    }

    /// <summary>取得 revision 指向的 commit 与文件清单。</summary>
    public static async Task<RevisionInfo> GetRevisionAsync(
        HttpClient http, string endpoint, string repo, string revision, CancellationToken ct)
    {
        using var response = await http.GetAsync($"{endpoint}/api/models/{repo}/revision/{revision}", ct);
        if (response.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.NotFound)
        {
            throw new HfException($"仓库 {repo} 不存在或无权限");
        }
        response.EnsureSuccessStatusCode();
        var info = await response.Content.ReadFromJsonAsync<ModelInfo>(JsonOptions, ct)
            ?? throw new HfException($"仓库 {repo} 的 revision {revision} 响应为空");
        return new RevisionInfo(info.Sha, info.LastModified, [.. info.Siblings.Select(s => s.Rfilename)]);
    }

    /// <summary>下载 sha 下的单个文件，覆盖 destination。</summary>
    public static async Task DownloadAsync(
        HttpClient http,
        string endpoint,
        string repo,
        string sha,
        string file,
        string destination,
        IProgress<(string File, long Received, long? Total)> progress,
        CancellationToken ct)
    {
        using var response = await http.GetAsync(
            $"{endpoint}/{repo}/resolve/{sha}/{file}", HttpCompletionOption.ResponseHeadersRead, ct);
        response.EnsureSuccessStatusCode();
        var total = response.Content.Headers.ContentLength;
        await using var source = await response.Content.ReadAsStreamAsync(ct);
        await using var sink = File.Create(destination);
        var buffer = new byte[1 << 20];
        long received = 0;
        int read;
        while ((read = await source.ReadAsync(buffer, ct)) > 0)
        {
            await sink.WriteAsync(buffer.AsMemory(0, read), ct);
            received += read;
            progress.Report((file, received, total));
        }
    }

    private sealed record ModelInfo(string Sha, DateTimeOffset? LastModified, IReadOnlyList<Sibling> Siblings);

    private sealed record Sibling(string Rfilename);
}
