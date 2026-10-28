using System.Windows.Controls;
using System.Windows.Input;

namespace LufiaForge.Modules.Lufia2;

public partial class L2CutscenesView : UserControl
{
    public L2CutscenesView()
    {
        InitializeComponent();
        DataContextChanged += (_, e) =>
        {
            if (e.OldValue is L2CutscenesPage old) old.ScrollRequested -= ScrollTo;
            if (e.NewValue is L2CutscenesPage vm) vm.ScrollRequested += ScrollTo;
        };
    }

    private void Stage_MouseDown(object sender, MouseButtonEventArgs e)
    {
        if (DataContext is not L2CutscenesPage vm) return;
        var p = e.GetPosition(Stage);
        vm.StageClick(p.X, p.Y);
    }

    /// <summary>Keeps the camera's view (a point of the map) in sight.</summary>
    private void ScrollTo(double x, double y)
    {
        if (DataContext is not L2CutscenesPage vm) return;
        double z = vm.Zoom;
        double vx = x * z - StageScroll.ViewportWidth / 2, vy = y * z - StageScroll.ViewportHeight / 2;
        double left = StageScroll.HorizontalOffset, top = StageScroll.VerticalOffset;
        // only when the point is out of the middle part of the view
        if (x * z < left + StageScroll.ViewportWidth * 0.2 || x * z > left + StageScroll.ViewportWidth * 0.8) StageScroll.ScrollToHorizontalOffset(Math.Max(0, vx));
        if (y * z < top + StageScroll.ViewportHeight * 0.2 || y * z > top + StageScroll.ViewportHeight * 0.8) StageScroll.ScrollToVerticalOffset(Math.Max(0, vy));
    }
}
