using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;

namespace LufiaForge.Modules.Emulator;

public partial class EmulatorHostView : UserControl
{
    private EmulatorHost?          _host;
    private EmulatorHostViewModel? _vm;

    // Keep a reference to the parent Window so we can unhook on unload.
    private Window? _parentWindow;

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

        // Hook the parent Window's preview key events so we can forward controller
        // inputs to Snes9x even while WPF controls have focus.
        _parentWindow = Window.GetWindow(this);
        if (_parentWindow != null)
        {
            _parentWindow.PreviewKeyDown += OnWindowPreviewKeyDown;
            _parentWindow.PreviewKeyUp   += OnWindowPreviewKeyUp;
        }
    }

    private void OnUnloaded(object sender, RoutedEventArgs e)
    {
        // Unhook window-level key events
        if (_parentWindow != null)
        {
            _parentWindow.PreviewKeyDown -= OnWindowPreviewKeyDown;
            _parentWindow.PreviewKeyUp   -= OnWindowPreviewKeyUp;
            _parentWindow = null;
        }

        _host?.Shutdown();
        _vm?.Dispose();
    }

    private void EmulatorBorder_SizeChanged(object sender, SizeChangedEventArgs e)
    {
        _host?.FitToHost();
    }

    // -------------------------------------------------------------------------
    // Window-level key forwarding
    // -------------------------------------------------------------------------

    private void OnWindowPreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (_vm == null) return;

        // Always try to forward mapped keys to Snes9x — the game must receive
        // input regardless of which WPF element currently has logical focus.
        bool forwarded = _vm.HandleWindowKey(e.Key, isDown: true);

        // Only swallow the event when NO interactive WPF control has focus.
        // If a TextBox/ListView/etc. is focused we let the event pass through
        // so normal typing and list navigation still work; Snes9x gets the key
        // via PostMessage either way.
        if (forwarded && !IsInteractiveFocus())
            e.Handled = true;
    }

    private void OnWindowPreviewKeyUp(object sender, KeyEventArgs e)
    {
        if (_vm == null) return;
        bool forwarded = _vm.HandleWindowKey(e.Key, isDown: false);
        if (forwarded && !IsInteractiveFocus())
            e.Handled = true;
    }

    /// <summary>
    /// Returns true when a WPF control that uses keyboard input for its own
    /// purposes (typing, list navigation) currently holds keyboard focus.
    /// In that case we forward to Snes9x but do NOT mark the event handled,
    /// so the control also receives the key.
    /// </summary>
    private static bool IsInteractiveFocus()
    {
        return Keyboard.FocusedElement is TextBoxBase    // TextBox, PasswordBox, RichTextBox
                                       or ComboBox
                                       or ListView
                                       or ListBox
                                       or TreeView;
    }
}
