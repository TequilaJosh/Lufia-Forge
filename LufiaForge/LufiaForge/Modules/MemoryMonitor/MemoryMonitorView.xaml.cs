using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;

namespace LufiaForge.Modules.MemoryMonitor;

public partial class MemoryMonitorView : UserControl
{
    public MemoryMonitorView()
    {
        InitializeComponent();

        // DataContext is set in XAML during InitializeComponent(),
        // so DataContextChanged already fired before we can subscribe.
        // Wire the Panel immediately if DataContext is already set.
        if (DataContext is MemoryMonitorViewModel vm)
            BizHawkFormsHost.Child = vm.HostPanel;

        DataContextChanged += OnDataContextChanged;
    }

    private void OnDataContextChanged(object sender, DependencyPropertyChangedEventArgs e)
    {
        if (e.NewValue is MemoryMonitorViewModel vm)
            BizHawkFormsHost.Child = vm.HostPanel;
    }

    /// <summary>Double-click a label search result to add it to the watchlist.</summary>
    private void LabelResult_DoubleClick(object sender, MouseButtonEventArgs e)
    {
        if (sender is ListBoxItem item && item.DataContext is KnownAddress known)
        {
            if (DataContext is MemoryMonitorViewModel vm)
                vm.AddKnownWatchCommand.Execute(known);
        }
    }
}
