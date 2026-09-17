using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using IComponent = Etssd.Core.IComponent;
using System.Windows.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Etssd.Bridge;
using Etssd.Bridge.Http;
using Etssd.Core;
using Etssd.Core.Models;
using Etssd.Doctor;
using Etssd.Inference;
using Microsoft.Extensions.Logging;

namespace Etssd.Gui;

public enum ReadinessStatus
{
    Unknown,
    Ok,
    Warning,
    Failed,
    Busy,
}

public sealed record CheckRow(string Name, ReadinessStatus Status, string Message, string? Link, string? LinkText);

/// <param name="Endpoint">写入 config.toml 的 hf_endpoint，空表示直连。</param>
public sealed record MirrorOption(string Label, string Endpoint);

/// <summary>bridge 与 control 各自随窗口的生命周期运行，infer 由运行模型与停止控制。</summary>
public sealed partial class MainViewModel : ObservableObject
{
    private const int MaxLogLines = 2000;
    private const string HfMirror = "https://hf-mirror.com";

    /// <summary>随组件运行状态与 bridge 订阅变化的属性，由 <see cref="NotifyStatusChanged"/> 统一刷新。</summary>
    private static readonly string[] StatusProperties =
    [
        nameof(IsBridgeRunning),
        nameof(IsControlRunning),
        nameof(IsInferRunning),
        nameof(Callbacks),
        nameof(InferFreq),
        nameof(IsControlEnabled),
        nameof(OverviewStatus),
        nameof(OverviewTitle),
        nameof(OverviewSubtitle),
    ];

