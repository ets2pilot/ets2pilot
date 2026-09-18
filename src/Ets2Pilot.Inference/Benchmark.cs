using System.Diagnostics;
using Ets2Pilot.Bridge.Sensing;
using Ets2Pilot.Core;
using Microsoft.Extensions.Logging;
using Microsoft.ML.OnnxRuntime;

namespace Ets2Pilot.Inference;

/// <summary>测每帧推理各阶段的耗时分布。</summary>
/// <param name="modelDir">含 telemetry_encoder.onnx、image_encoder.onnx、decoder.onnx 的目录。</param>
public sealed class Benchmark(string modelDir, ILogger log)
{
    private const int Warmup = 10;

    private const int Iterations = 100;

    /// <summary>耗时分位数，ms。</summary>
    public sealed record Stage(string Name, double P50, double P95, double Max);

    public IReadOnlyList<Stage> Run(CancellationToken ct)
    {
        GpuProviders.Register(log);
        using var telemetryEncoder = new TelemetryEncoder(Load("telemetry_encoder.onnx", GpuProviders.CreateDirectMlSession));
        using var imageEncoder = new ImageEncoder(Load("image_encoder.onnx", CreateTensorRtRtxSession));
        using var decoder = new Decoder(Load("decoder.onnx", CreateTensorRtRtxSession));

        var random = new Random(0);
        var crops = imageEncoder.Regions.ToDictionary(r => r.Key, r => RandomCrop(random, r.Value));
        // 耗时与取值无关，全零避开 NaN
        var telemetryWindow = Enumerable.Repeat(new byte[ScsTelemetry.Size], decoder.Frames).ToArray();
        var window = new Queue<IDisposableReadOnlyCollection<OrtValue>>();
        var image = new StageClock("image");
        var telemetry = new StageClock("telemetry");
        var decode = new StageClock("decoder");

        log.LogInformation(AppEvents.UserVisible, "正在推理，预热 {Warmup} 次后计时 {Iterations} 次", Warmup, Iterations);
        // decoder 需要满窗口，前 Frames - 1 次只填窗
        for (var i = 0; i < decoder.Frames - 1 + Warmup + Iterations; i++)
        {
            ct.ThrowIfCancellationRequested();
            window.Enqueue(image.Time(() => imageEncoder.Forward(crops)));
            if (window.Count > decoder.Frames)
            {
                window.Dequeue().Dispose();
            }
            if (window.Count < decoder.Frames)
            {
                continue;
            }
            using var encoded = telemetry.Time(() => telemetryEncoder.Forward(telemetryWindow));
            decode.Time(() => decoder.Forward(encoded, [.. window]));
        }
        foreach (var value in window)
        {
            value.Dispose();
        }
        return [image.Summarize(), telemetry.Summarize(), decode.Summarize()];
    }

    private InferenceSession Load(string file, Func<string, InferenceSession> create)
    {
        var start = Stopwatch.GetTimestamp();
        var session = create(Path.Combine(modelDir, file));
        log.LogInformation(AppEvents.UserVisible, "{Model} 加载用时 {Seconds:F1} s", file, Stopwatch.GetElapsedTime(start).TotalSeconds);
        return session;
    }

    private InferenceSession CreateTensorRtRtxSession(string path) => GpuProviders.CreateTensorRtRtxSession(path, log);

    /// <summary>RGB 平面排列的 uint8 [3, height, width]。</summary>
    private static byte[] RandomCrop(Random random, Region region)
    {
        var crop = new byte[3 * region.Width * region.Height];
        random.NextBytes(crop);
        return crop;
    }

    /// <summary>记录每次调用的耗时，统计最后 Iterations 次。</summary>
    private sealed class StageClock(string name)
    {
        private readonly List<double> _ms = [];

        public T Time<T>(Func<T> call)
        {
            var start = Stopwatch.GetTimestamp();
            var result = call();
            _ms.Add(Stopwatch.GetElapsedTime(start).TotalMilliseconds);
            return result;
        }

        public Stage Summarize()
        {
            var sorted = _ms.TakeLast(Iterations).Order().ToArray();
            return new Stage(name, Percentile(sorted, 0.5), Percentile(sorted, 0.95), sorted[^1]);
        }

        /// <param name="sorted">升序样本。</param>
        private static double Percentile(double[] sorted, double q) =>
            sorted[(int)Math.Ceiling(q * sorted.Length) - 1];
    }
}
