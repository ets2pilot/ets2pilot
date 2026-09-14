using System.Collections.Concurrent;
using Microsoft.Extensions.Logging;

namespace Etssd.Interface.Sensing;

/// <param name="SimulationTimeUs">SCS simulation_time，暂停时继续走。</param>
/// <param name="UnixNs">锁定时刻，Unix epoch 纳秒。</param>
/// <param name="Raw">Local\SCSTelemetry 的完整原始字节，各回调线程共享只读。</param>
public sealed record TelemetrySample(ulong SimulationTimeUs, long UnixNs, byte[] Raw);

/// <summary>唯一读取 SCS 的线程。游戏时钟每次更新时锁定一份快照，按顺序交给每个回调线程。</summary>
public sealed class TelemetryCapture(ILogger log) : IDisposable
{
    private static readonly TimeSpan WaitingInterval = TimeSpan.FromMilliseconds(100);

    private readonly List<BlockingCollection<TelemetrySample>> _outputs = [];

    /// <summary>为一个回调线程建立快照队列，须在 <see cref="RunAsync"/> 之前调用。采集结束时队列被标记完成。</summary>
    public BlockingCollection<TelemetrySample> Subscribe()
    {
        var output = new BlockingCollection<TelemetrySample>();
        _outputs.Add(output);
        return output;
    }

    public Task RunAsync(CancellationToken ct) => DedicatedThread.RunAsync("etssd-telemetry-capture", () => Run(ct));

    public void Dispose()
    {
        foreach (var output in _outputs)
        {
            output.Dispose();
        }
    }

    private void Run(CancellationToken ct)
    {
        // 游戏每帧更新一次 telemetry，默认 15.6ms 的定时器精度会漏掉更新
        Win32.timeBeginPeriod(1);
        try
        {
            using var telemetry = new ScsTelemetry();
            ulong? lastSimUs = null;
            var waiting = false;
            while (!ct.IsCancellationRequested)
            {
                if (!telemetry.TryOpen() || !telemetry.SdkActive)
                {
                    if (!waiting)
                    {
                        log.LogInformation("等待 telemetry: {Map}", ScsTelemetry.MapName);
                        waiting = true;
                    }
                    lastSimUs = null;
                    ct.WaitHandle.WaitOne(WaitingInterval);
                    continue;
                }
                if (waiting)
                {
                    log.LogInformation("telemetry 已连接");
                    waiting = false;
                }
                if (telemetry.SimulationTimeUs == lastSimUs)
                {
                    Thread.Sleep(1);
                    continue;
                }
                var raw = telemetry.Snapshot(out var unixNs);
                // 游戏可能在轮询与拷贝之间再次更新，时刻以快照内容为准
                var simUs = ScsTelemetry.SimulationTimeUsOf(raw);
                lastSimUs = simUs;
                var sample = new TelemetrySample(simUs, unixNs, raw);
                foreach (var output in _outputs)
                {
                    output.Add(sample);
                }
            }
        }
        finally
        {
            foreach (var output in _outputs)
            {
                output.CompleteAdding();
            }
            Win32.timeEndPeriod(1);
        }
    }
}
