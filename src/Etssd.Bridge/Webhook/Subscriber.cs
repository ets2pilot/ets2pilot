using System.Net.Http.Headers;
using System.Text.Json;
using System.Threading.Channels;
using Etssd.Bridge.Frames;
using Etssd.Bridge.Http;
using Microsoft.Extensions.Logging;

namespace Etssd.Bridge.Webhook;

/// <summary>一个 webhook 订阅，按注册顺序逐条投递通知。</summary>
/// <remarks>
/// 队列满时丢弃最旧的通知。队列长度等于 slot 数，排在更后面的 image 通知指向的 slot 已被覆盖。
/// 租期到期或投递失败即注销，订阅者需重新注册。
/// </remarks>
public sealed class Subscriber
{
    private static readonly TimeSpan DeliveryTimeout = TimeSpan.FromSeconds(2);

    private readonly Channel<Notification> _queue;
    private readonly CancellationTokenSource _stop = new();
    private readonly ulong _periodUs;
    private readonly TimeSpan _lease;
    private ulong? _nextDueUs;
    private long _dropped;
    private long _loggedDropped;
    private Task _sending = Task.CompletedTask;

    public Subscriber(WebhookRequest request)
    {
        Request = request;
        _periodUs = (ulong)Math.Max(1, Math.Round(1_000_000 / request.Freq));
        _lease = TimeSpan.FromSeconds(request.Lease);
        _stop.CancelAfter(_lease);
        _queue = Channel.CreateBounded<Notification>(
            new BoundedChannelOptions(FrameRing.SlotCount)
            {
                FullMode = BoundedChannelFullMode.DropOldest,
                SingleReader = true,
                SingleWriter = true,
            },
            _ => Interlocked.Increment(ref _dropped));
    }

    public WebhookRequest Request { get; }

    public string Name => Request.Name;

    /// <summary>订阅建立以来丢弃的通知条数。</summary>
    public long Dropped => Volatile.Read(ref _dropped);

    /// <summary>续期。租期已过时返回 false，此时投递已停止。</summary>
    public bool TryRenew()
    {
        _stop.CancelAfter(_lease);
        return !_stop.IsCancellationRequested;
    }

    /// <summary>判断游戏时刻 simUs 是否到期并推进调度。只由负责该订阅类型的传感线程调用。</summary>
    /// <remarks>首次调用或时钟回退超过一个周期时以 simUs 重新锚定，落后超过一个周期时跳过积压的周期。</remarks>
    public bool IsDue(ulong simUs)
    {
        if (_nextDueUs is not { } next || simUs + _periodUs < next)
        {
            next = simUs;
        }
        if (simUs < next)
        {
            _nextDueUs = next;
            return false;
        }
        next += _periodUs;
        _nextDueUs = next > simUs ? next : simUs + _periodUs;
        return true;
    }

    public void Post(Notification notification) => _queue.Writer.TryWrite(notification);

    /// <param name="onStopped">租期到期、投递失败或 <see cref="StopAsync"/> 后调用一次。</param>
    public void Start(HttpClient http, ILogger log, Action<Subscriber> onStopped) =>
        _sending = Task.Run(() => SendLoopAsync(http, log, onStopped));

    /// <summary>停止投递并丢弃队列中剩余的通知。</summary>
    public async Task StopAsync()
    {
        _queue.Writer.TryComplete();
        await _stop.CancelAsync();
        await _sending;
    }

    public async Task<bool> TrySendAsync(HttpClient http, Notification notification, TimeSpan timeout, CancellationToken ct)
    {
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(timeout);
        // 带 Content-Length 发送，Python http.server 等简单接收端不支持 chunked body
        using var content = new ByteArrayContent(JsonSerializer.SerializeToUtf8Bytes(notification, Json.Options));
        content.Headers.ContentType = new MediaTypeHeaderValue("application/json");
        try
        {
            using var response = await http.PostAsync(Request.Url, content, cts.Token);
            return response.IsSuccessStatusCode;
        }
        catch (Exception ex) when (ex is HttpRequestException or OperationCanceledException)
        {
            return false;
        }
    }

    private async Task SendLoopAsync(HttpClient http, ILogger log, Action<Subscriber> onStopped)
    {
        try
        {
            await foreach (var notification in _queue.Reader.ReadAllAsync(_stop.Token))
            {
                if (!await TrySendAsync(http, notification, DeliveryTimeout, _stop.Token))
                {
                    if (!_stop.IsCancellationRequested)
                    {
                        log.LogWarning("webhook {Name} 投递到 {Url} 失败", Name, Request.Url);
                    }
                    return;
                }
                if (Interlocked.Read(ref _dropped) - _loggedDropped is var dropped and > 0)
                {
                    _loggedDropped += dropped;
                    log.LogWarning("webhook {Name} 处理过慢，丢弃 {Count} 条通知", Name, dropped);
                }
            }
        }
        catch (OperationCanceledException)
        {
        }
        finally
        {
            onStopped(this);
        }
    }
}
