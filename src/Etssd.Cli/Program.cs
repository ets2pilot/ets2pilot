using System.CommandLine;
using Etssd.Components;
using Etssd.Core;
using Etssd.Doctor;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

var root = new RootCommand("ets2-self-driving");
var modelOption = new Option<string?>("--model")
{
    Description = "指定模型",
};

foreach (var name in Manifest.All(new AppConfig(), NullLoggerFactory.Instance).Select(c => c.Name))
{
    var command = new Command(name);
    var hasModel = name == "infer";
    if (hasModel)
    {
        command.Options.Add(modelOption);
    }
    command.SetAction(async (parseResult, ct) =>
    {
        using var loggers = EtssdLogging.CreateFactory(name, b => b.AddSimpleConsole(o =>
        {
            o.SingleLine = true;
            o.TimestampFormat = "HH:mm:ss ";
        }));
        var config = AppConfig.Load(loggers.CreateLogger<AppConfig>());
        if (hasModel && parseResult.GetValue(modelOption) is { } model)
        {
            config = config with { Model = model };
        }
        await Manifest.All(config, loggers).Single(c => c.Name == name).RunAsync(ct);
        return 0;
    });
    root.Subcommands.Add(command);
}

var doctor = new Command("doctor", "检查 vJoy 驱动、telemetry 插件、显卡");
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
