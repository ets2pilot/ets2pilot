using System.Windows;
using Ets2Pilot.Core;
using Microsoft.Extensions.Logging;

namespace Ets2Pilot.Gui;

public partial class App : Application
{
    public const string LogRole = "gui";

    private ILoggerFactory? _loggers;

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        var uiLog = new UiLoggerProvider();
        _loggers = Ets2PilotLogging.CreateFactory(LogRole, b => b.AddProvider(uiLog));
        var config = AppConfig.Load(_loggers.CreateLogger<AppConfig>());
        new MainWindow(new MainViewModel(config, _loggers, uiLog, Dispatcher)).Show();
    }

    protected override void OnExit(ExitEventArgs e)
    {
        _loggers?.Dispose();
        base.OnExit(e);
    }
}
