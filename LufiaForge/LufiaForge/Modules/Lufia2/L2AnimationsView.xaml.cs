using System.Windows;
using System.Windows.Controls;

namespace LufiaForge.Modules.Lufia2;

public partial class L2AnimationsView : UserControl
{
    private L2AnimationsPage? Vm => DataContext as L2AnimationsPage;

    public L2AnimationsView()
    {
        InitializeComponent();
        Canvas.PixelPressed += (x, y, right) => Vm?.PaintPress(x, y, right);
        Canvas.PixelDragged += (x, y) => Vm?.PaintDrag(x, y);
        Canvas.PixelReleased += () => Vm?.PaintRelease();
    }

    private void Swatch_Click(object sender, System.Windows.Input.MouseButtonEventArgs e)
    {
        if (sender is FrameworkElement { Tag: int i }) Vm?.PickColour(i);
    }
}
