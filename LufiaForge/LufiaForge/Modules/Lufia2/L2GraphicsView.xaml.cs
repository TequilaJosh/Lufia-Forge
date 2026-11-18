using System.Windows.Controls;
using System.Windows.Input;
using KeyEventArgs = System.Windows.Input.KeyEventArgs;

namespace LufiaForge.Modules.Lufia2;

public partial class L2GraphicsView : UserControl
{
    public L2GraphicsView()
    {
        InitializeComponent();
        // the canvas hands us pixel positions and the page does the painting (also while the picture is popped out)
        Canvas.PixelPressed += (x, y, right) => Vm?.PaintPress(x, y, right);
        Canvas.PixelDragged += (x, y) => Vm?.PaintDrag(x, y);
        Canvas.PixelReleased += () => Vm?.PaintRelease();
        Canvas.PixelHover += (x, y) => Vm?.PaintHover(x, y);
        Canvas.ZoomWheel += delta => (delta > 0 ? Vm?.ZoomInCommand : Vm?.ZoomOutCommand)?.Execute(null);
    }

    private L2GraphicsPage? Vm => Canvas.DataContext as L2GraphicsPage;

    // keeps the picked picture in sight (a new sprite or palette picks one far down the list)
    private void List_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (sender is not System.Windows.Controls.ListBox list || list.SelectedItem == null) return;
        // wait until the list has laid out its new items (a refresh replaces them all)
        Dispatcher.BeginInvoke(() => { if (list.SelectedItem != null) list.ScrollIntoView(list.SelectedItem); }, System.Windows.Threading.DispatcherPriority.Background);
    }

    // Ctrl+Z / Ctrl+Y take painting strokes back while there are some, otherwise the keys go on to the app's Undo
    private void Picture_KeyDown(object sender, KeyEventArgs e)
    {
        if (Vm == null || Keyboard.FocusedElement is System.Windows.Controls.Primitives.TextBoxBase || !Keyboard.Modifiers.HasFlag(ModifierKeys.Control)) return;
        bool shift = Keyboard.Modifiers.HasFlag(ModifierKeys.Shift);
        if (e.Key == Key.Z && !shift) e.Handled = Vm.PaintKey(redo: false);
        else if (e.Key == Key.Y || e.Key == Key.Z && shift) e.Handled = Vm.PaintKey(redo: true);
    }
}
