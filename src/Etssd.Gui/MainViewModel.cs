using System.Collections.ObjectModel;
using System.Windows.Threading;
using Etssd.Components;
using Etssd.Core;
using Etssd.Doctor;
using Microsoft.Extensions.Logging;

namespace Etssd.Gui;

public sealed record CheckRow(string Name, CheckStatus Status, string Message, string? Link);

/// <summary>interface 随窗口的生命周期运行，infer 由 Run/Stop 控制。</summary>
public sealed class MainViewModel : ObservableObject
{
    private const int MaxLogLines = 2000;

    private readonly AppConfig _config;
    private readonly ILogger _log;
    private readonly IComponent _interface;
    private readonly IComponent _infer;
    private readonly CancellationTokenSource _lifetime = new();
    private CancellationTokenSource? _inferCts;
    private bool _isRunning;

    public MainViewModel(AppConfig config, ILoggerFactory loggers, UiLoggerProvider uiLog, Dispatcher dispatcher)
    {
        _config = config;
        _log = loggers.CreateLogger<MainViewModel>();
        var components = Manifest.All(config, loggers);
        _interface = components.Single(c => c.Name == "interface");
        _infer = components.Single(c => c.Name == "infer");
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

    /// <summary>TextBox 默认在失焦时更新，每次更新即落盘。</summary>
    public string Model
    {
        get => _config.Model;
        set
        {
            if (_config.Model == value)
            {
                return;
            }
            _config.Model = value;
            _config.Save();
            OnPropertyChanged();
        }
    }

    public bool IsRunning
    {
        get => _isRunning;
        private set
        {
            if (SetField(ref _isRunning, value))
            {
                OnPropertyChanged(nameof(CanRun));
            }
        }
    }

    public bool CanRun => !IsRunning;

    /// <summary>运行 interface 直到 <see cref="Shutdown"/>。</summary>
    public Task RunInterfaceAsync() => RunOne(_interface, _lifetime.Token);

    public async Task StartAsync()
    {
        if (IsRunning)
        {
            return;
        }
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(_lifetime.Token);
        _inferCts = cts;
        IsRunning = true;
        await RunOne(_infer, cts.Token);
        _inferCts = null;
        IsRunning = false;
    }

    public void Stop() => _inferCts?.Cancel();

    /// <summary>窗口关闭时停止全部组件。</summary>
    public void Shutdown() => _lifetime.Cancel();

    public async Task RunChecksAsync()
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
