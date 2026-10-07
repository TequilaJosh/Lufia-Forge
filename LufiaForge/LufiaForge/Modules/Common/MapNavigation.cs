using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using Cursor = System.Windows.Input.Cursor;
using Cursors = System.Windows.Input.Cursors;
using MouseEventArgs = System.Windows.Input.MouseEventArgs;
using Point = System.Windows.Point;

namespace LufiaForge.Modules.Common;

/// <summary>
/// Mouse navigation for map views: Ctrl + wheel zooms around the mouse pointer, holding the middle button and
/// moving the mouse drags the map around. The plain wheel still scrolls as usual.
/// </summary>
public sealed class MapNavigation
{
    private readonly ScrollViewer _scroller;
    private readonly Func<double> _getZoom;
    private readonly Action<double> _setZoom;
    private readonly double _min, _max;
    private Point _dragStart;
    private double _startH, _startV;
    private bool _dragging;
    private Cursor? _oldCursor;

    private MapNavigation(ScrollViewer scroller, Func<double> getZoom, Action<double> setZoom, double min, double max)
    {
        _scroller = scroller; _getZoom = getZoom; _setZoom = setZoom; _min = min; _max = max;
        scroller.PreviewMouseWheel += OnWheel;
        scroller.PreviewMouseDown += OnDown;
        scroller.PreviewMouseMove += OnMove;
        scroller.PreviewMouseUp += OnUp;
        scroller.LostMouseCapture += (_, _) => EndDrag();
    }

    /// <param name="getZoom">Current zoom factor of the content inside the scroll viewer.</param>
    /// <param name="setZoom">Applies a new zoom factor (the content's LayoutTransform scale).</param>
    public static MapNavigation Attach(ScrollViewer scroller, Func<double> getZoom, Action<double> setZoom, double min = 0.25, double max = 6) =>
        new(scroller, getZoom, setZoom, min, max);

    private void OnWheel(object sender, MouseWheelEventArgs e)
    {
        if ((Keyboard.Modifiers & ModifierKeys.Control) == 0) return;
        e.Handled = true;
        double old = _getZoom();
        double zoom = Math.Clamp(e.Delta > 0 ? old * 1.2 : old / 1.2, _min, _max);
        if (Math.Abs(zoom - old) < 1e-6) return;

        // keep the map point under the mouse where it is
        var mouse = e.GetPosition(_scroller);
        double contentX = (_scroller.HorizontalOffset + mouse.X) / old;
        double contentY = (_scroller.VerticalOffset + mouse.Y) / old;
        _setZoom(zoom);
        _scroller.UpdateLayout();
        _scroller.ScrollToHorizontalOffset(Math.Max(0, contentX * zoom - mouse.X));
        _scroller.ScrollToVerticalOffset(Math.Max(0, contentY * zoom - mouse.Y));
    }

    private void OnDown(object sender, MouseButtonEventArgs e)
    {
        if (e.ChangedButton != MouseButton.Middle) return;
        _dragging = true;
        _dragStart = e.GetPosition(_scroller);
        _startH = _scroller.HorizontalOffset;
        _startV = _scroller.VerticalOffset;
        _oldCursor = _scroller.Cursor;
        _scroller.Cursor = Cursors.SizeAll;
        _scroller.CaptureMouse();
        e.Handled = true;
    }

    private void OnMove(object sender, MouseEventArgs e)
    {
        if (!_dragging) return;
        var p = e.GetPosition(_scroller);
        _scroller.ScrollToHorizontalOffset(_startH - (p.X - _dragStart.X));
        _scroller.ScrollToVerticalOffset(_startV - (p.Y - _dragStart.Y));
        e.Handled = true;
    }

    private void OnUp(object sender, MouseButtonEventArgs e)
    {
        if (e.ChangedButton != MouseButton.Middle || !_dragging) return;
        _scroller.ReleaseMouseCapture();
        EndDrag();
        e.Handled = true;
    }

    private void EndDrag()
    {
        if (!_dragging) return;
        _dragging = false;
        _scroller.Cursor = _oldCursor;
    }
}
