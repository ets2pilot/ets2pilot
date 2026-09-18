using System.ComponentModel;
using System.Windows;
using Ets2Pilot.Gui.Pages;
using Wpf.Ui.Abstractions;
using Wpf.Ui.Controls;

namespace Ets2Pilot.Gui;

public partial class MainWindow : FluentWindow
{
    private readonly MainViewModel _vm;
    private bool _shutdownStarted;
    private bool _shutdownDone;

    public MainWindow(MainViewModel vm)
    {
        _vm = vm;
        DataContext = vm;
        InitializeComponent();
        // NavigationView 的内容区是 Frame，页面不继承窗口的 DataContext
        RootNavigation.SetPageProviderService(new PageProvider(new Dictionary<Type, FrameworkElement>
        {
            [typeof(OverviewPage)] = new OverviewPage { DataContext = vm },
            [typeof(LogsPage)] = new LogsPage { DataContext = vm },
            [typeof(SettingsPage)] = new SettingsPage { DataContext = vm },
        }));
        Loaded += OnLoaded;
        Closing += OnClosing;
    }

    /// <summary>进程随窗口关闭退出，先取消关闭，等组件停止后再关一次。</summary>
    private async void OnClosing(object? sender, CancelEventArgs e)
    {
        if (_shutdownDone)
        {
            return;
        }
        e.Cancel = true;
        if (_shutdownStarted)
        {
            return;
        }
        _shutdownStarted = true;
        await _vm.ShutdownAsync();
        _shutdownDone = true;
        Close();
    }

    private async void OnLoaded(object sender, RoutedEventArgs e)
    {
        RootNavigation.Navigate(typeof(OverviewPage));
        _vm.Start();
        await Task.WhenAll(
            _vm.RunChecksCommand.ExecuteAsync(null),
            _vm.CheckModelCommand.ExecuteAsync(null));
    }

    private sealed class PageProvider(IReadOnlyDictionary<Type, FrameworkElement> pages) : INavigationViewPageProvider
    {
        public object? GetPage(Type pageType) => pages.GetValueOrDefault(pageType);
    }
}
