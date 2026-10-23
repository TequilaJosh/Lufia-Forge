using System.Windows.Controls;
using System.Windows.Input;

namespace LufiaForge.Modules.Lufia2;

public partial class L2MapsView : UserControl
{
    public L2MapsView() => InitializeComponent();

    private L2MapsPage? Vm => DataContext as L2MapsPage;

    private void Map_MouseDown(object sender, MouseButtonEventArgs e)
    {
        var p = e.GetPosition(MapImageControl);
        Vm?.PaintAt(p.X, p.Y, e.ChangedButton == MouseButton.Right);
    }

    private void Map_MouseMove(object sender, System.Windows.Input.MouseEventArgs e)
    {
        if (e.LeftButton != MouseButtonState.Pressed) return;
        var p = e.GetPosition(MapImageControl);
        Vm?.PaintAt(p.X, p.Y, false);
    }

    private void Picker_MouseDown(object sender, MouseButtonEventArgs e)
    {
        var p = e.GetPosition(PickerControl);
        Vm?.PickFromPicker(p.X, p.Y);
    }
}
