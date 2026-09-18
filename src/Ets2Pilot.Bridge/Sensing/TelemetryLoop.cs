using System.Collections.Concurrent;
using Ets2Pilot.Bridge.Http;
using Ets2Pilot.Bridge.Webhook;

namespace Ets2Pilot.Bridge.Sensing;

/// <summary>telemetry 回调线程，把每份快照投递给到期的 telemetry 订阅。</summary>
public sealed class TelemetryLoop(BlockingCollection<TelemetrySample> samples, SubscriberRegistry registry)
{
    public Task RunAsync(CancellationToken ct) => Task.Factory.StartNew(() => Run(ct), CancellationToken.None, TaskCreationOptions.LongRunning, TaskScheduler.Default);

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
