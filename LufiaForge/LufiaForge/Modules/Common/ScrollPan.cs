using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using Point = System.Windows.Point;
using Cursor = System.Windows.Input.Cursor;
using Cursors = System.Windows.Input.Cursors;
using MouseEventArgs = System.Windows.Input.MouseEventArgs;

namespace LufiaForge.Modules.Common;

// hold the middle mouse button and drag to move around a big view (maps). set common:ScrollPan.MiddleDrag="True" on
// its ScrollViewer. the view follows the mouse like a hand pulling the map, and the left and right buttons still do
// whatever the view does with them
public static class ScrollPan
{
    public static readonly DependencyProperty MiddleDragProperty = DependencyProperty.RegisterAttached(
        "MiddleDrag", typeof(bool), typeof(ScrollPan), new PropertyMetadata(false, OnChanged));

    public static bool GetMiddleDrag(DependencyObject o) => (bool)o.GetValue(MiddleDragProperty);
    public static void SetMiddleDrag(DependencyObject o, bool v) => o.SetValue(MiddleDragProperty, v);

    private sealed class State { public Point Start; public double Horizontal, Vertical; public Cursor? Cursor; }
    private static readonly DependencyProperty StateProperty = DependencyProperty.RegisterAttached("PanState", typeof(State), typeof(ScrollPan));

    private static void OnChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is not ScrollViewer scroller) return;
        if ((bool)e.NewValue)
        {
            scroller.PreviewMouseDown += Down; scroller.PreviewMouseMove += Move; scroller.PreviewMouseUp += Up; scroller.LostMouseCapture += Lost;
        }
        else
        {
            scroller.PreviewMouseDown -= Down; scroller.PreviewMouseMove -= Move; scroller.PreviewMouseUp -= Up; scroller.LostMouseCapture -= Lost;
        }
    }

    private static void Down(object sender, MouseButtonEventArgs e)
    {
        if (e.ChangedButton != MouseButton.Middle || sender is not ScrollViewer scroller) return;
        scroller.SetValue(StateProperty, new State { Start = e.GetPosition(scroller), Horizontal = scroller.HorizontalOffset, Vertical = scroller.VerticalOffset, Cursor = scroller.Cursor });
        scroller.Cursor = Cursors.SizeAll;
        scroller.CaptureMouse();
        e.Handled = true;
    }

    private static void Move(object sender, MouseEventArgs e)
    {
        if (sender is not ScrollViewer scroller || scroller.GetValue(StateProperty) is not State state) return;
        if (e.MiddleButton != MouseButtonState.Pressed) { Stop(scroller); return; }
        var mouse = e.GetPosition(scroller);
        scroller.ScrollToHorizontalOffset(state.Horizontal - (mouse.X - state.Start.X));
        scroller.ScrollToVerticalOffset(state.Vertical - (mouse.Y - state.Start.Y));
        e.Handled = true;
    }

    private static void Up(object sender, MouseButtonEventArgs e)
    {
        if (e.ChangedButton != MouseButton.Middle || sender is not ScrollViewer scroller) return;
        Stop(scroller);
        e.Handled = true;
    }

    private static void Lost(object sender, MouseEventArgs e)
    {
        if (sender is ScrollViewer scroller) Stop(scroller, release: false);
    }

    private static void Stop(ScrollViewer scroller, bool release = true)
    {
        if (scroller.GetValue(StateProperty) is not State state) return;
        scroller.ClearValue(StateProperty);
        scroller.Cursor = state.Cursor;
        if (release && scroller.IsMouseCaptured) scroller.ReleaseMouseCapture();
    }
}
