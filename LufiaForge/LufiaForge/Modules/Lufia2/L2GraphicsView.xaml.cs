using System.Windows.Controls;
using System.Windows.Input;
using KeyEventArgs = System.Windows.Input.KeyEventArgs;

namespace LufiaForge.Modules.Lufia2;

public partial class L2GraphicsView : UserControl
{
    public L2GraphicsView()
    {
        InitializeComponent();
        // the canvas gives pixel positions; the page paints (also while the picture is popped out)
        Canvas.PixelPressed += (x, y, right) => Vm?.PaintPress(x, y, right);
        Canvas.PixelDragged += (x, y) => Vm?.PaintDrag(x, y);
        Canvas.PixelReleased += () => Vm?.PaintRelease();
        Canvas.PixelHover += (x, y) => Vm?.PaintHover(x, y);
    }

    private L2GraphicsPage? Vm => Canvas.DataContext as L2GraphicsPage;

    /// <summary>Ctrl+Z / Ctrl+Y take painting strokes back while there are some; else the keys go on to the app's Undo.</summary>
    private void Picture_KeyDown(object sender, KeyEventArgs e)
    {
        if (Vm == null || Keyboard.FocusedElement is System.Windows.Controls.Primitives.TextBoxBase || !Keyboard.Modifiers.HasFlag(ModifierKeys.Control)) return;
        bool shift = Keyboard.Modifiers.HasFlag(ModifierKeys.Shift);
        if (e.Key == Key.Z && !shift) e.Handled = Vm.PaintKey(redo: false);
        else if (e.Key == Key.Y || e.Key == Key.Z && shift) e.Handled = Vm.PaintKey(redo: true);
    }
}
