using System.Collections.Specialized;
using System.Windows;
using System.Windows.Controls;

namespace Etssd.Gui.Pages;

public partial class LogsPage : Page
{
    public LogsPage()
    {
        InitializeComponent();
        Loaded += OnLoaded;
        Unloaded += OnUnloaded;
    }

    private MainViewModel Vm => (MainViewModel)DataContext;

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        Vm.Logs.CollectionChanged += OnLogsChanged;
        ScrollToEnd();
    }

    private void OnUnloaded(object sender, RoutedEventArgs e) => Vm.Logs.CollectionChanged -= OnLogsChanged;

    private void OnLogsChanged(object? sender, NotifyCollectionChangedEventArgs e) => ScrollToEnd();

    private void ScrollToEnd()
    {
        if (AutoScroll.IsChecked == true && LogList.Items.Count > 0)
        {
            LogList.ScrollIntoView(LogList.Items[^1]);
        }
    }
}
