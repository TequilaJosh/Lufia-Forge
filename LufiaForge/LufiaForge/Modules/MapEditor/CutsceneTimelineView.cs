using LufiaForge.Core.Maps;
using LufiaForge.Modules.MemoryMonitor;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Shapes;
using System.Windows.Threading;
using Brush = System.Windows.Media.Brush;
using Brushes = System.Windows.Media.Brushes;
using Button = System.Windows.Controls.Button;
using CheckBox = System.Windows.Controls.CheckBox;
using Color = System.Windows.Media.Color;
using Cursors = System.Windows.Input.Cursors;
using MouseEventArgs = System.Windows.Input.MouseEventArgs;
using Orientation = System.Windows.Controls.Orientation;
using Point = System.Windows.Point;
using Rectangle = System.Windows.Shapes.Rectangle;

namespace LufiaForge.Modules.MapEditor;

/// <summary>
/// Cutscene timeline (phase 6.5): every line as a block on its track (characters, camera, dialogue, screen, sound,
/// waits, logic), placed by when it happens. Drag a block sideways to change the wait before it, drag its right
/// edge to change how long it takes (waits, walk speed, scroll speed, fades). Play / stop / rewind drive the stage
/// preview and the playhead; "Follow the game" moves the playhead to the line BizHawk is running.
/// </summary>
public sealed class CutsceneTimelineView : DockPanel
{
    private readonly ScrollViewer _scroll = new() { HorizontalScrollBarVisibility = ScrollBarVisibility.Auto, VerticalScrollBarVisibility = ScrollBarVisibility.Auto, Background = new SolidColorBrush(Color.FromRgb(0x14, 0x0C, 0x22)) };
    private readonly Canvas _canvas = new() { Background = Brushes.Transparent };
    private readonly Slider _zoom = new() { Minimum = 0.25, Maximum = 6, Value = 1.5, Width = 110, VerticalAlignment = VerticalAlignment.Center, ToolTip = "Pixels per frame" };
    private readonly TextBlock _info = new() { Foreground = new SolidColorBrush(Color.FromRgb(0xB8, 0xA8, 0xD8)), VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(10, 0, 0, 0), TextTrimming = TextTrimming.CharacterEllipsis };
    private readonly CheckBox _follow = new() { Content = "Follow the game", Foreground = Brushes.White, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(10, 0, 0, 0), ToolTip = "Move the playhead to the line BizHawk is running (Lufia Forge tool open in BizHawk)" };
    private readonly Line _playhead = new() { Stroke = Brushes.Red, StrokeThickness = 2, IsHitTestVisible = false };
    private readonly DispatcherTimer _followTimer = new() { Interval = TimeSpan.FromMilliseconds(150) };
    private BizHawkBridge? _bridge;

    private EventEditorPanel? _panel;
    private CutsceneStageView? _stage;
    private List<TimelineItem> _items = new();
    private List<string> _tracks = new();
    private int _playFrame;
    private int _selectedLine = -1;

    private const double LabelW = 8, TrackH = 26, RulerH = 22, NamesW = 110;
    private readonly Canvas _names = new() { Width = NamesW, Background = new SolidColorBrush(Color.FromRgb(0x1C, 0x12, 0x2C)) };
    private readonly ScrollViewer _namesScroll = new() { VerticalScrollBarVisibility = ScrollBarVisibility.Hidden, HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled, Width = NamesW };
    private double Ppf => _zoom.Value;   // pixels per frame

    private static readonly Dictionary<string, Brush> KindBrush = new()
    {
        ["text"] = new SolidColorBrush(Color.FromRgb(0x7A, 0x4C, 0xC8)),
        ["walk"] = new SolidColorBrush(Color.FromRgb(0x2E, 0x9E, 0x6A)),
        ["actor"] = new SolidColorBrush(Color.FromRgb(0x3A, 0x7E, 0x5A)),
        ["camera"] = new SolidColorBrush(Color.FromRgb(0x3C, 0x78, 0xC8)),
        ["screen"] = new SolidColorBrush(Color.FromRgb(0xC8, 0x6E, 0x2C)),
        ["sound"] = new SolidColorBrush(Color.FromRgb(0xC8, 0xA8, 0x2C)),
        ["wait"] = new SolidColorBrush(Color.FromRgb(0x5A, 0x5A, 0x6E)),
        ["logic"] = new SolidColorBrush(Color.FromRgb(0x2C, 0x5A, 0x8C)),
        ["other"] = new SolidColorBrush(Color.FromRgb(0x50, 0x46, 0x66)),
    };

