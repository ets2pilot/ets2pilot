using System.Text.Json;
using System.Text.Json.Serialization;

namespace Etssd.Interface.Http;

public enum SubscriptionType
{
    Telemetry,

    [JsonStringEnumMemberName("image+telemetry")]
    ImageTelemetry,
}

/// <param name="Freq">Hz，按游戏 simulation_time 计时。</param>
/// <param name="Url">接收 <see cref="Notification"/> 的 http 地址。</param>
public sealed record WebhookRequest(string Name, SubscriptionType Type, double Freq, string Url);

/// <summary>帧共享内存的布局，见 <see cref="Frames.FrameRing"/>。</summary>
public sealed record WebhookResponse(int ProtocolVersion, string MappingName, int SlotCount, int Width, int Height);

public sealed record ControlRequest(double Throttle, double Steer);

/// <summary>POST 到订阅者 url 的 body。</summary>
/// <param name="Event">data 或 end，end 表示 interface 正在关闭。</param>
/// <param name="Seq">帧序号，仅 image+telemetry 订阅的 data 事件携带。</param>
/// <param name="TimestampNs">Unix epoch 纳秒，telemetry 快照时刻。</param>
/// <param name="Telemetry">Local\SCSTelemetry 的完整原始字节，JSON 中为 base64。</param>
public sealed record Notification(string Event, ulong? Seq = null, long? TimestampNs = null, byte[]? Telemetry = null)
{
    public static Notification End { get; } = new("end");
}

public static class Json
{
    public static JsonSerializerOptions Options { get; } = Configure(new JsonSerializerOptions());

    public static JsonSerializerOptions Configure(JsonSerializerOptions options)
    {
        options.PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower;
        options.DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull;
        options.RespectNullableAnnotations = true;
        options.RespectRequiredConstructorParameters = true;
        options.Converters.Add(new JsonStringEnumConverter(JsonNamingPolicy.SnakeCaseLower, allowIntegerValues: false));
        return options;
    }
}
