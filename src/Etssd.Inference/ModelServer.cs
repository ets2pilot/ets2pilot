using System.IO.MemoryMappedFiles;
using System.Net.Http.Json;
using Etssd.Interface.Http;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.ML.OnnxRuntime;
using SessionOptions = Microsoft.ML.OnnxRuntime.SessionOptions;

namespace Etssd.Inference;

/// <summary>模型算法 server。注册 image+telemetry webhook，推理出的轨迹 POST 到 control server。</summary>
/// <param name="modelDir">含 telemetry_encoder.onnx、image_encoder.onnx、decoder.onnx 的目录。</param>
public sealed class ModelServer(string modelDir, HttpClient http)
{
    public const string Url = "http://127.0.0.1:5321";

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

        // 注册的响应给出帧共享内存的布局，通知可能先于响应到达
        var frames = new TaskCompletionSource<FrameRing>(TaskCreationOptions.RunContinuationsAsynchronously);
        var window = new Queue<(byte[] Telemetry, IDisposableReadOnlyCollection<OrtValue> Image)>();

        app.MapPost("/notify", async (Notification notification) =>
        {
            if (notification is not { Event: "data", Seq: { } seq, Telemetry: { } telemetry })
            {
                return Results.NoContent();
            }
            if ((await frames.Task).Crop(seq, imageEncoder.Regions) is not { } crops)
            {
                return Results.NoContent();
            }
            window.Enqueue((telemetry, imageEncoder.Forward(crops)));
            if (window.Count > decoder.Frames)
            {
                window.Dequeue().Image.Dispose();
            }
            if (window.Count == decoder.Frames)
            {
                using var encoded = telemetryEncoder.Forward([.. window.Select(w => w.Telemetry)]);
                var (speed, yawRate) = decoder.Forward(encoded, [.. window.Select(w => w.Image)]);
                using var response = await http.PostAsJsonAsync($"{ControlServer.Url}/trajectory",
                    new TrajectoryRequest(decoder.Freq, telemetry, speed, yawRate), Json.Options);
            }
            return Results.NoContent();
        });

        await app.StartAsync(ct);
        using var registered = await http.PostAsJsonAsync($"{InterfaceServer.Url}/webhook",
            new WebhookRequest("infer", SubscriptionType.ImageTelemetry, decoder.Freq, $"{Url}/notify"), Json.Options, ct);
        registered.EnsureSuccessStatusCode();
        using var ring = new FrameRing((await registered.Content.ReadFromJsonAsync<WebhookResponse>(Json.Options, ct))!);
        frames.SetResult(ring);
        try
        {
            await Task.Delay(Timeout.Infinite, ct);
        }
        catch (OperationCanceledException)
        {
        }
        await app.StopAsync();
        foreach (var (_, image) in window)
        {
            image.Dispose();
        }
    }

    /// <summary>帧共享内存的读端，布局见 <see cref="Interface.Frames.FrameRing"/>。</summary>
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
