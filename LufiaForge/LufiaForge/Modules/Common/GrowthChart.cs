using System.Globalization;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using Color = System.Windows.Media.Color;
using Point = System.Windows.Point;
using Size = System.Windows.Size;
using Brush = System.Windows.Media.Brush;
using Brushes = System.Windows.Media.Brushes;
using Pen = System.Windows.Media.Pen;
using Cursors = System.Windows.Input.Cursors;
using MouseEventArgs = System.Windows.Input.MouseEventArgs;

namespace LufiaForge.Modules.Common;

// one line of a GrowthChart: a value per level (index 0 = level 1)
public sealed record ChartSeries(string Name, double[] Values, Color Color, bool Highlight = false);

// a small line chart of stats (or EXP) by level. one line per series, the highlighted one thick and the rest thin.
// levels go along the bottom, the value up the side. with HandleLevels set, the highlighted line gets a handle at each
// of those levels (where a band of levels ends). drag one up or down and you see the new shape (the bands on both
// sides of the handle bend, the rest stays put). let go and HandleDropped fires with the handle and the value wanted
// there, and the page turns that into gains per level
public sealed class GrowthChart : FrameworkElement
{
    public static readonly DependencyProperty SeriesProperty = DependencyProperty.Register(nameof(Series), typeof(IReadOnlyList<ChartSeries>),
        typeof(GrowthChart), new FrameworkPropertyMetadata(null, FrameworkPropertyMetadataOptions.AffectsRender));

    public IReadOnlyList<ChartSeries>? Series
    {
        get => (IReadOnlyList<ChartSeries>?)GetValue(SeriesProperty);
        set => SetValue(SeriesProperty, value);
    }

    public static readonly DependencyProperty HandleLevelsProperty = DependencyProperty.Register(nameof(HandleLevels), typeof(IReadOnlyList<int>),
        typeof(GrowthChart), new FrameworkPropertyMetadata(null, FrameworkPropertyMetadataOptions.AffectsRender));

    // the levels (1-based) where the highlighted line can be dragged. null = can't edit it
    public IReadOnlyList<int>? HandleLevels
    {
        get => (IReadOnlyList<int>?)GetValue(HandleLevelsProperty);
        set => SetValue(HandleLevelsProperty, value);
    }

    // a handle was let go: its index in HandleLevels and the value wanted at its level
    public event Action<int, double>? HandleDropped;

    private Point? _mouse;
    private int _drag = -1, _hover = -1;
    private double _dragValue, _dragMax;

    private const double Left = 54, Right = 12, Top = 10, Bottom = 22;

    public GrowthChart()
    {
        MinHeight = 160; MinWidth = 280;
        MouseMove += OnMove;
        MouseLeave += (_, _) => { _mouse = null; if (_drag < 0) _hover = -1; InvalidateVisual(); };
        MouseLeftButtonDown += (_, e) =>
        {
            if (_hover < 0 || Highlighted is not { } line || HandleLevels is not { } handleLevels) return;
            _dragMax = Scale(line);
            _drag = _hover;
            _dragValue = line.Values[Math.Clamp(handleLevels[_drag] - 1, 0, line.Values.Length - 1)];
            CaptureMouse();
            e.Handled = true;
        };
        MouseLeftButtonUp += (_, e) =>
        {
            if (_drag < 0) return;
            int handle = _drag; double droppedValue = _dragValue;
            _drag = -1;
            ReleaseMouseCapture();
            InvalidateVisual();
            HandleDropped?.Invoke(handle, droppedValue);
            e.Handled = true;
        };
    }

    private ChartSeries? Highlighted => Series?.FirstOrDefault(s => s.Highlight);

