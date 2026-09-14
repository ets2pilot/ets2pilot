using Etssd.Core;
using Microsoft.Extensions.Logging;

namespace Etssd.Interface;

/// <summary>截图、遥测、共享内存、HTTP 服务、vJoy。占位实现，只等待取消。</summary>
public sealed class InterfaceComponent(ILoggerFactory loggers) : IComponent
{
    private readonly ILogger _log = loggers.CreateLogger<InterfaceComponent>();

    public string Name => "interface";

    public async Task RunAsync(CancellationToken ct)
    {
        _log.LogInformation("interface running (placeholder)");
        try
        {
            await Task.Delay(Timeout.Infinite, ct);
        }
        catch (OperationCanceledException)
        {
        }
        _log.LogInformation("interface stopped");
    }
}
