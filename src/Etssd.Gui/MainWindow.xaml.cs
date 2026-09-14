using System.Collections.Specialized;
using System.ComponentModel;
using System.Diagnostics;
using System.Windows;
using System.Windows.Navigation;

namespace Etssd.Gui;

public partial class MainWindow : Window
{
    private bool _shutdownStarted;
    private bool _shutdownDone;

    public MainWindow()
    {
        InitializeComponent();
        Loaded += OnLoaded;
        Closing += OnClosing;
    }

    private MainViewModel Vm => (MainViewModel)DataContext;

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
        await Vm.ShutdownAsync();
        _shutdownDone = true;
        Close();
    }

    private async void OnLoaded(object sender, RoutedEventArgs e)
    {
        Vm.Logs.CollectionChanged += OnLogsChanged;
        _ = Vm.RunInterfaceAsync();
        await Vm.RunChecksAsync();
    }

    private void OnLogsChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        if (LogList.Items.Count > 0)
        {
            LogList.ScrollIntoView(LogList.Items[^1]);
        }
    }

    private async void Run_Click(object sender, RoutedEventArgs e) => await Vm.StartAsync();

    private void Stop_Click(object sender, RoutedEventArgs e) => Vm.Stop();

    private async void Check_Click(object sender, RoutedEventArgs e) => await Vm.RunChecksAsync();

    private void Link_RequestNavigate(object sender, RequestNavigateEventArgs e)
    {
        Process.Start(new ProcessStartInfo(e.Uri.AbsoluteUri) { UseShellExecute = true });
        e.Handled = true;
    }
}