    public CutsceneTimelineView()
    {
        var bar = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 0, 0, 4) };
        Button B(string text, string tip, RoutedEventHandler h) { var b = new Button { Content = text, ToolTip = tip, Margin = new Thickness(0, 0, 4, 0), Padding = new Thickness(8, 2, 8, 2) }; b.SetResourceReference(StyleProperty, "LufiaButton"); b.Click += h; return b; }
        bar.Children.Add(B("⏮", "Rewind: play from the start", (_, _) => _stage?.PlayFrom(0)));
        bar.Children.Add(B("▶", "Play from the selected line", (_, _) => _stage?.PlayFrom(Math.Max(0, _selectedLine))));
        bar.Children.Add(B("⏸", "Stop", (_, _) => _stage?.Stop()));
        bar.Children.Add(B("＋ Add at playhead…", "Add a line where the playhead is", (_, _) => AddAtPlayhead()));
        bar.Children.Add(new TextBlock { Text = "Zoom", Foreground = Brushes.White, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(8, 0, 4, 0) });
        bar.Children.Add(_zoom);
        bar.Children.Add(_follow);
        bar.Children.Add(_info);
        SetDock(bar, Dock.Top);
        Children.Add(bar);
        _scroll.Content = _canvas;
        // track names stay put while the time axis scrolls
        _namesScroll.Content = _names;
        SetDock(_namesScroll, Dock.Left);
        Children.Add(_namesScroll);
        Children.Add(_scroll);
        _scroll.ScrollChanged += (_, _) => _namesScroll.ScrollToVerticalOffset(_scroll.VerticalOffset);
        _zoom.ValueChanged += (_, _) => Draw();
        _canvas.MouseMove += Canvas_MouseMove;
        _canvas.MouseLeftButtonUp += Canvas_MouseUp;
        _canvas.MouseLeftButtonDown += Canvas_Down;
        _follow.Click += (_, _) => { if (_follow.IsChecked == true) _followTimer.Start(); else _followTimer.Stop(); };
        _followTimer.Tick += (_, _) => FollowGame();
        Common.MapNavigation.Attach(_scroll, () => _zoom.Value, z => _zoom.Value = z, _zoom.Minimum, _zoom.Maximum);
    }

    public void Bind(EventEditorPanel panel, CutsceneStageView stage)
    {
        if (_stage != stage)
        {
            if (_stage != null) { _stage.Resimulated -= Refresh; _stage.PlaybackProgress -= OnProgress; }
            _stage = stage;
            _stage.Resimulated += Refresh;
            _stage.PlaybackProgress += OnProgress;
        }
        _panel = panel;
        Refresh();
    }

    public void Refresh()
    {
        if (_stage == null) return;
        _items = _stage.TimelineItems.ToList();
        _tracks = CutsceneTimeline.Tracks(_items);
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

    // ── drawing ─────────────────────────────────────────────────────────────

    private void Draw()
    {
        _canvas.Children.Clear();
        _names.Children.Clear();
        if (_items.Count == 0) { _info.Text = "No lines to place in time."; return; }
        int total = _items.Max(i => i.Start + Math.Max(1, i.Duration));
        double width = X(total) + 80, height = RulerH + _tracks.Count * TrackH + 10;
        _canvas.Width = width; _canvas.Height = height; _names.Height = height + 20;

        // ruler: a tick every second (60 frames), labels every 1 or 5 s depending on zoom
        int step = Ppf * 60 >= 40 ? 60 : 300;
        for (int f = 0; f <= total; f += 30)
        {
            bool major = f % step == 0;
            var tick = new Line { X1 = X(f), X2 = X(f), Y1 = major ? 4 : 12, Y2 = height, Stroke = new SolidColorBrush(Color.FromArgb((byte)(major ? 70 : 28), 255, 255, 255)), IsHitTestVisible = false };
            _canvas.Children.Add(tick);
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
                Width = w, Height = TrackH - 6, CornerRadius = new CornerRadius(3), Tag = it, Cursor = Cursors.SizeWE,
                Background = KindBrush.GetValueOrDefault(it.Kind, KindBrush["other"]),
                BorderBrush = it.Line == _selectedLine ? Brushes.Gold : new SolidColorBrush(Color.FromArgb(it.Blocking ? (byte)200 : (byte)90, 255, 255, 255)),
                BorderThickness = new Thickness(it.Line == _selectedLine ? 2 : 1),
                Opacity = it.Blocking ? 1 : 0.75,
                ToolTip = $"Line {it.Line + 1}: {it.Label}\nStarts {Sec(it.Start)}, lasts {Sec(it.Duration)}{(it.Blocking ? "" : " (the cutscene doesn't wait for it)")}\n" +
                          "Click to edit · drag to move in time · drag the right edge to change its length",
                Child = new TextBlock { Text = it.Label, Foreground = Brushes.White, FontSize = 10, TextTrimming = TextTrimming.CharacterEllipsis, Margin = new Thickness(4, 1, 2, 0), IsHitTestVisible = false },
            };
            Canvas.SetLeft(block, X(it.Start)); Canvas.SetTop(block, RulerH + k * TrackH + 2);
            block.MouseLeftButtonDown += Block_Down;
            _canvas.Children.Add(block);
        }
        _canvas.Children.Add(_playhead);
        PlacePlayhead();
        _info.Text = $"{_items.Count} lines · about {Sec(total)} (dialogue timed at a typical reading speed)";
    }

    private static string Sec(int frames) => frames < 60 ? $"{frames} frames" : $"{frames / 60.0:0.0} s";

    private void PlacePlayhead()
    {
        _playhead.X1 = _playhead.X2 = X(_playFrame);
        _playhead.Y1 = 0; _playhead.Y2 = _canvas.Height;
    }

    // ── editing by dragging ─────────────────────────────────────────────────

    private TimelineItem? _drag;
    private bool _resize, _moved;
    private Point _dragStart;
    private Border? _dragBlock;
    private double _blockLeft, _blockWidth;

    private void Block_Down(object sender, MouseButtonEventArgs e)
    {
        if (sender is not Border { Tag: TimelineItem it } b) return;
        _drag = it; _dragBlock = b; _dragStart = e.GetPosition(_canvas); _moved = false;
        _blockLeft = Canvas.GetLeft(b); _blockWidth = b.Width;
        _resize = e.GetPosition(b).X > b.Width - 7;
        _canvas.CaptureMouse();
        e.Handled = true;
    }

    private void Canvas_Down(object sender, MouseButtonEventArgs e)
    {
        // click on empty space: move the playhead there
        var p = e.GetPosition(_canvas);
        if (p.X < LabelW) return;
        _playFrame = Math.Max(0, (int)((p.X - LabelW) / Ppf));
        PlacePlayhead();
    }

    private void Canvas_MouseMove(object sender, MouseEventArgs e)
    {
        if (_drag == null || _dragBlock == null || e.LeftButton != MouseButtonState.Pressed) return;
        double dx = e.GetPosition(_canvas).X - _dragStart.X;
        if (!_moved && Math.Abs(dx) < 4) return;
        _moved = true;
        if (_resize) _dragBlock.Width = Math.Max(6, _blockWidth + dx);
        else Canvas.SetLeft(_dragBlock, Math.Max(LabelW, _blockLeft + dx));
        int frames = (int)Math.Round(dx / Ppf);
        _info.Text = _resize ? $"Length: {Sec(Math.Max(1, _drag.Duration + frames))}" : $"Move {(frames >= 0 ? "later" : "earlier")} by {Sec(Math.Abs(frames))}";
    }

    private void Canvas_MouseUp(object sender, MouseButtonEventArgs e)
    {
        _canvas.ReleaseMouseCapture();
        var it = _drag;
        _drag = null;
        if (it == null || _panel == null) return;
        if (!_moved)
        {
            _selectedLine = it.Line;
            _panel.SelectLine(it.Line);
            _panel.HighlightLine(it.Line);
            _playFrame = it.Start; PlacePlayhead(); Draw();
            return;
        }
        int frames = (int)Math.Round((e.GetPosition(_canvas).X - _dragStart.X) / Ppf);
        string? result = _resize ? Resize(it, Math.Max(1, it.Duration + frames)) : Move(it, frames);
        if (result != null) _info.Text = result;
        Refresh();
    }

    /// <summary>Move a line in time by changing the wait right before it.</summary>
    private string? Move(TimelineItem it, int frames)
    {
        var script = _panel!.Script;
        if (Math.Abs(frames) < 2) return null;
        int prev = it.Line - 1;
        int prevWait = prev >= 0 ? CutsceneTimeline.WaitFrames(script.Ops[prev]) : -1;
        if (prevWait >= 0)
        {
            int total = Math.Max(0, prevWait + frames);
            ReplaceWait(prev, total);
            return $"Line {it.Line + 1}: wait before it is now {Sec(total)}.";
        }
        if (frames < 0) return "Nothing to shorten: the line before this one isn't a wait. Drag it later, or shorten an earlier wait.";
        var waits = CutsceneTimeline.WaitCommands(frames);
        int at = it.Line - 1;
        foreach (var w in waits) at = _panel.InsertCommand(at, w);
        _panel.SelectLine(it.Line + waits.Count);
        return $"Added a {Sec(frames)} wait before line {it.Line + 1}.";
    }

    /// <summary>Replace the wait at <paramref name="line"/> with one (or more) lasting <paramref name="frames"/> (0 = remove it).</summary>
    private void ReplaceWait(int line, int frames)
    {
        var script = _panel!.Script;
        var waits = CutsceneTimeline.WaitCommands(frames);
        if (waits.Count == 0)
        {
            script.Remove(script.Ops[line]);
            _panel.LinesChangedExternally(Math.Max(0, line - 1));
            return;
        }
        _panel.SetLineBytes(line, waits[0]);
        int at = line;
        for (int k = 1; k < waits.Count; k++) at = _panel.InsertCommand(at, waits[k]);
    }

    /// <summary>Change how long a line takes.</summary>
    private string? Resize(TimelineItem it, int frames)
    {
        var script = _panel!.Script;
        var op = script.Ops[it.Line];
        if (op.IsText) return "Dialogue lasts until the player closes it; its length here is an estimate.";
        var b = op.Bytes;
        if (CutsceneTimeline.WaitFrames(op) >= 0) { ReplaceWait(it.Line, frames); return $"Wait is now {Sec(frames)}."; }
        int Closest(Func<int, int> durationOf) => Enumerable.Range(0, 8).OrderBy(s => Math.Abs(durationOf(s) - frames)).First();
        switch (b[0])
        {
            case 0x14 or 0x15:
            {
                int tiles = Math.Max(1, it.Duration / Math.Max(1, CutsceneStage.FramesPerTile(_stage!.StageMap!.Paths[b[1] - 1].Steps.FirstOrDefault()?.Speed ?? 4)));
                int speed = Closest(s => tiles * CutsceneStage.FramesPerTile(s));
                _stage.SetPathSpeed(b[1], speed);
                _panel.Changed();
                return $"Walk speed set to {speed} for path {b[1]} (applies to every line using that path; ✔ Apply writes it).";
            }
            case >= 0x10 and <= 0x13:
            {
                int tiles = b[1] == 0 ? 256 : b[1];
                int speed = Closest(s => tiles * CutsceneStage.FramesPerTile(s));
                var nb = (byte[])b.Clone(); nb[2] = (byte)speed;
                _panel.SetLineBytes(it.Line, nb);
                return $"Scroll speed set to {speed}.";
            }
            case 0x18 or 0x19:
            {
                var nb = (byte[])b.Clone(); nb[1] = (byte)Math.Clamp((frames + 16) / 32, 1, 255);
                _panel.SetLineBytes(it.Line, nb);
                return $"Fade takes {nb[1]} frame(s) per step.";
            }
            default:
                return "This line happens instantly; its length can't change.";
        }
    }

    private void AddAtPlayhead()
    {
        if (_panel == null || _items.Count == 0) return;
        var before = _items.LastOrDefault(i => i.Start <= _playFrame) ?? _items[0];
        _panel.ShowInsertMenuAt(this, before.Line + 1);
    }

    // ── follow the running game ─────────────────────────────────────────────

    private void FollowGame()
    {
        if (_panel?.Script is not { } script) return;
        _bridge ??= new BizHawkBridge();
        if (!_bridge.IsConnected) _bridge.TryConnect();
        _bridge.ReadFrame();
        var wram = _bridge.Wram;
        if (!_bridge.IsConnected || wram == null) { _info.Text = "Follow the game: not connected to BizHawk (open the Lufia Forge tool in BizHawk)."; return; }
        if (wram[0x0D14] == 0xFF) { _info.Text = "Follow the game: no event running."; return; }
        int off = 0x18000 + (wram[0x0D12] | (wram[0x0D13] << 8) | (wram[0x0D14] << 16));
        int line = script.Ops.FindLastIndex(o => !o.IsNew && o.Offset <= off && off < o.Offset + Math.Max(1, o.Length + 1));
        if (line < 0) { _info.Text = $"Follow the game: the game is running another script (0x{off:X6})."; return; }
        var it = _items.FirstOrDefault(i => i.Line == line);
        if (it != null) { _playFrame = it.Start; PlacePlayhead(); ScrollToFrame(it.Start); }
        if (line != _selectedLine) { _selectedLine = line; _panel.HighlightLine(line); Draw(); }
        _info.Text = $"Follow the game: line {line + 1} is running.";
    }
}