    private void OnMove(object sender, MouseEventArgs e)
    {
        _mouse = e.GetPosition(this);
        double height = ActualHeight;
        if (_drag >= 0)
        {
            double y = Math.Clamp(_mouse.Value.Y, Top, height - Bottom);
            _dragValue = Math.Max(0, Math.Round((1 - (y - Top) / (height - Top - Bottom)) * _dragMax));
        }
        else
        {
            _hover = -1;
            if (Highlighted is { } line && HandleLevels is { } handleLevels)
            {
                double max = Scale(line);
                for (int handle = 0; handle < handleLevels.Count; handle++)
                {
                    int i = Math.Clamp(handleLevels[handle] - 1, 0, line.Values.Length - 1);
                    var handlePoint = new Point(X(i, line.Values.Length), Y(line.Values[i], max));
                    if ((handlePoint - _mouse.Value).Length <= 8) { _hover = handle; break; }
                }
            }
            Cursor = _hover >= 0 ? Cursors.SizeNS : null;
        }
        InvalidateVisual();
    }

    protected override Size MeasureOverride(Size available) => new(double.IsInfinity(available.Width) ? 520 : Math.Max(MinWidth, available.Width), 220);

    private static readonly Typeface Face = new("Segoe UI");

    private double X(int i, int levels) => Left + (ActualWidth - Left - Right) * i / Math.Max(1, levels - 1);
    private double Y(double v, double max) => Top + (ActualHeight - Top - Bottom) * (1 - Math.Clamp(v / max, 0, 1.05));

    // the top of the scale for the line(s) shown (it stays put while a handle is dragged)
    private double Scale(ChartSeries s)
    {
        if (_drag >= 0) return _dragMax;
        var series = Series!;
        var shown = series.Where(x => x.Highlight).DefaultIfEmpty(series[0]).ToList();
        double max = Math.Max(1, shown.Max(x => x.Values.Length == 0 ? 0 : x.Values.Max()));
        double step = Nice(max / 4);
        return Math.Ceiling(max * 1.15 / step) * step;   // room above the line to drag it up
    }

    // the highlighted line while a handle is being dragged: the two bands next to it bend, the rest stays
    private double[] Preview(ChartSeries s)
    {
        var values = (double[])s.Values.Clone();
        var handleLevels = HandleLevels!;
        int at = handleLevels[_drag] - 1, from = _drag > 0 ? handleLevels[_drag - 1] - 1 : 0, to = _drag + 1 < handleLevels.Count ? handleLevels[_drag + 1] - 1 : -1;
        double delta = _dragValue - s.Values[at];
        for (int i = from + 1; i <= at && i < values.Length; i++) values[i] += delta * (i - from) / Math.Max(1, at - from);
        if (to > at) for (int i = at + 1; i < to && i < values.Length; i++) values[i] += delta * (to - i) / (to - at);
        else for (int i = at + 1; i < values.Length; i++) values[i] += delta;   // the last handle moves everything after it
        return values;
    }

