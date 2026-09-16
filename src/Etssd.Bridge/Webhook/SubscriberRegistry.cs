using Etssd.Bridge.Http;
using Etssd.Core;
using Microsoft.Extensions.Logging;

namespace Etssd.Bridge.Webhook;

/// <summary>按 name 索引的订阅集合。HTTP 线程写入，传感线程读取 <see cref="Snapshot"/>。</summary>
public sealed class SubscriberRegistry(HttpClient http, ILogger log)
{
    private static readonly TimeSpan EndTimeout = TimeSpan.FromSeconds(1);

    private readonly Lock _lock = new();
    private Subscriber[] _subscribers = [];
    private bool _closed;

    public Subscriber[] Snapshot => Volatile.Read(ref _subscribers);

    /// <summary>注册订阅。参数相同的同名订阅续期，参数不同的同名订阅被替换。关闭后返回 false。</summary>
    public bool Add(WebhookRequest request)
    {
        Subscriber? replaced;
        lock (_lock)
        {
            if (_closed)
            {
                return false;
            }
            replaced = _subscribers.FirstOrDefault(s => s.Name == request.Name);
            if (replaced?.Request == request && replaced.TryRenew())
            {
                return true;
            }
            var subscriber = new Subscriber(request);
            Volatile.Write(ref _subscribers, [.. _subscribers.Where(s => s != replaced), subscriber]);
            subscriber.Start(http, log, stopped => Remove(stopped));
        }
        _ = replaced?.StopAsync();
        log.LogInformation(AppEvents.UserVisible, "webhook {Name} 已注册: {Type} {Freq}Hz 租期 {Lease}s -> {Url}",
            request.Name, request.Type, request.Freq, request.Lease, request.Url);
        return true;
    }

    /// <summary>停止全部投递，并行向每个订阅者发送 end 通知，之后拒绝新的注册。</summary>
    public async Task EndAllAsync()
    {
        Subscriber[] all;
        lock (_lock)
        {
            _closed = true;
            all = _subscribers;
            Volatile.Write(ref _subscribers, []);
        }
        await Task.WhenAll(all.Select(async s =>
        {
            await s.StopAsync();
            if (!await s.TrySendAsync(http, Notification.End, EndTimeout, CancellationToken.None))
            {
                log.LogWarning("webhook {Name} 未收到 end 通知", s.Name);
            }
        }));
        if (all.Length > 0)
        {
            log.LogInformation(AppEvents.UserVisible, "已向 {Count} 个 webhook 发送 end 通知", all.Length);
        }
    }

    private void Remove(Subscriber subscriber)
    {
        lock (_lock)
        {
            if (!_subscribers.Contains(subscriber))
            {
                return;
            }
            Volatile.Write(ref _subscribers, [.. _subscribers.Where(s => s != subscriber)]);
        }
        log.LogInformation(AppEvents.UserVisible, "webhook {Name} 已注销", subscriber.Name);
    }
}
