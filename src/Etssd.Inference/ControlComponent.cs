using Etssd.Core;
using Microsoft.Extensions.Logging;

namespace Etssd.Inference;

/// <summary>控制算法 server，跟踪任意模型 server POST 到 /trajectory 的轨迹，由 <see cref="AutopilotSwitch"/> 决定是否接管车辆，<see cref="AutopilotKeys"/> 提供激活事件。</summary>
public sealed class ControlComponent(ILoggerFactory loggers) : IComponent
{
    private readonly ILogger _log = loggers.CreateLogger<ControlComponent>();

    private volatile AutopilotSwitch? _autopilot;

    /// <summary><see cref="IsAutopilotEngaged"/> 变化后在后台线程上触发。</summary>
    public event Action? AutopilotChanged;

    public string Name => "control";

    /// <summary>未运行时为 false。</summary>
    public bool IsAutopilotEngaged => _autopilot?.IsEngaged ?? false;

    public async Task RunAsync(CancellationToken ct)
    {
        // loopback 请求不能走 HTTP_PROXY
        using var http = new HttpClient(new SocketsHttpHandler { UseProxy = false });
        var autopilot = new AutopilotSwitch(loggers.CreateLogger<AutopilotSwitch>());
        autopilot.Changed += () => AutopilotChanged?.Invoke();
        _autopilot = autopilot;
        using var stop = CancellationTokenSource.CreateLinkedTokenSource(ct);
        var keys = new AutopilotKeys(autopilot).RunAsync(stop.Token);
        _log.LogInformation(AppEvents.UserVisible, "control 启动 {Url}，Q 手动锁定 · W 开启自动驾驶 · 长按 S 关闭", ControlServer.Url);
        try
        {
            await new ControlServer(http, autopilot).RunAsync(ct);
        }
        finally
        {
            await stop.CancelAsync();
            await keys;
            _autopilot = null;
            AutopilotChanged?.Invoke();
        }
        _log.LogInformation(AppEvents.UserVisible, "control 已停止");
    }
}
