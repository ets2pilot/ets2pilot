using Ets2Pilot.Inference;
using Microsoft.Extensions.Logging;
using Microsoft.ML.OnnxRuntime;

namespace Ets2Pilot.Doctor;

/// <summary>每帧推理耗时。由用户手动触发，不在 <see cref="Checks.All"/> 内。</summary>
public sealed class BenchmarkCheck(string modelDir, ILogger log) : IDoctorCheck
{
    /// <summary>各阶段 p95 之和的上限，ms。</summary>
    public const double MaxP95 = 30;

    public string Name => "benchmark";

    public CheckResult Run()
    {
        IReadOnlyList<Benchmark.Stage> stages;
        try
        {
            stages = new Benchmark(modelDir, log).Run(CancellationToken.None);
        }
        catch (Exception ex) when (ex is OnnxRuntimeException or FileNotFoundException or InvalidOperationException)
        {
            return new(CheckStatus.Failed, ex.Message);
        }
        var total = stages.Sum(s => s.P95);
        var detail = string.Join(" · ", stages.Select(s => $"{s.Name} {s.P95:F1}"));
        return total > MaxP95
            ? new(CheckStatus.Warning, $"p95 合计 {total:F1} ms 超过 {MaxP95} ms（{detail}）")
            : new(CheckStatus.Ok, $"p95 合计 {total:F1} ms（{detail}）");
    }
}
