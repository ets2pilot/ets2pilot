using System.IO.MemoryMappedFiles;
using System.Net.Http.Json;
using System.Threading.Channels;
using Etssd.Bridge.Http;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.ML.OnnxRuntime;
using SessionOptions = Microsoft.ML.OnnxRuntime.SessionOptions;

namespace Etssd.Inference;

/// <summary>模型算法 server。注册 image+telemetry webhook 并持续续约，推理出的轨迹 POST 到 control server。</summary>
/// <param name="modelDir">含 telemetry_encoder.onnx、image_encoder.onnx、decoder.onnx 的目录。</param>
public sealed class ModelServer(string modelDir, HttpClient http, ILogger log)
{
    public const string Url = "http://127.0.0.1:5321";

    private static readonly TimeSpan Lease = TimeSpan.FromSeconds(5);

    public async Task RunAsync(CancellationToken ct)
    {
        using var options = new SessionOptions
        {
            // DirectML EP 要求关闭内存图样并顺序执行
            EnableMemoryPattern = false,
            ExecutionMode = ExecutionMode.ORT_SEQUENTIAL,
        };
        options.AppendExecutionProvider_DML(0);
        using var telemetryEncoder = new TelemetryEncoder(new InferenceSession(Path.Combine(modelDir, "telemetry_encoder.onnx"), options));
        using var imageEncoder = new ImageEncoder(new InferenceSession(Path.Combine(modelDir, "image_encoder.onnx"), options));
        using var decoder = new Decoder(new InferenceSession(Path.Combine(modelDir, "decoder.onnx"), options));

        var builder = WebApplication.CreateSlimBuilder();
        builder.WebHost.UseUrls(Url);
        builder.Logging.ClearProviders();
        builder.Services.ConfigureHttpJsonOptions(o => Json.Configure(o.SerializerOptions));
        await using var app = builder.Build();

        // 只保留最新一条通知。seq 在全部 image 订阅间统一编号，丢帧按到达序号判断
        var latest = Channel.CreateBounded<(long Index, ulong Seq, byte[] Telemetry)>(
            new BoundedChannelOptions(1) { FullMode = BoundedChannelFullMode.DropOldest });
        long received = 0;
        var window = new Queue<(byte[] Telemetry, IDisposableReadOnlyCollection<OrtValue> Image)>();

        // bridge 收到响应才投递下一条，推理不能阻塞响应
        app.MapPost("/notify", (Notification notification) =>
        {
            if (notification is { Event: "data", Seq: { } seq, Telemetry: { } telemetry })
            {
                latest.Writer.TryWrite((Interlocked.Increment(ref received), seq, telemetry));
            }
            return Results.NoContent();
        });

        await app.StartAsync(ct);
        var webhook = new WebhookRequest("infer", SubscriptionType.ImageTelemetry, decoder.Freq, $"{Url}/notify", Lease.TotalSeconds);
        using var ring = new FrameRing(await RegisterAsync());
        using var renewing = new Timer(state => _ = RegisterAsync(), null, Lease / 2, Lease / 2);
        await using var stopping = ct.Register(() => latest.Writer.TryComplete());
        long consumed = 0;
        await foreach (var (index, seq, telemetry) in latest.Reader.ReadAllAsync())
        {
            var crops = ring.Crop(seq, imageEncoder.Regions);
            if (index != consumed + 1 || crops is null)
            {
                log.LogWarning("推理跟不上 {Freq}Hz，丢弃 {Count} 帧历史", decoder.Freq, window.Count);
                DiscardWindow();
            }
            consumed = index;
            if (crops is null)
            {
                continue;
            }
            window.Enqueue((telemetry, imageEncoder.Forward(crops)));
            if (window.Count > decoder.Frames)
            {
                window.Dequeue().Image.Dispose();
            }
            if (window.Count < decoder.Frames)
            {
                continue;
            }
            using var encoded = telemetryEncoder.Forward([.. window.Select(w => w.Telemetry)]);
            var (speed, yawRate) = decoder.Forward(encoded, [.. window.Select(w => w.Image)]);
            using var response = await http.PostAsJsonAsync($"{ControlServer.Url}/trajectory",
                new TrajectoryRequest(decoder.Freq, telemetry, speed, yawRate), Json.Options);
        }
        await app.StopAsync();
        DiscardWindow();

        async Task<WebhookResponse> RegisterAsync()
        {
            using var response = await http.PostAsJsonAsync($"{BridgeServer.Url}/webhook", webhook, Json.Options, ct);
            response.EnsureSuccessStatusCode();
            return (await response.Content.ReadFromJsonAsync<WebhookResponse>(Json.Options, ct))!;
        }

        void DiscardWindow()
        {
            foreach (var (_, image) in window)
            {
                image.Dispose();
            }
            window.Clear();
        }
    }

    /// <summary>帧共享内存的读端，布局见 <see cref="Bridge.Frames.FrameRing"/>。</summary>
    private sealed class FrameRing : IDisposable
    {
        private readonly WebhookResponse _layout;
        private readonly long _slotBytes;
        private readonly MemoryMappedFile _map;
        private readonly MemoryMappedViewAccessor _view;

        public FrameRing(WebhookResponse layout)
        {
            _layout = layout;
            _slotBytes = sizeof(ulong) + (long)layout.Width * layout.Height * 4;
            _map = MemoryMappedFile.OpenExisting(layout.MappingName, MemoryMappedFileRights.Read);
            _view = _map.CreateViewAccessor(0, layout.SlotCount * _slotBytes, MemoryMappedFileAccess.Read);
        }

        /// <summary>从第 seq 帧裁出各区域，RGB 平面排列的 uint8 [3, height, width]。</summary>
        /// <returns>拷贝前后 slot 的 seq 不等于 seq（已被覆盖）时为 null。</returns>
        public Dictionary<string, byte[]>? Crop(ulong seq, IReadOnlyDictionary<string, Region> regions)
        {
            var slot = (long)(seq % (ulong)_layout.SlotCount) * _slotBytes;
            if (_view.ReadUInt64(slot) != seq)
            {
                return null;
            }
            var crops = regions.ToDictionary(r => r.Key, r => CropRgb(slot + sizeof(ulong), r.Value));
            return _view.ReadUInt64(slot) == seq ? crops : null;
        }

        public void Dispose()
        {
            _view.Dispose();
            _map.Dispose();
        }

        private byte[] CropRgb(long pixels, Region region)
        {
            var plane = region.Width * region.Height;
            var crop = new byte[3 * plane];
            var row = new byte[region.Width * 4];
            for (var y = 0; y < region.Height; y++)
            {
                _view.ReadArray(pixels + ((long)(region.Top + y) * _layout.Width + region.Left) * 4, row, 0, row.Length);
                for (var x = 0; x < region.Width; x++)
                {
                    // BGRA
                    crop[y * region.Width + x] = row[x * 4 + 2];
                    crop[plane + y * region.Width + x] = row[x * 4 + 1];
                    crop[2 * plane + y * region.Width + x] = row[x * 4];
                }
            }
            return crop;
        }
    }
}
