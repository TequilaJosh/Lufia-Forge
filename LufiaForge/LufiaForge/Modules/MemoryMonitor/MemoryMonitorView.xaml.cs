using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;

namespace LufiaForge.Modules.MemoryMonitor;

public partial class MemoryMonitorView : UserControl
{
    public MemoryMonitorView()
    {
        InitializeComponent();

        // the XAML sets DataContext inside InitializeComponent(), so DataContextChanged already fired before we
        // could listen. hook up the panel right away if it's set
        if (DataContext is MemoryMonitorViewModel viewModel)
            BizHawkFormsHost.Child = viewModel.HostPanel;

        DataContextChanged += OnDataContextChanged;
    }

    private void OnDataContextChanged(object sender, DependencyPropertyChangedEventArgs e)
    {
        if (e.NewValue is MemoryMonitorViewModel viewModel)
            BizHawkFormsHost.Child = viewModel.HostPanel;
    }

    // double-click a label search result to put it on the watchlist
    private void LabelResult_DoubleClick(object sender, MouseButtonEventArgs e)
    {
        if (sender is ListBoxItem item && item.DataContext is KnownAddress known)
        {
            if (DataContext is MemoryMonitorViewModel viewModel)
                viewModel.AddKnownWatchCommand.Execute(known);
        }
    }
}
