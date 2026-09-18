using Ets2Pilot.Core;
using Ets2Pilot.Bridge.Http;
using Ets2Pilot.Bridge.Sensing;
using Ets2Pilot.Bridge.VJoy;
using Ets2Pilot.Bridge.Webhook;
using Microsoft.Extensions.Logging;

namespace Ets2Pilot.Bridge;

/// <param name="Freq">Hz</param>
/// <param name="Dropped">订阅建立以来丢弃的通知条数。</param>
public sealed record SubscriptionInfo(string Name, SubscriptionType Type, double Freq, long Dropped);

/// <summary>截图、相机回正、遥测、帧共享内存、webhook 通知与 vJoy 控制。</summary>
public sealed class BridgeComponent(ILoggerFactory loggers) : IComponent
{
    private static readonly TimeSpan ServerStopTimeout = TimeSpan.FromMilliseconds(500);

    private readonly ILogger _log = loggers.CreateLogger<BridgeComponent>();

    private volatile SubscriberRegistry? _registry;

    public string Name => "bridge";

    /// <summary>当前的 webhook 订阅，未运行时为空。</summary>
    public IReadOnlyList<SubscriptionInfo> Subscriptions =>
        _registry is { } registry
            ? [.. registry.Snapshot.Select(s => new SubscriptionInfo(s.Name, s.Request.Type, s.Request.Freq, s.Dropped))]
            : [];

    /// <summary>ct 取消后先向全部订阅者发送 end 通知再关闭 HTTP 服务，总耗时须小于 CLI 的 2s 终止等待。</summary>
    public async Task RunAsync(CancellationToken ct)
    {
        // loopback 通知不能走 HTTP_PROXY
        using var http = new HttpClient(new SocketsHttpHandler { UseProxy = false });
        var registry = new SubscriberRegistry(http, loggers.CreateLogger<SubscriberRegistry>());
        _registry = registry;
        using var vjoy = VJoyController.TryAcquire(_log);
        await using var app = BridgeServer.Build(registry, vjoy, loggers);
        await app.StartAsync(CancellationToken.None);
        _log.LogInformation(AppEvents.UserVisible, "bridge 监听 {Url}", BridgeServer.Url);
        using var stop = CancellationTokenSource.CreateLinkedTokenSource(ct);
        using var capture = new TelemetryCapture(loggers.CreateLogger<TelemetryCapture>());
        var telemetryLoop = new TelemetryLoop(capture.Subscribe(), registry);
        var imageLoop = new ImageTelemetryLoop(capture.Subscribe(), registry, loggers.CreateLogger<ImageTelemetryLoop>());
        var cameraLoop = new CameraResetLoop(registry, loggers.CreateLogger<CameraResetLoop>());
        Task[] loops =
        [
            capture.RunAsync(stop.Token),
            telemetryLoop.RunAsync(stop.Token),
            imageLoop.RunAsync(stop.Token),
            cameraLoop.RunAsync(stop.Token),
        ];
        try
        {
            // 任一线程异常退出时停下其余线程，异常由 WhenAll 抛出
            await Task.WhenAny(loops);
            await stop.CancelAsync();
            await Task.WhenAll(loops);
        }
        finally
        {
            _registry = null;
            await registry.EndAllAsync();
            using var stopTimeout = new CancellationTokenSource(ServerStopTimeout);
            await app.StopAsync(stopTimeout.Token);
            _log.LogInformation(AppEvents.UserVisible, "bridge 已停止");
        }
    }
}
