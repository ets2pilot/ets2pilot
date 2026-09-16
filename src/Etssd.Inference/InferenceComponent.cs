using Etssd.Core;
using Microsoft.Extensions.Logging;

namespace Etssd.Inference;

/// <summary>作为 bridge 的回调客户端运行 ONNX 模型，轨迹交给 control 组件跟踪。</summary>
/// <param name="modelDir">含 telemetry_encoder.onnx、image_encoder.onnx、decoder.onnx 的目录。</param>
public sealed class InferenceComponent(string modelDir, ILoggerFactory loggers) : IComponent
{
    private readonly ILogger _log = loggers.CreateLogger<InferenceComponent>();

    public string Name => "infer";

    public async Task RunAsync(CancellationToken ct)
    {
        _log.LogInformation(AppEvents.UserVisible, "infer running, model: {Model}", modelDir);
        // loopback 请求不能走 HTTP_PROXY
        using var http = new HttpClient(new SocketsHttpHandler { UseProxy = false });
        await new ModelServer(modelDir, http, loggers.CreateLogger<ModelServer>()).RunAsync(ct);
        _log.LogInformation(AppEvents.UserVisible, "infer stopped");
    }
}
