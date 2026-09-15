using Etssd.Core;
using Etssd.Inference;
using Etssd.Bridge;
using Microsoft.Extensions.Logging;

namespace Etssd.Components;

/// <summary>组件清单。新组件在此加一项，CLI 子命令与 GUI 开关随之出现。</summary>
public static class Manifest
{
    /// <summary>构造无副作用，组件在 RunAsync 才申请资源。</summary>
    public static IReadOnlyList<IComponent> All(AppConfig config, ILoggerFactory loggers) =>
    [
        new BridgeComponent(loggers),
        new ControlComponent(loggers),
        new InferenceComponent(config, loggers),
    ];
}
