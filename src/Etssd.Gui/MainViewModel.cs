using System.Collections.ObjectModel;
using System.Windows.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Etssd.Bridge;
using Etssd.Core;
using Etssd.Core.Models;
using Etssd.Doctor;
using Etssd.Inference;
using Microsoft.Extensions.Logging;

namespace Etssd.Gui;

public sealed record CheckRow(string Name, CheckStatus Status, string Message, string? Link);

/// <summary>bridge 与 control 各自随窗口的生命周期运行，infer 由 Run/Stop 控制。</summary>
public sealed partial class MainViewModel : ObservableObject
{
    private const int MaxLogLines = 2000;

    private readonly AppConfig _config;
    private readonly ILoggerFactory _loggers;
    private readonly ILogger _log;
    private readonly IComponent _bridge;
    private readonly IComponent _control;
    private readonly CancellationTokenSource _lifetime = new();
    private Task _bridgeRun = Task.CompletedTask;
    private Task _controlRun = Task.CompletedTask;

    public MainViewModel(AppConfig config, ILoggerFactory loggers, UiLoggerProvider uiLog, Dispatcher dispatcher)
    {
        _config = config;
        _loggers = loggers;
        _log = loggers.CreateLogger<MainViewModel>();
        _bridge = new BridgeComponent(loggers);
        _control = new ControlComponent(loggers);
        uiLog.EntryLogged += line => dispatcher.BeginInvoke(() =>
        {
            Logs.Add(line);
            if (Logs.Count > MaxLogLines)
            {
                Logs.RemoveAt(0);
            }
        });
    }

    public ObservableCollection<CheckRow> Checks { get; } = [];

    public ObservableCollection<string> Logs { get; } = [];

    /// <summary>运行 bridge 直到 <see cref="ShutdownAsync"/>。</summary>
    public Task RunBridgeAsync() => _bridgeRun = RunOne(_bridge, _lifetime.Token);

    /// <summary>运行 control 直到 <see cref="ShutdownAsync"/>。</summary>
    public Task RunControlAsync() => _controlRun = RunOne(_control, _lifetime.Token);

    /// <summary>生成 InferCommand 与 InferCancelCommand，执行期间 InferCommand 不可用。</summary>
    [RelayCommand(IncludeCancelCommand = true)]
    private async Task InferAsync(CancellationToken token)
    {
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(_lifetime.Token, token);
        if (await ResolveModelDirAsync(cts.Token) is { } modelDir)
        {
            await RunOne(new InferenceComponent(modelDir, _loggers), cts.Token);
        }
    }

    /// <summary>模型不可用或已取消时返回 null。</summary>
    private async Task<string?> ResolveModelDirAsync(CancellationToken ct)
    {
        ModelState state;
        try
        {
            state = await ModelStore.ResolveAsync(_config, new Progress<ModelState>(), ct);
        }
        catch (OperationCanceledException)
        {
            return null;
        }
        if (state is ModelState.Error error)
        {
            _log.LogError("模型不可用：{Message}", error.Message);
            return null;
        }
        var ready = (ModelState.Ready)state;
        if (ready.Warning is { } warning)
        {
            _log.LogWarning("使用缓存的模型：{Warning}", warning);
        }
        return ready.Dir;
    }

    /// <summary>停止全部组件，等待 bridge 向订阅者发完 end 通知。</summary>
    public async Task ShutdownAsync()
    {
        await _lifetime.CancelAsync();
        await Task.WhenAll(_bridgeRun, _controlRun);
    }

    [RelayCommand]
    private async Task RunChecksAsync()
    {
        Checks.Clear();
        foreach (var check in Etssd.Doctor.Checks.All)
        {
            var result = await Task.Run(check.Run);
            Checks.Add(new CheckRow(check.Name, result.Status, result.Message, result.Link));
            _log.LogInformation("[{Status}] {Name}: {Message}", result.Status, check.Name, result.Message);
        }
    }

    private async Task RunOne(IComponent component, CancellationToken ct)
    {
        try
        {
            await Task.Run(() => component.RunAsync(ct), ct);
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception ex)
        {
            _log.LogError(ex, "{Name} 异常退出", component.Name);
        }
    }
}
