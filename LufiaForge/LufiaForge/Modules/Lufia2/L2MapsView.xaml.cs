using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using MouseEventArgs = System.Windows.Input.MouseEventArgs;
using KeyEventArgs = System.Windows.Input.KeyEventArgs;

namespace LufiaForge.Modules.Lufia2;

public partial class L2MapsView : UserControl, LufiaForge.Modules.Common.ILocalUndo
{
    public L2MapsView()
    {
        InitializeComponent();
        Focusable = true;
        // Ctrl+Z / Ctrl+Y take back the map's edits not yet written; with nothing left the app's own Undo gets the key
        PreviewKeyDown += (_, e) =>
        {
            if (Vm == null || !Keyboard.Modifiers.HasFlag(ModifierKeys.Control) || Keyboard.FocusedElement is System.Windows.Controls.Primitives.TextBoxBase) return;
            bool shift = Keyboard.Modifiers.HasFlag(ModifierKeys.Shift);
            if (e.Key == Key.Z && !shift) e.Handled = Vm.UndoPaint();
            else if (e.Key == Key.Y || e.Key == Key.Z && shift) e.Handled = Vm.RedoPaint();
        };
        DataContextChanged += (_, e) =>
        {
            if (e.OldValue is L2MapsPage old) old.JumpRequested -= Jump;
            if (e.NewValue is L2MapsPage vm) vm.JumpRequested += Jump;
        };
    }

    private L2MapsPage? Vm => DataContext as L2MapsPage;

    public bool LocalUndo() => Vm?.UndoPaint() == true;
    public bool LocalRedo() => Vm?.RedoPaint() == true;

    private void MapSurface_MouseDown(object sender, MouseButtonEventArgs e)
    {
        if (Vm == null) return;
        MapSurface.Focus();   // keys (tools, Del, Esc, Ctrl+Z) reach the map
        LufiaForge.Modules.Common.LocalUndoTarget.Set(this);
        var p = e.GetPosition(MapImageControl);
        Vm.MouseDown(p.X, p.Y, e.ChangedButton == MouseButton.Right, Keyboard.Modifiers.HasFlag(ModifierKeys.Control), e.ClickCount == 2);
        if (e.ChangedButton == MouseButton.Left) MapSurface.CaptureMouse();
        e.Handled = true;
    }

    private void MapSurface_MouseUp(object sender, MouseButtonEventArgs e)
    {
        MapSurface.ReleaseMouseCapture();
        Vm?.MouseUp();
    }

    private void MapSurface_MouseMove(object sender, MouseEventArgs e)
    {
        var p = e.GetPosition(MapImageControl);
        Vm?.MouseMove(p.X, p.Y);
    }

    private void MapSurface_MouseLeave(object sender, MouseEventArgs e) => Vm?.MouseLeave();

    private void MapSurface_KeyDown(object sender, KeyEventArgs e)
    {
        if (Vm == null) return;
        e.Handled = Vm.Key(e.Key, Keyboard.Modifiers.HasFlag(ModifierKeys.Control));
    }

    private void MapScroll_PreviewMouseWheel(object sender, MouseWheelEventArgs e)
    {
        if (Vm == null || !Keyboard.Modifiers.HasFlag(ModifierKeys.Control)) return;
        // zoom around the mouse
        var before = e.GetPosition(MapImageControl);
        var inView = e.GetPosition(MapScroll);
        Vm.WheelZoom(e.Delta);
        MapScroll.UpdateLayout();
        MapScroll.ScrollToHorizontalOffset(before.X * Vm.Zoom - inView.X);
        MapScroll.ScrollToVerticalOffset(before.Y * Vm.Zoom - inView.Y);
        e.Handled = true;
    }

    private void Fit_Click(object sender, RoutedEventArgs e) => Vm?.Fit(MapScroll.ViewportWidth - 4, MapScroll.ViewportHeight - 4);

    /// <summary>Scrolls so map pixel (x, y) is in the middle of the view.</summary>
    private void Jump(double x, double y) => Dispatcher.BeginInvoke(() =>
    {
        if (Vm == null) return;
        MapScroll.UpdateLayout();
        MapScroll.ScrollToHorizontalOffset(x * Vm.Zoom - MapScroll.ViewportWidth / 2);
        MapScroll.ScrollToVerticalOffset(y * Vm.Zoom - MapScroll.ViewportHeight / 2);
    }, System.Windows.Threading.DispatcherPriority.Loaded);

    private void Picker_MouseDown(object sender, MouseButtonEventArgs e)
    {
        var p = e.GetPosition(PickerControl);
        Vm?.PickerDown(p.X, p.Y);
        PickerControl.CaptureMouse();
    }

    private void Picker_MouseMove(object sender, MouseEventArgs e)
    {
        if (e.LeftButton != MouseButtonState.Pressed) return;
        var p = e.GetPosition(PickerControl);
        Vm?.PickerMove(p.X, p.Y);
    }

    private void Picker_MouseUp(object sender, MouseButtonEventArgs e)
    {
        PickerControl.ReleaseMouseCapture();
        Vm?.PickerUp();
    }

    private void PaletteScroll_PreviewMouseWheel(object sender, MouseWheelEventArgs e)
    {
        if (Vm == null || !Keyboard.Modifiers.HasFlag(ModifierKeys.Control)) return;
        Vm.PaletteZoom = Math.Clamp(Vm.PaletteZoom + (e.Delta > 0 ? 0.5 : -0.5), 1, 4);
        e.Handled = true;
    }
}
