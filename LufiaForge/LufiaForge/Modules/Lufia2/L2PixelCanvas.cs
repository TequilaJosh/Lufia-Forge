using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Brush = System.Windows.Media.Brush;
using Color = System.Windows.Media.Color;
using MouseEventArgs = System.Windows.Input.MouseEventArgs;
using Size = System.Windows.Size;
using Pen = System.Windows.Media.Pen;
using Point = System.Windows.Point;

namespace LufiaForge.Modules.Lufia2;

/// <summary>A picture drawn pixel by pixel at a whole-number zoom, with an optional grid (every pixel when big
/// enough, every 8x8 square, and every <see cref="CellWidth"/> x <see cref="CellHeight"/> cell, e.g. a sprite frame).
/// Mouse presses and drags come out as pixel positions (a fast drag gives every pixel on the way).</summary>
public sealed class L2PixelCanvas : FrameworkElement
{
    public static readonly DependencyProperty SourceProperty = DependencyProperty.Register(nameof(Source), typeof(BitmapSource), typeof(L2PixelCanvas),
        new FrameworkPropertyMetadata(null, FrameworkPropertyMetadataOptions.AffectsMeasure | FrameworkPropertyMetadataOptions.AffectsRender));
    public static readonly DependencyProperty ZoomProperty = DependencyProperty.Register(nameof(Zoom), typeof(int), typeof(L2PixelCanvas),
        new FrameworkPropertyMetadata(4, FrameworkPropertyMetadataOptions.AffectsMeasure | FrameworkPropertyMetadataOptions.AffectsRender));
    public static readonly DependencyProperty ShowGridProperty = DependencyProperty.Register(nameof(ShowGrid), typeof(bool), typeof(L2PixelCanvas),
        new FrameworkPropertyMetadata(true, FrameworkPropertyMetadataOptions.AffectsRender));
    public static readonly DependencyProperty CellWidthProperty = DependencyProperty.Register(nameof(CellWidth), typeof(int), typeof(L2PixelCanvas),
        new FrameworkPropertyMetadata(8, FrameworkPropertyMetadataOptions.AffectsRender));
    public static readonly DependencyProperty CellHeightProperty = DependencyProperty.Register(nameof(CellHeight), typeof(int), typeof(L2PixelCanvas),
        new FrameworkPropertyMetadata(8, FrameworkPropertyMetadataOptions.AffectsRender));

    public BitmapSource? Source { get => (BitmapSource?)GetValue(SourceProperty); set => SetValue(SourceProperty, value); }
    public int Zoom { get => (int)GetValue(ZoomProperty); set => SetValue(ZoomProperty, value); }
    public bool ShowGrid { get => (bool)GetValue(ShowGridProperty); set => SetValue(ShowGridProperty, value); }
    public int CellWidth { get => (int)GetValue(CellWidthProperty); set => SetValue(CellWidthProperty, value); }
    public int CellHeight { get => (int)GetValue(CellHeightProperty); set => SetValue(CellHeightProperty, value); }

    /// <summary>A button went down on pixel (x, y); right = the right button.</summary>
    public event Action<int, int, bool>? PixelPressed;
    /// <summary>The mouse moved onto pixel (x, y) with the button held.</summary>
    public event Action<int, int>? PixelDragged;
    public event Action? PixelReleased;
    /// <summary>The mouse is over pixel (x, y), or (-1, -1) when it left.</summary>
    public event Action<int, int>? PixelHover;

    private static readonly Pen PixelPen = Frozen(Color.FromArgb(0x30, 0xFF, 0xFF, 0xFF), 1);
    private static readonly Pen TilePen = Frozen(Color.FromArgb(0x70, 0xFF, 0xFF, 0xFF), 1);
    private static readonly Pen CellPen = Frozen(Color.FromArgb(0xC0, 0xE8, 0xC0, 0x40), 1);
    private static readonly Brush Checker = MakeChecker();

    private int _lastX = -1, _lastY = -1;
    private bool _right;

    public L2PixelCanvas()
    {
        Focusable = true;
        Cursor = System.Windows.Input.Cursors.Cross;
        RenderOptions.SetBitmapScalingMode(this, BitmapScalingMode.NearestNeighbor);
        SnapsToDevicePixels = true;
    }

    private static Pen Frozen(Color c, double w) { var p = new Pen(new SolidColorBrush(c), w); p.Freeze(); return p; }

    private static Brush MakeChecker()
    {
        var g = new DrawingGroup();
        g.Children.Add(new GeometryDrawing(new SolidColorBrush(Color.FromRgb(0x2A, 0x2A, 0x2A)), null, new RectangleGeometry(new Rect(0, 0, 16, 16))));
        g.Children.Add(new GeometryDrawing(new SolidColorBrush(Color.FromRgb(0x38, 0x38, 0x38)), null, new RectangleGeometry(new Rect(0, 0, 8, 8))));
        g.Children.Add(new GeometryDrawing(new SolidColorBrush(Color.FromRgb(0x38, 0x38, 0x38)), null, new RectangleGeometry(new Rect(8, 8, 8, 8))));
        var b = new DrawingBrush(g) { TileMode = TileMode.Tile, Viewport = new Rect(0, 0, 16, 16), ViewportUnits = BrushMappingMode.Absolute };
        b.Freeze();
        return b;
    }

