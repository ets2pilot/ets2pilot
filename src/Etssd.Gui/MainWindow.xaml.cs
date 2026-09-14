using System.Collections.Specialized;
using System.Diagnostics;
using System.Windows;
using System.Windows.Navigation;

namespace Etssd.Gui;

public partial class MainWindow : Window
{
    public MainWindow()
    {
        InitializeComponent();
        Loaded += OnLoaded;
        Closing += (_, _) => Vm.Shutdown();
    }

    private MainViewModel Vm => (MainViewModel)DataContext;

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
