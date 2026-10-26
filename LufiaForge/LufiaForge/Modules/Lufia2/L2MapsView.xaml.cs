using System.Windows.Controls;
using System.Windows.Input;

namespace LufiaForge.Modules.Lufia2;

public partial class L2MapsView : UserControl
{
    public L2MapsView()
    {
        InitializeComponent();
        Focusable = true;
        // Ctrl+Z / Ctrl+Y take back painting not yet applied; with nothing left the app's own Undo gets the key
        PreviewKeyDown += (_, e) =>
        {
            if (Vm == null || !Keyboard.Modifiers.HasFlag(ModifierKeys.Control) || Keyboard.FocusedElement is System.Windows.Controls.Primitives.TextBoxBase) return;
            bool shift = Keyboard.Modifiers.HasFlag(ModifierKeys.Shift);
            if (e.Key == Key.Z && !shift) e.Handled = Vm.UndoPaint();
            else if (e.Key == Key.Y || e.Key == Key.Z && shift) e.Handled = Vm.RedoPaint();
        };
    }

    private L2MapsPage? Vm => DataContext as L2MapsPage;

    private void Map_MouseDown(object sender, MouseButtonEventArgs e)
    {
        Focus();   // so Ctrl+Z reaches this tab
        Vm?.EndStroke();
        var p = e.GetPosition(MapImageControl);
        Vm?.PaintAt(p.X, p.Y, e.ChangedButton == MouseButton.Right);
    }

    private void Map_MouseUp(object sender, MouseButtonEventArgs e) => Vm?.EndStroke();

    private void Map_MouseMove(object sender, System.Windows.Input.MouseEventArgs e)
    {
        if (e.LeftButton != MouseButtonState.Pressed) return;
        var p = e.GetPosition(MapImageControl);
        Vm?.PaintAt(p.X, p.Y, false);
    }

    private void Info_DoubleClick(object sender, MouseButtonEventArgs e)
    {
        if (sender is System.Windows.Controls.ListBox lb) Vm?.FollowInfo(lb.SelectedIndex);
    }

    private void Picker_MouseDown(object sender, MouseButtonEventArgs e)
    {
        var p = e.GetPosition(PickerControl);
        Vm?.PickFromPicker(p.X, p.Y);
    }
}
