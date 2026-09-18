namespace Ets2Pilot.Core;

/// <summary>长期运行的组件。CLI 按 Name 生成子命令，GUI 按清单渲染开关。</summary>
public interface IComponent
{
    string Name { get; }

    /// <summary>运行到 ct 取消为止，取消后释放资源并正常返回。</summary>
    Task RunAsync(CancellationToken ct);
}
