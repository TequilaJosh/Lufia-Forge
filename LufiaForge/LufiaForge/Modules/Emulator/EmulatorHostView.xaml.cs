using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;

namespace LufiaForge.Modules.Emulator;

public partial class EmulatorHostView : UserControl
{
    private EmulatorHost?          _host;
    private EmulatorHostViewModel? _vm;

    public EmulatorHostView()
    {
        InitializeComponent();
        DataContextChanged += OnDataContextChanged;
        Loaded             += OnLoaded;
        Unloaded           += OnUnloaded;
    }

    private void OnDataContextChanged(object sender, DependencyPropertyChangedEventArgs e)
    {
        _vm = e.NewValue as EmulatorHostViewModel;
    }

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        if (_vm == null) return;

        // Create and insert the HwndHost into the border
        _host     = new EmulatorHost();
        _vm.Host  = _host;

        _host.StatusChanged += (_, msg) =>
            Dispatcher.InvokeAsync(() => { if (_vm != null) _vm.StatusText = msg; });

        EmulatorHostContainer.Children.Add(_host);
    }

    private void OnUnloaded(object sender, RoutedEventArgs e)
    {
        _host?.Shutdown();
        _vm?.Dispose();
    }

    private void EmulatorBorder_SizeChanged(object sender, SizeChangedEventArgs e)
    {
        _host?.FitToHost();
    }

    // Forward F5/F6/F7 from WPF keyboard to Snes9x
    private void UserControl_KeyDown(object sender, KeyEventArgs e)
    {
        if (_host == null) return;
        switch (e.Key)
        {
            case Key.F5: _host.ForwardKey(Key.F5, true); e.Handled = true; break;
            case Key.F6: _host.ForwardKey(Key.F6, true); e.Handled = true; break;
            case Key.F7: _host.ForwardKey(Key.F7, true); e.Handled = true; break;
        }
    }
}
