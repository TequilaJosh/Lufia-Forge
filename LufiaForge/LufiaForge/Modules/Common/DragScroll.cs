using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using DragEventArgs = System.Windows.DragEventArgs;
using Point = System.Windows.Point;

namespace LufiaForge.Modules.Common;

/// <summary>
/// While something is dragged over a list (a step being moved), holding it near the top or bottom of the list scrolls
/// it slowly, faster the closer to the edge: <c>common:DragScroll.Edges="True"</c> on the ScrollViewer. It keeps
/// scrolling while the mouse is held still there (the cursor is read every frame, as the drag doesn't report a mouse
/// that doesn't move) and stops when the mouse leaves the edge or the button is let go.
/// </summary>
public static class DragScroll
{
    public static readonly DependencyProperty EdgesProperty = DependencyProperty.RegisterAttached(
        "Edges", typeof(bool), typeof(DragScroll), new PropertyMetadata(false, OnChanged));

    public static bool GetEdges(DependencyObject o) => (bool)o.GetValue(EdgesProperty);
    public static void SetEdges(DependencyObject o, bool v) => o.SetValue(EdgesProperty, v);

    /// <summary>The edge band, and the speed at its inner side and at the edge, in pixels a frame.</summary>
    private const double Zone = 56, Slow = 1, Fast = 8;

    private static ScrollViewer? _active;
    private static DispatcherTimer? _timer;

    private static void OnChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is not ScrollViewer sv) return;
        sv.PreviewDragOver -= OnDragOver;
        if ((bool)e.NewValue) sv.PreviewDragOver += OnDragOver;
    }

    private static void OnDragOver(object sender, DragEventArgs e)
    {
        if (sender is not ScrollViewer sv || Speed(sv) == 0) return;
        _active = sv;
        if (_timer != null) return;
        _timer = new DispatcherTimer(DispatcherPriority.Normal) { Interval = TimeSpan.FromMilliseconds(16) };
        _timer.Tick += Tick;
        _timer.Start();
    }

    private static void Tick(object? sender, EventArgs e)
    {
        var sv = _active;
        double speed = sv != null && (GetAsyncKeyState(0x01) & 0x8000) != 0 && sv.IsVisible ? Speed(sv) : 0;
        if (speed == 0) { Stop(); return; }
        sv!.ScrollToVerticalOffset(Math.Clamp(sv.VerticalOffset + speed, 0, sv.ScrollableHeight));
    }

    private static void Stop()
    {
        _timer?.Stop();
        _timer = null;
        _active = null;
    }

    /// <summary>How fast to scroll for where the cursor is now: negative up, positive down, 0 away from the edges
    /// (or outside the list).</summary>
    private static double Speed(ScrollViewer sv)
    {
        if (!GetCursorPos(out var c) || PresentationSource.FromVisual(sv) == null) return 0;
        Point p;
        try { p = sv.PointFromScreen(new Point(c.X, c.Y)); } catch (InvalidOperationException) { return 0; }
        double h = sv.ActualHeight;
        if (p.X < 0 || p.X > sv.ActualWidth || p.Y < -Zone || p.Y > h + Zone || h < Zone * 3) return 0;
        double zone = Zone;
        if (p.Y < zone && sv.VerticalOffset > 0) return -(Slow + (Fast - Slow) * Math.Min(1, (zone - p.Y) / zone));
        if (p.Y > h - zone && sv.VerticalOffset < sv.ScrollableHeight) return Slow + (Fast - Slow) * Math.Min(1, (p.Y - (h - zone)) / zone);
        return 0;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct CursorPoint { public int X, Y; }

    [DllImport("user32.dll")] private static extern bool GetCursorPos(out CursorPoint p);
    [DllImport("user32.dll")] private static extern short GetAsyncKeyState(int key);
}
