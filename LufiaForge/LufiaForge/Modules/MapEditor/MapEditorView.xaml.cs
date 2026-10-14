using System.ComponentModel;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using MouseEventArgs = System.Windows.Input.MouseEventArgs;
using ToolTip = System.Windows.Controls.ToolTip;

namespace LufiaForge.Modules.MapEditor;

public partial class MapEditorView : UserControl
{
    // Floating tooltip that follows the mouse and describes what's under it (treasure, exits, arrivals).
    private readonly ToolTip _tip = new() { Placement = PlacementMode.Mouse, HorizontalOffset = 14, VerticalOffset = 14 };
    private MapEditorViewModel? _vmHooked;
    private bool _altWheel;

    public MapEditorView()
    {
        InitializeComponent();
        // Ctrl + wheel zooms around the mouse, middle button drags the map
        LufiaForge.Modules.Common.MapNavigation.Attach(MapScroll, () => Vm?.Zoom ?? 1, z => { if (Vm != null) Vm.Zoom = z; }, 0.25, 6);
        LufiaForge.Modules.Common.MapNavigation.Attach(PaletteScroll, () => Vm?.PaletteZoom ?? 2, z => { if (Vm != null) Vm.PaletteZoom = z; }, 1, 4);
        _tip.PlacementTarget = MapSurface;
        // the block palette (16 blocks per row) fills the card's width; the size slider still changes it
        PaletteScroll.SizeChanged += (_, e) =>
        {
            if (Vm == null || !e.WidthChanged) return;
            double fit = Math.Floor((PaletteScroll.ActualWidth - 20) / 256 * 4) / 4;
            if (fit >= 1) Vm.PaletteZoom = Math.Min(4, fit);
        };
        DataContextChanged += (_, _) => HookVm();
        // Alt + mouse wheel anywhere in the editor turns the brush block; letting go of Alt then doesn't open the menu bar
        PreviewMouseWheel += (_, e) =>
        {
            if (Vm == null || (Keyboard.Modifiers & ModifierKeys.Alt) == 0) return;
            if (Vm.TurnBrush(e.Delta > 0 ? 1 : -1)) { e.Handled = true; _altWheel = true; }
        };
        PreviewKeyUp += (_, e) =>
        {
            if (_altWheel && e.Key == Key.System && e.SystemKey is Key.LeftAlt or Key.RightAlt) { e.Handled = true; _altWheel = false; }
        };
        // Ctrl+Z / Ctrl+Y anywhere in the editor (text boxes keep their own): the map's edits first; with none left
        // the key goes on to the app's history (Edit > Undo)
        KeyDown += (_, e) =>
        {
            if (e.Handled || Vm == null || Keyboard.FocusedElement is System.Windows.Controls.Primitives.TextBoxBase) return;
            if (Keyboard.Modifiers == ModifierKeys.None) { if (Vm.ToolKey(e.Key)) e.Handled = true; return; }
            if ((Keyboard.Modifiers & ModifierKeys.Control) == 0) return;
            bool shift = (Keyboard.Modifiers & ModifierKeys.Shift) != 0;
            if (e.Key == Key.Z && !shift) { if (Vm.UndoCommand.CanExecute(null)) { Vm.UndoCommand.Execute(null); e.Handled = true; } }
            else if (e.Key == Key.Y || e.Key == Key.Z) { if (Vm.RedoCommand.CanExecute(null)) { Vm.RedoCommand.Execute(null); e.Handled = true; } }
        };
        HookVm();
    }

    private MapEditorViewModel? Vm => DataContext as MapEditorViewModel;

    private void HookVm()
    {
        if (_vmHooked != null)
        {
            _vmHooked.PropertyChanged -= Vm_PropertyChanged;
            _vmHooked.FocusRequested -= Vm_FocusRequested;
            _vmHooked.EventEditorRequested -= Vm_EventEditorRequested;
            _vmHooked.DestinationPickerRequested -= Vm_DestinationPickerRequested;
        }
        _vmHooked = Vm;
        if (_vmHooked != null)
        {
            _vmHooked.PropertyChanged += Vm_PropertyChanged;
            _vmHooked.FocusRequested += Vm_FocusRequested;
            _vmHooked.EventEditorRequested += Vm_EventEditorRequested;
            _vmHooked.DestinationPickerRequested += Vm_DestinationPickerRequested;
        }
    }

    /// <summary>Scroll so the given map pixel is centred (after a map switch, once the new image is laid out).</summary>
    private void Vm_FocusRequested(double x, double y)
    {
        Dispatcher.BeginInvoke(() =>
        {
            MapScroll.UpdateLayout();
            double zoom = Vm?.Zoom ?? 1;
            MapScroll.ScrollToHorizontalOffset(Math.Max(0, x * zoom - MapScroll.ViewportWidth / 2));
            MapScroll.ScrollToVerticalOffset(Math.Max(0, y * zoom - MapScroll.ViewportHeight / 2));
        }, System.Windows.Threading.DispatcherPriority.Loaded);
    }

