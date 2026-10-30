using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Shapes;
using Brushes = System.Windows.Media.Brushes;
using Color = System.Windows.Media.Color;
using Orientation = System.Windows.Controls.Orientation;
using Rectangle = System.Windows.Shapes.Rectangle;
using Brush = System.Windows.Media.Brush;
using Cursors = System.Windows.Input.Cursors;

namespace LufiaForge.Modules.Lufia2;

/// <summary>
/// The steps of a Lufia II event placed in time, as Lufia 1's cutscene timeline: a track per character, plus the
/// camera, dialogue, screen, sound, story flags and timing; a block per step, as long as the step takes in the game
/// (dialogue timed at a reading speed). Click a block to pick its step; the red playhead follows playback.
/// </summary>
public sealed class L2TimelineView : Grid
{
    private readonly ScrollViewer _scroll = new() { HorizontalScrollBarVisibility = ScrollBarVisibility.Auto, VerticalScrollBarVisibility = ScrollBarVisibility.Auto, Background = new SolidColorBrush(Color.FromRgb(0x14, 0x0C, 0x22)) };
    private readonly Canvas _canvas = new() { Background = Brushes.Transparent };
    private readonly Slider _zoom = new() { Minimum = 0.25, Maximum = 6, Value = 1.5, Width = 110, VerticalAlignment = VerticalAlignment.Center, ToolTip = "Pixels per frame" };
    private readonly TextBlock _info = new() { Foreground = new SolidColorBrush(Color.FromRgb(0xB8, 0xA8, 0xD8)), VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(10, 0, 0, 0), TextTrimming = TextTrimming.CharacterEllipsis };
    private readonly Line _playhead = new() { Stroke = Brushes.Red, StrokeThickness = 2, IsHitTestVisible = false };
    private readonly Canvas _names;
    private readonly ScrollViewer _namesScroll;
    private L2ScriptEditor? _editor;
    private List<L2TimelineItem> _items = new();
    private List<string> _tracks = new();
    private int _playFrame, _selectedLine = -1;

    private const double LabelW = 8, TrackH = 26, RulerH = 22, NamesW = 120;
    private double Ppf => _zoom.Value;

    private static readonly Dictionary<string, Brush> KindBrush = new()
    {
        ["walk"] = new SolidColorBrush(Color.FromRgb(0x2E, 0x7D, 0xC8)),
        ["character"] = new SolidColorBrush(Color.FromRgb(0x3A, 0x9A, 0x6A)),
        ["camera"] = new SolidColorBrush(Color.FromRgb(0xC8, 0x8A, 0x2E)),
        ["dialogue"] = new SolidColorBrush(Color.FromRgb(0x8A, 0x4A, 0xC8)),
        ["screen"] = new SolidColorBrush(Color.FromRgb(0x60, 0x60, 0x70)),
        ["sound"] = new SolidColorBrush(Color.FromRgb(0xC8, 0x3A, 0x8A)),
        ["flag"] = new SolidColorBrush(Color.FromRgb(0xA0, 0x90, 0x30)),
        ["wait"] = new SolidColorBrush(Color.FromRgb(0x40, 0x40, 0x50)),
        ["flow"] = new SolidColorBrush(Color.FromRgb(0x30, 0x80, 0x90)),
        ["other"] = new SolidColorBrush(Color.FromRgb(0x50, 0x50, 0x60)),
    };

