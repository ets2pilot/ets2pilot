using Etssd.Core;
using Microsoft.Extensions.Logging;

namespace Etssd.Inference;

/// <summary>控制算法 server，跟踪任意模型 server POST 到 /trajectory 的轨迹。</summary>
public sealed class ControlComponent(ILoggerFactory loggers) : IComponent
{
    private readonly ILogger _log = loggers.CreateLogger<ControlComponent>();

    public string Name => "control";

    public async Task RunAsync(CancellationToken ct)
    {
        // loopback 请求不能走 HTTP_PROXY
        using var http = new HttpClient(new SocketsHttpHandler { UseProxy = false });
        _log.LogInformation(AppEvents.UserVisible, "control 启动 {Url}", ControlServer.Url);
        await new ControlServer(http).RunAsync(ct);
        _log.LogInformation(AppEvents.UserVisible, "control 已停止");
    }
}
