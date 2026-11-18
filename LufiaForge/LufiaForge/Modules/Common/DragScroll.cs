using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using DragEventArgs = System.Windows.DragEventArgs;
using Point = System.Windows.Point;

namespace LufiaForge.Modules.Common;

// when you drag something over a list (moving a step), holding it near the top or bottom scrolls the list, slowly
// at first and faster the closer you get to the edge. turn it on with common:DragScroll.Edges="True" on the
// ScrollViewer. it keeps scrolling while the mouse sits still there (we read the cursor every frame, the drag doesn't
// report a mouse that isn't moving) and stops when the mouse leaves the edge or the button is let go
public static class DragScroll
{
    public static readonly DependencyProperty EdgesProperty = DependencyProperty.RegisterAttached(
        "Edges", typeof(bool), typeof(DragScroll), new PropertyMetadata(false, OnChanged));

    public static bool GetEdges(DependencyObject o) => (bool)o.GetValue(EdgesProperty);
    public static void SetEdges(DependencyObject o, bool v) => o.SetValue(EdgesProperty, v);

    // how tall the edge band is, and the speed at its inner side and right at the edge, in pixels a frame
    private const double Zone = 56, Slow = 1, Fast = 8;

    private static ScrollViewer? _active;
    private static DispatcherTimer? _timer;

    private static void OnChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is not ScrollViewer scroller) return;
        scroller.PreviewDragOver -= OnDragOver;
        if ((bool)e.NewValue) scroller.PreviewDragOver += OnDragOver;
    }

    private static void OnDragOver(object sender, DragEventArgs e)
    {
        if (sender is not ScrollViewer scroller || Speed(scroller) == 0) return;
        _active = scroller;
        if (_timer != null) return;
        _timer = new DispatcherTimer(DispatcherPriority.Normal) { Interval = TimeSpan.FromMilliseconds(16) };
        _timer.Tick += Tick;
        _timer.Start();
    }

    private static void Tick(object? sender, EventArgs e)
    {
        var scroller = _active;
        double speed = scroller != null && (GetAsyncKeyState(0x01) & 0x8000) != 0 && scroller.IsVisible ? Speed(scroller) : 0;
        if (speed == 0) { Stop(); return; }
        scroller!.ScrollToVerticalOffset(Math.Clamp(scroller.VerticalOffset + speed, 0, scroller.ScrollableHeight));
    }

    private static void Stop()
    {
        _timer?.Stop();
        _timer = null;
        _active = null;
    }

    // how fast to scroll for where the cursor is right now. negative = up, positive = down, 0 away from the edges
    // (or outside the list)
    private static double Speed(ScrollViewer scroller)
    {
        if (!GetCursorPos(out var cursor) || PresentationSource.FromVisual(scroller) == null) return 0;
        Point p;
        try { p = scroller.PointFromScreen(new Point(cursor.X, cursor.Y)); } catch (InvalidOperationException) { return 0; }
        double height = scroller.ActualHeight;
        if (p.X < 0 || p.X > scroller.ActualWidth || p.Y < -Zone || p.Y > height + Zone || height < Zone * 3) return 0;
        double zone = Zone;
        if (p.Y < zone && scroller.VerticalOffset > 0) return -(Slow + (Fast - Slow) * Math.Min(1, (zone - p.Y) / zone));
        if (p.Y > height - zone && scroller.VerticalOffset < scroller.ScrollableHeight) return Slow + (Fast - Slow) * Math.Min(1, (p.Y - (height - zone)) / zone);
        return 0;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct CursorPoint { public int X, Y; }

    [DllImport("user32.dll")] private static extern bool GetCursorPos(out CursorPoint p);
    [DllImport("user32.dll")] private static extern short GetAsyncKeyState(int key);
}
