using Etssd.Core;
using Microsoft.Extensions.Logging;

namespace Etssd.Inference;

/// <summary>作为 bridge 的回调客户端驾驶：模型算法 server 出轨迹，控制算法 server 跟踪轨迹。</summary>
public sealed class InferenceComponent(AppConfig config, ILoggerFactory loggers) : IComponent
{
    private readonly ILogger _log = loggers.CreateLogger<InferenceComponent>();

    public string Name => "infer";

    public async Task RunAsync(CancellationToken ct)
    {
        _log.LogInformation("infer running, model: {Model}", config.Model);
        // loopback 请求不能走 HTTP_PROXY
        using var http = new HttpClient(new SocketsHttpHandler { UseProxy = false });
        await Task.WhenAll(new ControlServer(http).RunAsync(ct), new ModelServer(config.Model, http, loggers.CreateLogger<ModelServer>()).RunAsync(ct));
        _log.LogInformation("infer stopped");
    }
}
