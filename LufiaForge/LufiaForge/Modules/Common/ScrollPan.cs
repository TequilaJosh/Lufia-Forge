using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using Point = System.Windows.Point;
using Cursor = System.Windows.Input.Cursor;
using Cursors = System.Windows.Input.Cursors;
using MouseEventArgs = System.Windows.Input.MouseEventArgs;

namespace LufiaForge.Modules.Common;

/// <summary>
/// Hold the middle mouse button and drag to move around a big view (maps): set
/// <c>common:ScrollPan.MiddleDrag="True"</c> on its ScrollViewer. The view follows the mouse like a hand pulling
/// the map; the left and right buttons keep doing what the view does with them.
/// </summary>
public static class ScrollPan
{
    public static readonly DependencyProperty MiddleDragProperty = DependencyProperty.RegisterAttached(
        "MiddleDrag", typeof(bool), typeof(ScrollPan), new PropertyMetadata(false, OnChanged));

    public static bool GetMiddleDrag(DependencyObject o) => (bool)o.GetValue(MiddleDragProperty);
    public static void SetMiddleDrag(DependencyObject o, bool v) => o.SetValue(MiddleDragProperty, v);

    private sealed class State { public Point Start; public double H, V; public Cursor? Cursor; }
    private static readonly DependencyProperty StateProperty = DependencyProperty.RegisterAttached("PanState", typeof(State), typeof(ScrollPan));

    private static void OnChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is not ScrollViewer sv) return;
        if ((bool)e.NewValue)
        {
            sv.PreviewMouseDown += Down; sv.PreviewMouseMove += Move; sv.PreviewMouseUp += Up; sv.LostMouseCapture += Lost;
        }
        else
        {
            sv.PreviewMouseDown -= Down; sv.PreviewMouseMove -= Move; sv.PreviewMouseUp -= Up; sv.LostMouseCapture -= Lost;
        }
    }

    private static void Down(object sender, MouseButtonEventArgs e)
    {
        if (e.ChangedButton != MouseButton.Middle || sender is not ScrollViewer sv) return;
        sv.SetValue(StateProperty, new State { Start = e.GetPosition(sv), H = sv.HorizontalOffset, V = sv.VerticalOffset, Cursor = sv.Cursor });
        sv.Cursor = Cursors.SizeAll;
        sv.CaptureMouse();
        e.Handled = true;
    }

    private static void Move(object sender, MouseEventArgs e)
    {
        if (sender is not ScrollViewer sv || sv.GetValue(StateProperty) is not State s) return;
        if (e.MiddleButton != MouseButtonState.Pressed) { Stop(sv); return; }
        var p = e.GetPosition(sv);
        sv.ScrollToHorizontalOffset(s.H - (p.X - s.Start.X));
        sv.ScrollToVerticalOffset(s.V - (p.Y - s.Start.Y));
        e.Handled = true;
    }

    private static void Up(object sender, MouseButtonEventArgs e)
    {
        if (e.ChangedButton != MouseButton.Middle || sender is not ScrollViewer sv) return;
        Stop(sv);
        e.Handled = true;
    }

    private static void Lost(object sender, MouseEventArgs e)
    {
        if (sender is ScrollViewer sv) Stop(sv, release: false);
    }

    private static void Stop(ScrollViewer sv, bool release = true)
    {
        if (sv.GetValue(StateProperty) is not State s) return;
        sv.ClearValue(StateProperty);
        sv.Cursor = s.Cursor;
        if (release && sv.IsMouseCaptured) sv.ReleaseMouseCapture();
    }
}
