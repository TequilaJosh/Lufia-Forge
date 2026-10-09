using System.Windows.Input;
using MouseEventArgs = System.Windows.Input.MouseEventArgs;

namespace LufiaForge.Modules.GameData.Views;

public partial class EncounterEditorView : UserControl
{
    public EncounterEditorView() => InitializeComponent();

    private EncounterEditorViewModel? Vm => DataContext as EncounterEditorViewModel;
    private bool _down;

    private (int X, int Y) Cell(MouseEventArgs e)
    {
        var p = e.GetPosition(WorldGrid);
        return ((int)(p.X / EncounterEditorViewModel.CellPixels), (int)(p.Y / EncounterEditorViewModel.CellPixels));
    }

    private void World_MouseDown(object sender, MouseButtonEventArgs e)
    {
        if (Vm == null) return;
        _down = true;
        WorldGrid.CaptureMouse();
        var (x, y) = Cell(e);
        Vm.UseTool(x, y, dragging: false);
    }

    private void World_MouseMove(object sender, MouseEventArgs e)
    {
        if (Vm == null) return;
        var (x, y) = Cell(e);
        Vm.HoverCell(x, y);
        if (_down && e.LeftButton == MouseButtonState.Pressed) Vm.UseTool(x, y, dragging: true);
    }

    private void World_MouseUp(object sender, MouseButtonEventArgs e)
    {
        _down = false;
        WorldGrid.ReleaseMouseCapture();
    }

    private void World_MouseLeave(object sender, MouseEventArgs e)
    {
        if (!_down) Vm?.HoverCell(-1, -1);
    }
}
