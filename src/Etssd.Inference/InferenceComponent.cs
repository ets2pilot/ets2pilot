using Etssd.Core;
using Microsoft.Extensions.Logging;

namespace Etssd.Inference;

/// <summary>加载 ONNX，作为 interface 的回调客户端推理。占位实现，只等待取消。</summary>
public sealed class InferenceComponent(AppConfig config, ILoggerFactory loggers) : IComponent
{
    private readonly ILogger _log = loggers.CreateLogger<InferenceComponent>();

    public string Name => "infer";

    public async Task RunAsync(CancellationToken ct)
    {
        _log.LogInformation("infer running (placeholder), model: {Model}", config.Model);
        try
        {
            await Task.Delay(Timeout.Infinite, ct);
        }
        catch (OperationCanceledException)
        {
        }
        _log.LogInformation("infer stopped");
    }
}
