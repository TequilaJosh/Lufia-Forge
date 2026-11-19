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

// a picture drawn pixel by pixel at a whole-number zoom, with an optional grid (every pixel when it's big enough,
// every 8x8 square, and every CellWidth x CellHeight cell, like a sprite frame).
// mouse presses and drags come out as pixel positions (a fast drag gives every pixel on the way)
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

    // a button went down on pixel (x, y), right = the right button
    public event Action<int, int, bool>? PixelPressed;
    // the mouse moved onto pixel (x, y) with the button held
    public event Action<int, int>? PixelDragged;
    public event Action? PixelReleased;
    // the mouse is over pixel (x, y), or (-1, -1) once it left
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

    private static Pen Frozen(Color color, double width) { var pen = new Pen(new SolidColorBrush(color), width); pen.Freeze(); return pen; }

    private static Brush MakeChecker()
    {
        var drawing = new DrawingGroup();
        drawing.Children.Add(new GeometryDrawing(new SolidColorBrush(Color.FromRgb(0x2A, 0x2A, 0x2A)), null, new RectangleGeometry(new Rect(0, 0, 16, 16))));
        drawing.Children.Add(new GeometryDrawing(new SolidColorBrush(Color.FromRgb(0x38, 0x38, 0x38)), null, new RectangleGeometry(new Rect(0, 0, 8, 8))));
        drawing.Children.Add(new GeometryDrawing(new SolidColorBrush(Color.FromRgb(0x38, 0x38, 0x38)), null, new RectangleGeometry(new Rect(8, 8, 8, 8))));
        var brush = new DrawingBrush(drawing) { TileMode = TileMode.Tile, Viewport = new Rect(0, 0, 16, 16), ViewportUnits = BrushMappingMode.Absolute };
        brush.Freeze();
        return brush;
    }

    private int ZoomLevel => Math.Max(1, Zoom);

    protected override Size MeasureOverride(Size availableSize) =>
        Source is { } source ? new Size(source.PixelWidth * ZoomLevel + 1, source.PixelHeight * ZoomLevel + 1) : new Size(0, 0);

    protected override void OnRender(DrawingContext dc)
    {
        if (Source is not { } source) return;
        int w = source.PixelWidth, h = source.PixelHeight, zoom = ZoomLevel;
        var area = new Rect(0, 0, w * zoom, h * zoom);
        dc.DrawRectangle(Checker, null, area);
        dc.DrawImage(source, area);
        if (!ShowGrid) return;
        // lines on the half pixel so a 1-pixel pen is sharp
        void Lines(int step, Pen pen)
        {
            for (int x = step; x < w; x += step) dc.DrawLine(pen, new Point(x * zoom + 0.5, 0), new Point(x * zoom + 0.5, h * zoom));
            for (int y = step; y < h; y += step) dc.DrawLine(pen, new Point(0, y * zoom + 0.5), new Point(w * zoom, y * zoom + 0.5));
        }
        if (zoom >= 6) Lines(1, PixelPen);
        if (zoom >= 2) Lines(8, TilePen);
        int cellW = Math.Max(1, CellWidth), cellH = Math.Max(1, CellHeight);
        for (int x = cellW; x < w; x += cellW) dc.DrawLine(CellPen, new Point(x * zoom + 0.5, 0), new Point(x * zoom + 0.5, h * zoom));
        for (int y = cellH; y < h; y += cellH) dc.DrawLine(CellPen, new Point(0, y * zoom + 0.5), new Point(w * zoom, y * zoom + 0.5));
        dc.DrawRectangle(null, TilePen, new Rect(0.5, 0.5, w * zoom, h * zoom));
    }

    private (int X, int Y) PixelAt(MouseEventArgs e)
    {
        var mousePos = e.GetPosition(this);
        return ((int)Math.Floor(mousePos.X / ZoomLevel), (int)Math.Floor(mousePos.Y / ZoomLevel));
    }

    private bool Inside(int x, int y) => Source is { } source && x >= 0 && y >= 0 && x < source.PixelWidth && y < source.PixelHeight;

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

    // Ctrl + mouse wheel: +1 to zoom in, -1 to zoom out
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