    protected override void OnRender(DrawingContext dc)
    {
        double width = ActualWidth, height = ActualHeight;
        dc.DrawRectangle(new SolidColorBrush(Color.FromArgb(0x30, 0, 0, 0)), null, new Rect(0, 0, width, height));
        var series = Series;
        if (series == null || series.Count == 0 || width < 60 || height < 40) return;
        double dpi = VisualTreeHelper.GetDpi(this).PixelsPerDip;
        var label = new SolidColorBrush(Color.FromRgb(0xB8, 0xA8, 0xD8));
        var grid = new Pen(new SolidColorBrush(Color.FromArgb(0x40, 0xFF, 0xFF, 0xFF)), 1);
        FormattedText Text(string s, double size, Brush b) => new(s, CultureInfo.CurrentCulture, System.Windows.FlowDirection.LeftToRight, Face, size, b, dpi);

        int levels = series.Max(s => s.Values.Length);
        var shown = series.Where(s => s.Highlight).DefaultIfEmpty(series[0]).ToList();
        double max = Scale(shown[0]);
        double step = Nice(max / 5);
        for (double value = 0; value <= max + step / 2; value += step)
        {
            dc.DrawLine(grid, new Point(Left, Y(value, max)), new Point(width - Right, Y(value, max)));
            var tick = Text(value >= 10000 ? $"{value / 1000:0}k" : $"{value:0}", 10, label);
            dc.DrawText(tick, new Point(Left - 6 - tick.Width, Y(value, max) - tick.Height / 2));
        }
        foreach (int level in new[] { 1, 10, 20, 30, 40, 50, 60, 70, 80, 90, 99 })
        {
            if (level > levels) continue;
            dc.DrawLine(grid, new Point(X(level - 1, levels), Top), new Point(X(level - 1, levels), height - Bottom));
            var tick = Text(level.ToString(), 10, label);
            dc.DrawText(tick, new Point(X(level - 1, levels) - tick.Width / 2, height - Bottom + 4));
        }
        // the others first (thin and faint), the highlighted one on top
        foreach (var line in series.OrderBy(s => s.Highlight))
        {
            var values = line.Highlight && _drag >= 0 && HandleLevels != null ? Preview(line) : line.Values;
            if (values.Length < 2) continue;
            var color = line.Color;
            var pen = new Pen(new SolidColorBrush(line.Highlight ? color : Color.FromArgb(0x70, color.R, color.G, color.B)), line.Highlight ? 2.5 : 1.2);
            var geometry = new StreamGeometry();
            using (var ctx = geometry.Open())
            {
                ctx.BeginFigure(new Point(X(0, levels), Y(values[0], max)), false, false);
                for (int i = 1; i < values.Length; i++) ctx.LineTo(new Point(X(i, levels), Y(values[i], max)), true, false);
            }
            geometry.Freeze();
            dc.DrawGeometry(null, pen, geometry);
            // the handles
            if (line.Highlight && HandleLevels is { } handleLevels)
                for (int handle = 0; handle < handleLevels.Count; handle++)
                {
                    int i = Math.Clamp(handleLevels[handle] - 1, 0, values.Length - 1);
                    bool hot = handle == _hover || handle == _drag;
                    dc.DrawEllipse(hot ? Brushes.White : new SolidColorBrush(color), new Pen(Brushes.White, 1), new Point(X(i, levels), Y(values[i], max)), hot ? 6 : 4, hot ? 6 : 4);
                }
        }
        // the value under the mouse (or the one being dragged)
        if (_mouse is Point mouse && mouse.X >= Left && mouse.X <= width - Right)
        {
            int i = _drag >= 0 ? HandleLevels![_drag] - 1 : Math.Clamp((int)Math.Round((mouse.X - Left) / (width - Left - Right) * (levels - 1)), 0, levels - 1);
            dc.DrawLine(new Pen(label, 1), new Point(X(i, levels), Top), new Point(X(i, levels), height - Bottom));
            string text = _drag >= 0
                ? $"Level {i + 1}: {shown[0].Name} {_dragValue:#,0} (let go to set it)"
                : $"Level {i + 1}: " + string.Join(", ", shown.Where(s => i < s.Values.Length).Select(s => $"{s.Name} {s.Values[i]:#,0}"))
                  + (_hover >= 0 ? "  (drag the dot up or down)" : "");
            var tip = Text(text, 11, Brushes.White);
            double tipX = Math.Max(Left, Math.Min(X(i, levels) + 6, width - Right - tip.Width - 4));
            dc.DrawRectangle(new SolidColorBrush(Color.FromArgb(0xC0, 0x20, 0x10, 0x30)), null, new Rect(tipX - 3, Top, tip.Width + 6, tip.Height + 2));
            dc.DrawText(tip, new Point(tipX, Top + 1));
        }
    }

    private static double Nice(double x)
    {
        if (x <= 0) return 1;
        double power = Math.Pow(10, Math.Floor(Math.Log10(x)));
        double fraction = x / power;
        return (fraction <= 1 ? 1 : fraction <= 2 ? 2 : fraction <= 5 ? 5 : 10) * power;
    }
}