    private int Z => Math.Max(1, Zoom);

    protected override Size MeasureOverride(Size availableSize) =>
        Source is { } s ? new Size(s.PixelWidth * Z + 1, s.PixelHeight * Z + 1) : new Size(0, 0);

    protected override void OnRender(DrawingContext dc)
    {
        if (Source is not { } s) return;
        int w = s.PixelWidth, h = s.PixelHeight, z = Z;
        var area = new Rect(0, 0, w * z, h * z);
        dc.DrawRectangle(Checker, null, area);
        dc.DrawImage(s, area);
        if (!ShowGrid) return;
        // lines on the half pixel so a 1-pixel pen is sharp
        void Lines(int step, Pen pen)
        {
            for (int x = step; x < w; x += step) dc.DrawLine(pen, new Point(x * z + 0.5, 0), new Point(x * z + 0.5, h * z));
            for (int y = step; y < h; y += step) dc.DrawLine(pen, new Point(0, y * z + 0.5), new Point(w * z, y * z + 0.5));
        }
        if (z >= 6) Lines(1, PixelPen);
        if (z >= 2) Lines(8, TilePen);
        int cw = Math.Max(1, CellWidth), ch = Math.Max(1, CellHeight);
        for (int x = cw; x < w; x += cw) dc.DrawLine(CellPen, new Point(x * z + 0.5, 0), new Point(x * z + 0.5, h * z));
        for (int y = ch; y < h; y += ch) dc.DrawLine(CellPen, new Point(0, y * z + 0.5), new Point(w * z, y * z + 0.5));
        dc.DrawRectangle(null, TilePen, new Rect(0.5, 0.5, w * z, h * z));
    }

    private (int X, int Y) PixelAt(MouseEventArgs e)
    {
        var p = e.GetPosition(this);
        return ((int)Math.Floor(p.X / Z), (int)Math.Floor(p.Y / Z));
    }

    private bool Inside(int x, int y) => Source is { } s && x >= 0 && y >= 0 && x < s.PixelWidth && y < s.PixelHeight;

    protected override void OnMouseDown(MouseButtonEventArgs e)
    {
        base.OnMouseDown(e);
        if (e.ChangedButton is not (MouseButton.Left or MouseButton.Right)) return;
        Focus();
        var (x, y) = PixelAt(e);
        if (!Inside(x, y)) return;
        _right = e.ChangedButton == MouseButton.Right;
        CaptureMouse();
        _lastX = x; _lastY = y;
        PixelPressed?.Invoke(x, y, _right);
        e.Handled = true;
    }

    protected override void OnMouseMove(MouseEventArgs e)
    {
        base.OnMouseMove(e);
        var (x, y) = PixelAt(e);
        PixelHover?.Invoke(Inside(x, y) ? x : -1, Inside(x, y) ? y : -1);
        if (!IsMouseCaptured || _right || (x == _lastX && y == _lastY)) return;
        // every pixel from the last one to this one (Bresenham), so a quick stroke has no gaps
        int x0 = _lastX, y0 = _lastY, dx = Math.Abs(x - x0), dy = -Math.Abs(y - y0), sx = x0 < x ? 1 : -1, sy = y0 < y ? 1 : -1, err = dx + dy;
        while (x0 != x || y0 != y)
        {
            int e2 = 2 * err;
            if (e2 >= dy) { err += dy; x0 += sx; }
            if (e2 <= dx) { err += dx; y0 += sy; }
            if (Inside(x0, y0)) PixelDragged?.Invoke(x0, y0);
        }
        _lastX = x; _lastY = y;
    }

    protected override void OnMouseUp(MouseButtonEventArgs e)
    {
        base.OnMouseUp(e);
        if (!IsMouseCaptured) return;
        _lastX = _lastY = -1;
        ReleaseMouseCapture();
        PixelReleased?.Invoke();
        e.Handled = true;
    }

    protected override void OnLostMouseCapture(MouseEventArgs e)
    {
        base.OnLostMouseCapture(e);
        if (_lastX >= 0) { _lastX = _lastY = -1; PixelReleased?.Invoke(); }
    }

    /// <summary>Ctrl + the mouse wheel: +1 to zoom in, -1 to zoom out.</summary>
    public event Action<int>? ZoomWheel;

    protected override void OnMouseWheel(MouseWheelEventArgs e)
    {
        base.OnMouseWheel(e);
        if (!Keyboard.Modifiers.HasFlag(ModifierKeys.Control) || e.Delta == 0) return;
        ZoomWheel?.Invoke(Math.Sign(e.Delta));
        e.Handled = true;
    }

    protected override void OnMouseLeave(MouseEventArgs e)
    {
        base.OnMouseLeave(e);
        PixelHover?.Invoke(-1, -1);
    }
}