    private void Vm_EventEditorRequested(LufiaForge.Core.Maps.EventScript script, string title)
    {
        if (Vm?.Rom == null) return;
        _tip.IsOpen = false;
        var win = new EventEditorWindow(Vm.Rom, script, title, Vm) { Owner = System.Windows.Window.GetWindow(this) };
        win.ShowDialog();
    }

    private void Vm_PropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName != nameof(MapEditorViewModel.HoverTip) || Vm == null) return;
        string text = Vm.HoverTip;
        // close and reopen so the tip moves to the current mouse position
        _tip.IsOpen = false;
        if (string.IsNullOrEmpty(text)) return;
        _tip.Content = text;
        _tip.IsOpen = true;
    }

    private void Vm_DestinationPickerRequested(LufiaForge.Core.Maps.MapExit exit)
    {
        if (Vm?.Rom == null || Vm.SelectedMap == null) return;
        _tip.IsOpen = false;
        var win = new DestinationPickerWindow(Vm.Rom, Vm, exit, Vm.SelectedMap.MapId) { Owner = System.Windows.Window.GetWindow(this) };
        if (win.ShowDialog() == true && win.ResultMap >= 0)
            Vm.SetExitDestination(exit, win.ResultMap, win.ResultArrival, win.ResultNewArrival);
    }

    private void MapSurface_KeyDown(object sender, System.Windows.Input.KeyEventArgs e)
    {
        if (Vm == null) return;
        bool ctrl = (Keyboard.Modifiers & ModifierKeys.Control) != 0;
        if (e.Key == Key.Escape) { Vm.CancelStamp(); Vm.ClearAreaSelection(); e.Handled = true; }
        else if (ctrl && e.Key == Key.C) { Vm.CopyAreaCommand.Execute(null); e.Handled = true; }
        else if (ctrl && e.Key == Key.X) { Vm.CutAreaCommand.Execute(null); e.Handled = true; }
        else if (ctrl && e.Key == Key.V) { Vm.PasteCommand.Execute(null); e.Handled = true; }
        else if (e.Key == Key.Delete)
        {
            if (Vm.IsAreaTool && Vm.HasArea) Vm.ClearAreaCommand.Execute(null);
            else Vm.DeleteSelectedCommand.Execute(null);
            e.Handled = true;
        }
    }

    /// <summary>Zoom so the whole map fits the view.</summary>
    private void Fit_Click(object sender, System.Windows.RoutedEventArgs e)
    {
        if (Vm?.MapImage is not { } img || img.PixelWidth == 0) return;
        double z = Math.Min((MapScroll.ViewportWidth - 4) / img.PixelWidth, (MapScroll.ViewportHeight - 4) / img.PixelHeight);
        Vm.Zoom = Math.Clamp(z, 0.25, 6);
        MapScroll.ScrollToHome();
    }

    private void MapSurface_MouseRightButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (Vm?.IsStamping == true) { Vm.CancelStamp(); e.Handled = true; }
    }

    private void PaletteImage_MouseMove(object sender, MouseEventArgs e)
    {
        if (e.LeftButton == MouseButtonState.Pressed) { var p = e.GetPosition(PaletteImage); Vm?.PaletteDrag(p.X, p.Y); }
    }

    private void PaletteImage_MouseLeftButtonUp(object sender, MouseButtonEventArgs e)
    {
        PaletteImage.ReleaseMouseCapture();
        Vm?.PaletteUp();
    }

    private void MapSurface_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        MapSurface.Focus();
        var p = e.GetPosition(MapSurface);
        if (e.ClickCount == 2) { Vm?.OnDoubleClick(p.X, p.Y); return; }
        MapSurface.CaptureMouse();
        Vm?.OnMouseDown(p.X, p.Y, (Keyboard.Modifiers & ModifierKeys.Control) != 0);
    }

    private void MapSurface_MouseMove(object sender, MouseEventArgs e)
    {
        // eyedropper cursor while Ctrl is held
        MapSurface.Cursor = (Keyboard.Modifiers & ModifierKeys.Control) != 0 ? System.Windows.Input.Cursors.Cross : null;
        var p = e.GetPosition(MapSurface);
        Vm?.OnMouseMove(p.X, p.Y, e.LeftButton == MouseButtonState.Pressed);
    }

    private void MapSurface_MouseLeftButtonUp(object sender, MouseButtonEventArgs e)
    {
        MapSurface.ReleaseMouseCapture();
        Vm?.OnMouseUp();
    }

    private void MapSurface_MouseLeave(object sender, MouseEventArgs e) => Vm?.OnMouseLeave();

    private void PaletteImage_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        var p = e.GetPosition(PaletteImage);
        PaletteImage.CaptureMouse();
        Vm?.PaletteDown(p.X, p.Y);
    }
}
