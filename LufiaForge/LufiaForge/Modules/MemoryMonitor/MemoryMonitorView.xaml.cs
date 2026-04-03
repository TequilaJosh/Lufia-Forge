using System.Windows;
using System.Windows.Controls;

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
}
