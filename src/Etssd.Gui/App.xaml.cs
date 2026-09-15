using System.Windows;
using Etssd.Core;
using Microsoft.Extensions.Logging;

namespace Etssd.Gui;

public partial class App : Application
{
    public const string LogRole = "gui";

    private ILoggerFactory? _loggers;

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        var uiLog = new UiLoggerProvider();
        _loggers = EtssdLogging.CreateFactory(LogRole, b => b.AddProvider(uiLog));
        var config = AppConfig.Load(_loggers.CreateLogger<AppConfig>());
        new MainWindow(new MainViewModel(config, _loggers, uiLog, Dispatcher)).Show();
    }

    protected override void OnExit(ExitEventArgs e)
    {
        _loggers?.Dispose();
        base.OnExit(e);
    }
}
