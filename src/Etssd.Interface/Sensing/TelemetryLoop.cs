using System.Collections.Concurrent;
using Etssd.Interface.Http;
using Etssd.Interface.Webhook;

namespace Etssd.Interface.Sensing;

/// <summary>telemetry 回调线程，把每份快照投递给到期的 telemetry 订阅。</summary>
public sealed class TelemetryLoop(BlockingCollection<TelemetrySample> samples, SubscriberRegistry registry)
{
    public Task RunAsync(CancellationToken ct) => DedicatedThread.RunAsync("etssd-telemetry", () => Run(ct));

    private void Run(CancellationToken ct)
    {
        try
        {
            foreach (var sample in samples.GetConsumingEnumerable(ct))
            {
                foreach (var subscriber in registry.Snapshot)
                {
                    if (subscriber.Request.Type == SubscriptionType.Telemetry && subscriber.IsDue(sample.SimulationTimeUs))
                    {
                        subscriber.Post(new Notification("data", null, sample.UnixNs, sample.Raw));
                    }
                }
            }
        }
        catch (OperationCanceledException)
        {
        }
    }
}
