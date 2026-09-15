using System.Collections.Concurrent;
using Etssd.Bridge.Frames;
using Etssd.Bridge.Http;
using Etssd.Bridge.Webhook;
using Microsoft.Extensions.Logging;

namespace Etssd.Bridge.Sensing;

/// <summary>image 回调线程。快照有到期的 image+telemetry 订阅时截取当前屏幕写入 slot，与快照合并后投递。</summary>
/// <remarks><see cref="ScreenCapture"/> 与本线程的 DPI 设置绑定，只在本线程上使用。</remarks>
public sealed class ImageTelemetryLoop(BlockingCollection<TelemetrySample> samples, SubscriberRegistry registry, ILogger log)
{
    public Task RunAsync(CancellationToken ct) => Task.Factory.StartNew(() => Run(ct), CancellationToken.None, TaskCreationOptions.LongRunning, TaskScheduler.Default);

    private void Run(CancellationToken ct)
    {
        using var ring = new FrameRing();
        var capture = new ScreenCapture(log);
        ulong seq = 0;
        try
        {
            foreach (var sample in samples.GetConsumingEnumerable(ct))
            {
                var due = registry.Snapshot
                    .Where(s => s.Request.Type == SubscriptionType.ImageTelemetry && s.IsDue(sample.SimulationTimeUs))
                    .ToList();
                if (due.Count == 0)
                {
                    continue;
                }
                // 新图像最多等最短周期的一半，等不到时本周期断流
                var maxWait = TimeSpan.FromSeconds(0.5 / due.Max(s => s.Request.Freq));
                try
                {
                    if (!capture.TryCapture(ring, seq + 1, maxWait))
                    {
                        continue;
                    }
                }
                catch (Exception ex)
                {
                    log.LogWarning(ex, "截图失败，重建 D3D11 资源");
                    capture.Dispose();
                    capture = new ScreenCapture(log);
                    continue;
                }
                seq++;
                foreach (var subscriber in due)
                {
                    subscriber.Post(new Notification("data", seq, sample.UnixNs, sample.Raw));
                }
            }
        }
        catch (OperationCanceledException)
        {
        }
        finally
        {
            capture.Dispose();
        }
    }
}
