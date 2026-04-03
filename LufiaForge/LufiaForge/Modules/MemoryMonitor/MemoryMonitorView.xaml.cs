using System.Windows;
using System.Windows.Controls;

namespace LufiaForge.Modules.MemoryMonitor;

public partial class MemoryMonitorView : UserControl
{
    public MemoryMonitorView()
    {
        InitializeComponent();
        DataContextChanged += OnDataContextChanged;
    }

    private void OnDataContextChanged(object sender, DependencyPropertyChangedEventArgs e)
    {
        if (e.NewValue is MemoryMonitorViewModel vm)
        {
            // Wire the WinForms Panel (hosting BizHawk) into the WindowsFormsHost
            BizHawkFormsHost.Child = vm.HostPanel;
        }
    }
}