    private readonly AppConfig _config;
    private readonly ILoggerFactory _loggers;
    private readonly ILogger _log;
    private readonly BridgeComponent _bridge;
    private readonly ControlComponent _control;
    private readonly CancellationTokenSource _lifetime = new();
    private readonly DispatcherTimer _statusTimer;
    private readonly Stopwatch _inferElapsed = new();
    private readonly Stopwatch _downloadElapsed = new();
    private Task _bridgeRun = Task.CompletedTask;
    private Task _controlRun = Task.CompletedTask;
    private string? _downloadFile;
    private IReadOnlyList<SubscriptionInfo> _subscriptions = [];

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ModelStatus), nameof(ModelMessage), nameof(IsModelBusy), nameof(ModelProgress), nameof(IsModelProgressIndeterminate))]
    private ModelState? _model;

    [ObservableProperty]
    private MirrorOption _selectedMirror;

    public MainViewModel(AppConfig config, ILoggerFactory loggers, UiLoggerProvider uiLog, Dispatcher dispatcher)
    {
        _config = config;
        _loggers = loggers;
        _log = loggers.CreateLogger<MainViewModel>();
        _bridge = new BridgeComponent(loggers);
        _control = new ControlComponent(loggers);
        Mirrors = [new(Strings.Get("Text.Settings.MirrorDirect"), ""), new(new Uri(HfMirror).Host, HfMirror)];
        if (Mirrors.All(m => m.Endpoint != config.HfEndpoint))
        {
            Mirrors.Add(new(config.HfEndpoint, config.HfEndpoint));
        }
        _selectedMirror = Mirrors.First(m => m.Endpoint == config.HfEndpoint);
        uiLog.EntryLogged += entry => dispatcher.BeginInvoke(() =>
        {
            Logs.Add(entry);
            if (Logs.Count > MaxLogLines)
            {
                Logs.RemoveAt(0);
            }
        });
        InferCommand.PropertyChanged += OnInferCommandChanged;
        _statusTimer = new DispatcherTimer(TimeSpan.FromSeconds(1), DispatcherPriority.Background, (_, _) => NotifyStatusChanged(), dispatcher);
    }

    public ObservableCollection<CheckRow> Checks { get; } = [];

    public ObservableCollection<LogEntry> Logs { get; } = [];

    public string LogRetention => Strings.Format("Text.Logs.Retention", MaxLogLines);

    public string LogFile => AppPaths.LogFile(App.LogRole);

    public string DataDir => AppPaths.Root;

    public int BridgePort { get; } = new Uri(BridgeServer.Url).Port;

    public int ModelPort { get; } = new Uri(ModelServer.Url).Port;

    public int ControlPort { get; } = new Uri(ControlServer.Url).Port;

    public ObservableCollection<MirrorOption> Mirrors { get; }

    public string ModelRepo
    {
        get => _config.ModelRepo;
        set
        {
            if (SetProperty(_config.ModelRepo, value, _config, (c, v) => c.ModelRepo = v))
            {
                OnConfigChanged();
            }
        }
    }

    public string ModelRevision
    {
        get => _config.ModelRevision;
        set
        {
            if (SetProperty(_config.ModelRevision, value, _config, (c, v) => c.ModelRevision = v))
            {
                OnConfigChanged();
            }
        }
    }

    public bool IsBridgeRunning => !_bridgeRun.IsCompleted;

    public bool IsControlRunning => !_controlRun.IsCompleted;

    public bool IsInferRunning => InferCommand.IsRunning;

    public string Callbacks => _subscriptions.Count == 0
        ? Strings.Get("Text.Status.NoCallbacks")
        : string.Join(" · ", _subscriptions.Select(s => $"{s.Name} {s.Freq:0.#} Hz"));

    /// <summary>infer 订阅的频率，未注册时为 null。</summary>
    public string? InferFreq => InferSubscription is { } infer ? $"{infer.Freq:0.#} Hz" : null;

    /// <summary>control 只在收到轨迹后注册 telemetry 订阅，订阅存在即表示控制已启用。</summary>
    public bool IsControlEnabled => _subscriptions.Any(s => s.Name == "control");

    public ReadinessStatus OverviewStatus =>
        IsInferRunning ? ReadinessStatus.Ok : CanInfer() ? ReadinessStatus.Unknown : ReadinessStatus.Warning;

    public string OverviewTitle => Strings.Get(
        IsInferRunning ? "Text.Overview.TitleRunning" : CanInfer() ? "Text.Overview.TitleReady" : "Text.Overview.TitleNotReady");

    public string OverviewSubtitle =>
        IsInferRunning
            ? Strings.Format(
                "Text.Overview.SubtitleRunning",
                ModelName, RevisionName, _inferElapsed.Elapsed.ToString(@"mm\:ss"), InferSubscription?.Dropped ?? 0)
            : Strings.Get(CanInfer() ? "Text.Overview.SubtitleReady" : "Text.Overview.SubtitleNotReady");

    public ReadinessStatus ModelStatus => Model switch
    {
        null => ReadinessStatus.Unknown,
        ModelState.Checking or ModelState.Downloading => ReadinessStatus.Busy,
        ModelState.Ready { Warning: null } => ReadinessStatus.Ok,
        ModelState.Ready => ReadinessStatus.Warning,
        _ => ReadinessStatus.Failed,
    };

    public string ModelMessage => Model switch
    {
        null => Strings.Get("Text.Model.Unchecked"),
        ModelState.Checking => Strings.Format("Text.Model.Checking", _config.ModelRepo),
        ModelState.Downloading d => Strings.Format(
            "Text.Model.Downloading",
            RevisionName,
            d.File,
            d.Received / 1e6,
            d.Total is { } total ? $"{total / 1e6:0}" : "?",
            d.Received / 1e6 / Math.Max(_downloadElapsed.Elapsed.TotalSeconds, 1e-3)),
        ModelState.Ready { Warning: { } warning } => Strings.Format("Text.Model.Cached", ModelName, RevisionName, warning),
        ModelState.Ready ready => ready.LastModified is { } time
            ? Strings.Format("Text.Model.LatestAt", ModelName, RevisionName, time.LocalDateTime)
            : Strings.Format("Text.Model.Latest", ModelName, RevisionName),
        ModelState.Error error => error.Message,
        _ => "",
    };

    public bool IsModelBusy => ModelStatus == ReadinessStatus.Busy;

    /// <summary>0 到 100。</summary>
    public double ModelProgress => Model is ModelState.Downloading { Total: > 0 } d ? 100.0 * d.Received / d.Total.Value : 0;

    public bool IsModelProgressIndeterminate => Model is not ModelState.Downloading { Total: > 0 };

    private string ModelName => _config.ModelRepo.Split('/')[^1];

    private string RevisionName => _config.ModelRevision.Length == 0 ? "main" : _config.ModelRevision;

    private SubscriptionInfo? InferSubscription => _subscriptions.FirstOrDefault(s => s.Name == "infer");

    /// <summary>运行 bridge 与 control 直到 <see cref="ShutdownAsync"/>，并开始刷新状态栏。</summary>
    public void Start()
    {
        _bridgeRun = RunOne(_bridge, _lifetime.Token);
        _controlRun = RunOne(_control, _lifetime.Token);
        _statusTimer.Start();
    }

    /// <summary>停止全部组件，等待 bridge 向订阅者发完 end 通知。</summary>
    public async Task ShutdownAsync()
    {
        _statusTimer.Stop();
        await _lifetime.CancelAsync();
        await Task.WhenAll(_bridgeRun, _controlRun);
    }

    /// <summary>生成 InferCommand 与 InferCancelCommand，执行期间 InferCommand 不可用。</summary>
    [RelayCommand(IncludeCancelCommand = true, CanExecute = nameof(CanInfer))]
    private async Task InferAsync(CancellationToken token)
    {
        if (Model is not ModelState.Ready ready)
        {
            return;
        }
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(_lifetime.Token, token);
        _inferElapsed.Restart();
        await RunOne(new InferenceComponent(ready.Dir, _loggers), cts.Token);
        _inferElapsed.Stop();
    }

    /// <summary>有 Failed 检查项、检查未完成或模型不可用时不能运行。</summary>
    private bool CanInfer() =>
        Model is ModelState.Ready
        && Checks.Count == Etssd.Doctor.Checks.All.Count
        && Checks.All(c => c.Status != ReadinessStatus.Failed);

    /// <summary>生成 CheckModelCommand 与 CheckModelCancelCommand。缓存与 hub 不一致时下载。</summary>
    [RelayCommand(IncludeCancelCommand = true)]
    private async Task CheckModelAsync(CancellationToken token)
    {
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(_lifetime.Token, token);
        // Progress 异步投递到 UI 线程，结束后到达的进度不能覆盖最终状态
        var finished = false;
        var progress = new Progress<ModelState>(state =>
        {
            if (!finished)
            {
                OnModelProgress(state);
            }
        });
        ModelState? result;
        try
        {
            result = await ModelStore.ResolveAsync(_config, progress, cts.Token);
        }
        catch (OperationCanceledException)
        {
            result = null;
        }
        finished = true;
        Model = result;
        switch (result)
        {
            case ModelState.Error error:
                _log.LogError(AppEvents.UserVisible, "模型不可用：{Message}", error.Message);
                break;
            case ModelState.Ready { Warning: { } warning }:
                _log.LogWarning(AppEvents.UserVisible, "使用缓存的模型：{Warning}", warning);
                break;
            case ModelState.Ready ready:
                _log.LogInformation(AppEvents.UserVisible, "{Repo} 已是最新 {Sha}，缓存 {Dir}", _config.ModelRepo, ready.Sha, ready.Dir);
                break;
        }
    }

    [RelayCommand]
    private async Task RunChecksAsync()
    {
        Checks.Clear();
        NotifyStatusChanged();
        foreach (var check in Etssd.Doctor.Checks.All)
        {
            var result = await Task.Run(check.Run);
            var (name, linkText) = Describe(check);
            Checks.Add(new CheckRow(name, ToReadiness(result.Status), result.Message, result.Link, linkText));
            _log.LogInformation("[{Status}] {Name}: {Message}", result.Status, check.Name, result.Message);
        }
        NotifyStatusChanged();
    }

    [RelayCommand]
    private void OpenLogFile() => ShellOpen(LogFile);

    [RelayCommand]
    private void OpenLogDir() => ShellOpen(AppPaths.LogDir);

    [RelayCommand]
    private void OpenDataDir() => ShellOpen(AppPaths.Root);

    partial void OnModelChanged(ModelState? value) => NotifyStatusChanged();

    partial void OnSelectedMirrorChanged(MirrorOption value)
    {
        _config.HfEndpoint = value.Endpoint;
        OnConfigChanged();
    }

    private void OnModelProgress(ModelState state)
    {
        if (state is ModelState.Downloading downloading && downloading.File != _downloadFile)
        {
            _downloadFile = downloading.File;
            _downloadElapsed.Restart();
        }
        Model = state;
    }

    /// <summary>保存配置。已解析的模型属于旧配置，取消进行中的检查并清空模型状态。</summary>
    private void OnConfigChanged()
    {
        try
        {
            _config.Save();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _log.LogError(AppEvents.UserVisible, ex, "{Path} 无法写入", AppPaths.ConfigFile);
        }
        CheckModelCancelCommand.Execute(null);
        Model = null;
    }

    private void OnInferCommandChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(IAsyncRelayCommand.IsRunning))
        {
            NotifyStatusChanged();
        }
    }

    private void NotifyStatusChanged()
    {
        _subscriptions = _bridge.Subscriptions;
        foreach (var property in StatusProperties)
        {
            OnPropertyChanged(property);
        }
        InferCommand.NotifyCanExecuteChanged();
    }

    private static (string Name, string? LinkText) Describe(IDoctorCheck check) => check switch
    {
        VJoyCheck => (Strings.Get("Text.Check.VJoy"), Strings.Get("Text.Check.VJoyLink")),
        GameCheck => (Strings.Get("Text.Check.Game"), null),
        TelemetryCheck => (Strings.Get("Text.Check.Telemetry"), Strings.Get("Text.Check.TelemetryLink")),
        GpuCheck => (Strings.Get("Text.Check.Gpu"), null),
        EpCheck => (Strings.Get("Text.Check.Ep"), Strings.Get("Text.Check.EpLink")),
        _ => (check.Name, Strings.Get("Text.Check.DefaultLink")),
    };

    private static ReadinessStatus ToReadiness(CheckStatus status) => status switch
    {
        CheckStatus.Unknown => ReadinessStatus.Unknown,
        CheckStatus.Ok => ReadinessStatus.Ok,
        CheckStatus.Warning => ReadinessStatus.Warning,
        _ => ReadinessStatus.Failed,
    };

    private void ShellOpen(string path)
    {
        try
        {
            Process.Start(new ProcessStartInfo(path) { UseShellExecute = true });
        }
        catch (Win32Exception ex)
        {
            _log.LogWarning(AppEvents.UserVisible, "无法打开 {Path}：{Message}", path, ex.Message);
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
            _log.LogError(AppEvents.UserVisible, ex, "{Name} 异常退出", component.Name);
        }
    }
}
