using System.CommandLine;
using Ets2Pilot.Bridge;
using Ets2Pilot.Core;
using Ets2Pilot.Core.Models;
using Ets2Pilot.Doctor;
using Ets2Pilot.Inference;
using Microsoft.Extensions.Logging;

var root = new RootCommand("ets2pilot");

var bridge = new Command("bridge");
bridge.SetAction(async (_, ct) =>
{
    using var loggers = CreateLoggers("bridge");
    await new BridgeComponent(loggers).RunAsync(ct);
    return 0;
});
root.Subcommands.Add(bridge);

var control = new Command("control");
control.SetAction(async (_, ct) =>
{
    using var loggers = CreateLoggers("control");
    await new ControlComponent(loggers).RunAsync(ct);
    return 0;
});
root.Subcommands.Add(control);

var modelOption = new Option<string?>("--model")
{
    Description = "本地模型目录，缺省时按 config.toml 从 Hugging Face 解析",
};
var infer = new Command("infer");
infer.Options.Add(modelOption);
infer.SetAction(async (parseResult, ct) =>
{
    using var loggers = CreateLoggers("infer");
    var modelDir = parseResult.GetValue(modelOption) ?? await ResolveModelDirAsync(loggers, ct);
    if (modelDir is null)
    {
        return 1;
    }
    await new InferenceComponent(modelDir, loggers).RunAsync(ct);
    return 0;
});
root.Subcommands.Add(infer);

var bench = new Command("bench", "测每帧推理各阶段的耗时分布");
bench.Options.Add(modelOption);
bench.SetAction(async (parseResult, ct) =>
{
    using var loggers = CreateLoggers("bench");
    var modelDir = parseResult.GetValue(modelOption) ?? await ResolveModelDirAsync(loggers, ct);
    if (modelDir is null)
    {
        return 1;
    }
    var stages = new Benchmark(modelDir, loggers.CreateLogger<Benchmark>()).Run(ct);
    Console.WriteLine($"{"stage",-10}{"p50 ms",9}{"p95 ms",9}{"max ms",9}");
    foreach (var stage in stages)
    {
        Console.WriteLine($"{stage.Name,-10}{stage.P50,9:F2}{stage.P95,9:F2}{stage.Max,9:F2}");
    }
    return 0;
});
root.Subcommands.Add(bench);

var doctor = new Command("doctor", "检查 vJoy 驱动、游戏、telemetry 插件、显卡、TensorRT-RTX EP");
doctor.SetAction(_ =>
{
    var failed = false;
    foreach (var check in Checks.All)
    {
        var result = check.Run();
        failed |= result.Status == CheckStatus.Failed;
        var link = result.Link is null ? "" : $"  {result.Link}";
        Console.WriteLine($"[{result.Status}] {check.Name}: {result.Message}{link}");
    }
    return failed ? 1 : 0;
});
root.Subcommands.Add(doctor);

return await root.Parse(args).InvokeAsync();

static ILoggerFactory CreateLoggers(string role) =>
    Ets2PilotLogging.CreateFactory(role, b => b.AddSimpleConsole(o =>
    {
        o.SingleLine = true;
        o.TimestampFormat = "HH:mm:ss ";
    }));

static async Task<string?> ResolveModelDirAsync(ILoggerFactory loggers, CancellationToken ct)
{
    var log = loggers.CreateLogger(typeof(ModelStore));
    var config = AppConfig.Load(loggers.CreateLogger<AppConfig>());
    var state = await ModelStore.ResolveAsync(config, new ModelProgress(log), ct);
    if (state is ModelState.Error error)
    {
        log.LogError("模型不可用：{Message}", error.Message);
        return null;
    }
    var ready = (ModelState.Ready)state;
    if (ready.Warning is { } warning)
    {
        log.LogWarning("使用缓存的模型：{Warning}", warning);
    }
    return ready.Dir;
}

/// <summary>把模型解析进度打进日志。</summary>
sealed class ModelProgress(ILogger log) : IProgress<ModelState>
{
    private string _file = "";
    private long _tick;
    private int _decile = -1;

    public void Report(ModelState value)
    {
        switch (value)
        {
            case ModelState.Checking:
                log.LogInformation("正在检查模型仓库");
                break;
            case ModelState.Downloading downloading when ShouldLog(downloading):
                log.LogInformation(
                    "正在下载 {File} {Received}/{Total} MB",
                    downloading.File,
                    downloading.Received >> 20,
                    downloading.Total is { } total ? (total >> 20).ToString() : "?");
                break;
        }
    }

    /// <summary>同一文件每秒或每 10% 放过一条。</summary>
    private bool ShouldLog(ModelState.Downloading downloading)
    {
        var tick = Environment.TickCount64;
        var decile = downloading.Total is { } total ? (int)(downloading.Received * 10 / total) : 0;
        if (downloading.File == _file && tick - _tick < 1000 && decile == _decile)
        {
            return false;
        }
        _file = downloading.File;
        _tick = tick;
        _decile = decile;
        return true;
    }
}