    public L2TimelineView()
    {
        RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        var bar = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 0, 0, 4) };
        bar.Children.Add(new TextBlock { Text = "Zoom", Foreground = Brushes.Gold, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 6, 0) });
        bar.Children.Add(_zoom);
        bar.Children.Add(_info);
        Children.Add(bar);
        _names = new Canvas { Width = NamesW, Background = new SolidColorBrush(Color.FromRgb(0x1C, 0x12, 0x2C)) };
        _namesScroll = new ScrollViewer { VerticalScrollBarVisibility = ScrollBarVisibility.Hidden, HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled, Width = NamesW, Content = _names };
        var body = new DockPanel();
        DockPanel.SetDock(_namesScroll, Dock.Left);
        body.Children.Add(_namesScroll);
        _scroll.Content = _canvas;
        _scroll.ScrollChanged += (_, _) => _namesScroll.ScrollToVerticalOffset(_scroll.VerticalOffset);
        body.Children.Add(_scroll);
        SetRow(body, 1);
        Children.Add(body);
        _zoom.ValueChanged += (_, _) => Draw();
        _canvas.MouseLeftButtonDown += Canvas_Down;
    }

    public void Bind(L2ScriptEditor editor)
    {
        if (_editor != null) { _editor.Resimulated -= Refresh; _editor.PlaybackProgress -= OnProgress; }
        _editor = editor;
        editor.Resimulated += Refresh;
        editor.PlaybackProgress += OnProgress;
        Refresh();
    }

    public void Refresh()
    {
        if (_editor == null || Visibility != Visibility.Visible && !IsVisible) { if (_editor == null) return; }
        _items = _editor?.TimelineItems.ToList() ?? new();
        // tracks: characters in the order they first act, then the rest in a fixed order
        var fixedOrder = new[] { "Dialogue", "Camera", "Screen", "Music and sound", "Story flags", "Timing", "Checks and jumps", "Other" };
        _tracks = _items.Select(i => i.Track).Where(t => !fixedOrder.Contains(t)).Distinct().Concat(fixedOrder.Where(t => _items.Any(i => i.Track == t))).ToList();
        Draw();
    }

    public void SelectLine(int line)
    {
        _selectedLine = line;
        var it = _items.FirstOrDefault(i => i.Line == line);
        if (it != null) { _playFrame = it.Start; ScrollToFrame(it.Start); }
        Draw();
    }

    private void OnProgress(int line, int framesIntoLine)
    {
        var it = _items.FirstOrDefault(i => i.Line == line);
        if (it == null) return;
        _playFrame = it.Start + framesIntoLine;
        PlacePlayhead();
        ScrollToFrame(_playFrame);
    }

    private double X(int frame) => LabelW + frame * Ppf;

    private void ScrollToFrame(int frame)
    {
        double x = X(frame);
        if (x < _scroll.HorizontalOffset + LabelW || x > _scroll.HorizontalOffset + _scroll.ViewportWidth - 40)
            _scroll.ScrollToHorizontalOffset(Math.Max(0, x - _scroll.ViewportWidth / 3));
    }

    private void Draw()
    {
        _canvas.Children.Clear();
        _names.Children.Clear();
        if (_items.Count == 0) { _info.Text = "No steps to place in time."; return; }
        int total = _items.Max(i => i.Start + Math.Max(1, i.Duration));
        double width = X(total) + 80, height = RulerH + _tracks.Count * TrackH + 10;
        _canvas.Width = width; _canvas.Height = height; _names.Height = height + 20;
        int step = Ppf * 60 >= 40 ? 60 : 300;
        for (int f = 0; f <= total; f += 30)
        {
            bool major = f % step == 0;
            _canvas.Children.Add(new Line { X1 = X(f), X2 = X(f), Y1 = major ? 4 : 12, Y2 = height, Stroke = new SolidColorBrush(Color.FromArgb((byte)(major ? 70 : 28), 255, 255, 255)), IsHitTestVisible = false });
            if (major)
            {
                var t = new TextBlock { Text = $"{f / 60}s", Foreground = Brushes.Gray, FontSize = 10, IsHitTestVisible = false };
                Canvas.SetLeft(t, X(f) + 2); Canvas.SetTop(t, 2);
                _canvas.Children.Add(t);
            }
        }
        for (int k = 0; k < _tracks.Count; k++)
        {
            double y = RulerH + k * TrackH;
            var bg = new Rectangle { Width = width, Height = TrackH - 2, Fill = new SolidColorBrush(Color.FromArgb((byte)(k % 2 == 0 ? 26 : 12), 255, 255, 255)), IsHitTestVisible = false };
            Canvas.SetLeft(bg, 0); Canvas.SetTop(bg, y);
            _canvas.Children.Add(bg);
            var label = new TextBlock { Text = _tracks[k], Foreground = Brushes.Gold, FontSize = 11, FontWeight = FontWeights.SemiBold, Width = NamesW - 8, TextTrimming = TextTrimming.CharacterEllipsis, ToolTip = _tracks[k] };
            Canvas.SetLeft(label, 4); Canvas.SetTop(label, y + 5);
            _names.Children.Add(label);
        }
        foreach (var it in _items)
        {
            int k = _tracks.IndexOf(it.Track);
            if (k < 0) continue;
            double w = Math.Max(6, it.Duration * Ppf);
            var block = new Border
            {
                Width = w, Height = TrackH - 6, CornerRadius = new CornerRadius(3), Tag = it, Cursor = Cursors.Hand,
                Background = KindBrush.GetValueOrDefault(it.Kind, KindBrush["other"]),
                BorderBrush = it.Line == _selectedLine ? Brushes.Gold : new SolidColorBrush(Color.FromArgb(120, 255, 255, 255)),
                BorderThickness = new Thickness(it.Line == _selectedLine ? 2 : 1),
                Opacity = it.Reached ? 1 : 0.45,
                ToolTip = $"Step {it.Line + 1}: {it.Label}\nStarts {Sec(it.Start)}, lasts {Sec(it.Duration)}" + (it.Reached ? "" : "\n(playback doesn't reach it with these story flags)") + "\nClick to pick the step",
                Child = new TextBlock { Text = it.Label, Foreground = Brushes.White, FontSize = 10, TextTrimming = TextTrimming.CharacterEllipsis, Margin = new Thickness(4, 1, 2, 0), IsHitTestVisible = false },
            };
            Canvas.SetLeft(block, X(it.Start)); Canvas.SetTop(block, RulerH + k * TrackH + 2);
            block.MouseLeftButtonDown += Block_Down;
            _canvas.Children.Add(block);
        }
        _canvas.Children.Add(_playhead);
        PlacePlayhead();
        _info.Text = $"{_items.Count} steps · about {Sec(total)} (dialogue timed at a typical reading speed)";
    }

    private static string Sec(int frames) => frames < 60 ? $"{frames} frames" : $"{frames / 60.0:0.0} s";

    private void PlacePlayhead()
    {
        _playhead.X1 = _playhead.X2 = X(_playFrame);
        _playhead.Y1 = 0; _playhead.Y2 = _canvas.Height;
    }

    private void Block_Down(object sender, MouseButtonEventArgs e)
    {
        if (sender is Border { Tag: L2TimelineItem it } && _editor != null) { _editor.SelectedLine = it.Line; e.Handled = true; }
    }

    private void Canvas_Down(object sender, MouseButtonEventArgs e)
    {
        var p = e.GetPosition(_canvas);
        int frame = Math.Max(0, (int)((p.X - LabelW) / Ppf));
        _playFrame = frame;
        PlacePlayhead();
        var it = _items.Where(i => i.Start <= frame).OrderByDescending(i => i.Start).FirstOrDefault();
        if (it != null && _editor != null) _editor.SelectedLine = it.Line;
    }
}
