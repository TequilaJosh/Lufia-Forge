using LufiaForge.Core.Maps;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Shapes;
using Brush = System.Windows.Media.Brush;
using Cursors = System.Windows.Input.Cursors;
using Brushes = System.Windows.Media.Brushes;
using Color = System.Windows.Media.Color;
using ContextMenu = System.Windows.Controls.ContextMenu;
using MenuItem = System.Windows.Controls.MenuItem;
using MouseEventArgs = System.Windows.Input.MouseEventArgs;
using Point = System.Windows.Point;
using Rectangle = System.Windows.Shapes.Rectangle;

namespace LufiaForge.Modules.MapEditor;

/// <summary>A run of lines with no jump into its middle and no jump out of it before its last line.</summary>
public sealed class FlowBlock
{
    public int Start { get; init; }
    public int End { get; init; }            // exclusive
    public ScriptOp First { get; init; } = null!;
    public ScriptOp Last { get; init; } = null!;
    public string Kind { get; init; } = "";  // dialogue, choice, branch, end, step
    public Border Card { get; set; } = null!;
    public Point Pos { get; set; }
    public int Layer { get; set; } = -1;
}

/// <summary>
/// The event as a graph (phase 6.2): blocks of lines as cards, arrows for jumps and fall-through. Pan with the middle
/// mouse button, zoom with Ctrl + wheel, drag cards, drag a jump's port onto a card to retarget it, right-click for
/// insert / duplicate / delete. Card positions are cosmetic.
/// </summary>
public sealed class FlowGraphView : Grid
{
    private readonly ScrollViewer _scroll = new() { HorizontalScrollBarVisibility = ScrollBarVisibility.Auto, VerticalScrollBarVisibility = ScrollBarVisibility.Auto, Background = new SolidColorBrush(Color.FromRgb(0x16, 0x0E, 0x24)) };
    private readonly Canvas _canvas = new() { Background = Brushes.Transparent };
    private readonly ScaleTransform _zoom = new(1, 1);
    private readonly Border _minimap = new() { Width = 170, Height = 120, BorderBrush = new SolidColorBrush(Color.FromArgb(160, 255, 215, 0)), BorderThickness = new Thickness(1), Background = new SolidColorBrush(Color.FromArgb(200, 10, 6, 20)), HorizontalAlignment = System.Windows.HorizontalAlignment.Right, VerticalAlignment = System.Windows.VerticalAlignment.Bottom, Margin = new Thickness(0, 0, 18, 18), Cursor = Cursors.Hand };
    private readonly Canvas _miniCanvas = new();
    private readonly Rectangle _miniView = new() { Stroke = Brushes.Gold, StrokeThickness = 1, Fill = new SolidColorBrush(Color.FromArgb(40, 255, 215, 0)) };
    private EventEditorPanel? _panel;
    private List<FlowBlock> _blocks = new();
    private readonly Dictionary<ScriptOp, Point> _custom = new();
    private FlowBlock? _selected;
    private double _miniScale = 1;

    public event Action<FlowBlock?>? BlockSelected;
    public FlowBlock? Selected => _selected;

    private static readonly Brush DialogueBg = new SolidColorBrush(Color.FromRgb(0x4A, 0x2C, 0x7A));
    private static readonly Brush ChoiceBg = new SolidColorBrush(Color.FromRgb(0x7A, 0x5E, 0x10));
    private static readonly Brush BranchBg = new SolidColorBrush(Color.FromRgb(0x1E, 0x46, 0x7A));
    private static readonly Brush EndBg = new SolidColorBrush(Color.FromRgb(0x7A, 0x22, 0x2A));
    private static readonly Brush StepBg = new SolidColorBrush(Color.FromRgb(0x34, 0x2A, 0x4E));
    private static readonly Brush JumpLine = new SolidColorBrush(Color.FromRgb(0xFF, 0xD7, 0x00));
    private static readonly Brush NextLine = new SolidColorBrush(Color.FromRgb(0xB0, 0xA8, 0xC8));
    private const double CardW = 230, ColGap = 90, RowGap = 26;

    public FlowGraphView()
    {
        _canvas.LayoutTransform = _zoom;
        RenderOptions.SetBitmapScalingMode(_canvas, BitmapScalingMode.NearestNeighbor);
        _scroll.Content = _canvas;
        Children.Add(_scroll);
        _minimap.Child = _miniCanvas;
        Children.Add(_minimap);
        _miniCanvas.Children.Add(_miniView);
        _minimap.MouseLeftButtonDown += Minimap_Click;
        _scroll.ScrollChanged += (_, _) => UpdateMiniView();
        Common.MapNavigation.Attach(_scroll, () => _zoom.ScaleX, z => { _zoom.ScaleX = _zoom.ScaleY = z; }, 0.3, 2.5);
        _canvas.MouseRightButtonDown += Canvas_RightDown;
        _canvas.MouseMove += Canvas_MouseMove;
        _canvas.MouseLeftButtonUp += Canvas_MouseUp;
    }

    public void Bind(EventEditorPanel panel) { _panel = panel; Rebuild(); }

    /// <summary>Forget dragged positions and lay the graph out again.</summary>
    public void AutoLayout() { _custom.Clear(); Rebuild(); }

    // ── model ───────────────────────────────────────────────────────────────

    private static List<FlowBlock> Split(EventScript s)
    {
        var ops = s.Ops;
        var starts = new SortedSet<int> { 0 };
        for (int i = 0; i < ops.Count; i++)
        {
            var o = ops[i];
            foreach (var t in o.Targets) if (t.Op != null && ops.IndexOf(t.Op) is int k and >= 0) starts.Add(k);
            if (o.Targets.Count > 0 || o.EndsFlow) starts.Add(i + 1);
        }
        var list = new List<FlowBlock>();
        var arr = starts.Where(i => i < ops.Count).ToList();
        for (int b = 0; b < arr.Count; b++)
        {
            int start = arr[b], end = b + 1 < arr.Count ? arr[b + 1] : ops.Count;
            var last = ops[end - 1];
            string kind = last.IsText ? (last.EndsFlow ? "end" : "dialogue")
                : last.Bytes.Length > 0 && last.Bytes[0] == 0x03 ? "choice"
                : last.EndsFlow && last.Bytes[0] is 0x00 or 0x51 ? "end"
                : last.Targets.Count > 0 && last.Bytes[0] != 0x01 ? "branch"
                : ops.Skip(start).Take(end - start).Any(o => o.IsText) ? "dialogue" : "step";
            list.Add(new FlowBlock { Start = start, End = end, First = ops[start], Last = last, Kind = kind });
        }
        return list;
    }

    private FlowBlock? BlockOf(ScriptOp op) => _blocks.FirstOrDefault(b => _panel!.Script.Ops.IndexOf(op) is int i && i >= b.Start && i < b.End);

    private string JumpLabel(ScriptOp o, int k)
    {
        var b = o.Bytes;
        if (b.Length == 0) return "";
        string Flag(int f) => EventScript.FlagName?.Invoke(f) is { Length: > 0 } n ? $"{f:X2} “{(n.Length > 18 ? n[..18] + "…" : n)}”" : $"{f:X2}";
        return b[0] switch
        {
            0x01 => "go to",
            0x02 => k == 0 ? "first time" : "later",
            0x03 => $"answer {k + 1}",
            0x04 => $"if {Flag(b[1])} set",
            0x05 => $"if {Flag(b[1])} not set",
            >= 0xC0 and <= 0xCF => $"if {Flag(0xF0 + (b[0] & 15))} set",
            >= 0xD0 and <= 0xDF => $"if {Flag(0xF0 + (b[0] & 15))} not set",
            0x45 => "if knows spell", 0x46 => "if has item", 0x47 => "if level ≥", 0x48 => "if HP ≥", 0x49 => "if MP ≥", 0x4F => "if key item",
            _ => "jump",
        };
    }

    // ── drawing ─────────────────────────────────────────────────────────────

    public void Rebuild()
    {
        _canvas.Children.Clear();
        if (_panel?.Script is not { } script || script.Ops.Count == 0) return;
        var prevSelected = _selected?.First;
        _blocks = Split(script);
        Layout();
        foreach (var b in _blocks) b.Card = MakeCard(b, script);
        DrawEdges(script);
        foreach (var b in _blocks) { Canvas.SetLeft(b.Card, b.Pos.X); Canvas.SetTop(b.Card, b.Pos.Y); _canvas.Children.Add(b.Card); }
        double w = _blocks.Max(b => b.Pos.X + CardW) + 60, h = _blocks.Max(b => b.Pos.Y + CardHeight(b)) + 60;
        _canvas.Width = w; _canvas.Height = h;
        _selected = prevSelected != null ? _blocks.FirstOrDefault(b => b.First == prevSelected) ?? BlockOf(prevSelected) : null;
        Highlight();
        BuildMinimap(w, h);
    }

    private static double CardHeight(FlowBlock b) => 30 + 16 * Math.Min(b.End - b.Start, 7) + (b.End - b.Start > 7 ? 16 : 0) + 8;

    private void Layout()
    {
        var script = _panel!.Script;
        // layers: breadth-first from the first block along jumps and fall-through
        var queue = new Queue<FlowBlock>();
        if (_blocks.Count > 0) { _blocks[0].Layer = 0; queue.Enqueue(_blocks[0]); }
        while (queue.Count > 0)
        {
            var b = queue.Dequeue();
            foreach (var next in Successors(b, script))
                if (next.Layer < 0) { next.Layer = b.Layer + 1; queue.Enqueue(next); }
        }
        int maxLayer = _blocks.Max(b => b.Layer);
        foreach (var b in _blocks.Where(b => b.Layer < 0)) b.Layer = maxLayer + 1;   // nothing reaches these
        foreach (var layer in _blocks.GroupBy(b => b.Layer))
        {
            double y = 20;
            foreach (var b in layer.OrderBy(b => b.Start))
            {
                b.Pos = _custom.TryGetValue(b.First, out var p) ? p : new Point(20 + b.Layer * (CardW + ColGap), y);
                y += CardHeight(b) + RowGap;
            }
        }
    }

    private IEnumerable<FlowBlock> Successors(FlowBlock b, EventScript s)
    {
        foreach (var t in b.Last.Targets)
            if (t.Op != null && _blocks.FirstOrDefault(x => x.First == t.Op) is { } tb) yield return tb;
        if (!b.Last.EndsFlow && b.End < s.Ops.Count && _blocks.FirstOrDefault(x => x.Start == b.End) is { } next) yield return next;
    }

    private Border MakeCard(FlowBlock b, EventScript s)
    {
        var stack = new StackPanel();
        string title = b.Kind switch { "dialogue" => "💬 Dialogue", "choice" => "❓ Choice", "branch" => "⑂ Branch", "end" => "■ End", _ => "▸ Steps" };
        stack.Children.Add(new TextBlock
        {
            Text = $"{title}   lines {b.Start + 1}{(b.End - b.Start > 1 ? $"–{b.End}" : "")}", Foreground = Brushes.Gold, FontWeight = FontWeights.SemiBold, FontSize = 11, Margin = new Thickness(0, 0, 0, 3),
        });
        for (int i = b.Start; i < Math.Min(b.End, b.Start + 7); i++)
        {
            var o = s.Ops[i];
            string text = o.IsText ? $"{EventScript.SpeakerName(o.Bytes)}: {o.Text.Replace("\n", " ")}" : s.Describe(o);
            stack.Children.Add(new TextBlock { Text = $"{i + 1,3}  {text}", Foreground = o.IsText ? Brushes.White : new SolidColorBrush(Color.FromRgb(0xD8, 0xD0, 0xF0)), FontSize = 11, TextTrimming = TextTrimming.CharacterEllipsis });
        }
        if (b.End - b.Start > 7) stack.Children.Add(new TextBlock { Text = $"     … {b.End - b.Start - 7} more line(s)", Foreground = Brushes.Gray, FontSize = 11 });
        var card = new Border
        {
            Width = CardW, MinHeight = CardHeight(b) - 8, Padding = new Thickness(8, 5, 8, 5), CornerRadius = new CornerRadius(b.Kind == "end" ? 14 : 4),
            Background = b.Kind switch { "dialogue" => DialogueBg, "choice" => ChoiceBg, "branch" => BranchBg, "end" => EndBg, _ => StepBg },
            BorderBrush = new SolidColorBrush(Color.FromArgb(120, 255, 255, 255)), BorderThickness = new Thickness(1), Child = stack, Tag = b, Cursor = Cursors.Hand,
            ToolTip = "Click to edit these lines below · drag to move · right-click for more",
        };
        card.MouseLeftButtonDown += Card_MouseDown;
        card.MouseRightButtonDown += Card_RightDown;
        return card;
    }

    private void DrawEdges(EventScript s)
    {
        foreach (var b in _blocks)
        {
            int k = 0;
            foreach (var t in b.Last.Targets)
            {
                var from = new Point(b.Pos.X + CardW, b.Pos.Y + 14 + 14 * k);
                var target = t.Op != null ? _blocks.FirstOrDefault(x => x.First == t.Op) : null;
                if (target != null) DrawArrow(from, new Point(target.Pos.X, target.Pos.Y + 14), JumpLine, JumpLabel(b.Last, k), false);
                else
                {
                    var stub = new Point(from.X + 50, from.Y);
                    DrawArrow(from, stub, JumpLine, JumpLabel(b.Last, k) + $" → {s.DescribeOutside(t.Absolute)}", false);
                }
                AddPort(b, k, from);
                k++;
            }
            if (!b.Last.EndsFlow && b.End < s.Ops.Count && _blocks.FirstOrDefault(x => x.Start == b.End) is { } next)
                DrawArrow(new Point(b.Pos.X + CardW / 2, b.Pos.Y + CardHeight(b) - 8), new Point(next.Pos.X + (next.Layer == b.Layer ? CardW / 2 : 0), next.Pos.Y + (next.Layer == b.Layer ? 0 : 14)),
                          NextLine, b.Last.Targets.Count > 0 && b.Last.Bytes[0] != 0x03 ? "otherwise" : "", next.Layer == b.Layer);
        }
    }

    private void DrawArrow(Point a, Point z, Brush brush, string label, bool vertical)
    {
        var fig = new PathFigure { StartPoint = a };
        if (vertical) fig.Segments.Add(new BezierSegment(new Point(a.X, a.Y + 20), new Point(z.X, z.Y - 20), z, true));
        else
        {
            double dx = Math.Max(40, Math.Abs(z.X - a.X) / 2);
            fig.Segments.Add(new BezierSegment(new Point(a.X + dx, a.Y), new Point(z.X - dx, z.Y), z, true));
        }
        var geo = new PathGeometry(); geo.Figures.Add(fig);
        _canvas.Children.Add(new Path { Data = geo, Stroke = brush, StrokeThickness = 1.6, IsHitTestVisible = false });
        // arrow head
        var head = new Polygon { Fill = brush, IsHitTestVisible = false };
        head.Points = vertical
            ? new PointCollection { z, new(z.X - 5, z.Y - 8), new(z.X + 5, z.Y - 8) }
            : new PointCollection { z, new(z.X - 8, z.Y - 5), new(z.X - 8, z.Y + 5) };
        _canvas.Children.Add(head);
        if (label.Length > 0)
        {
            var t = new TextBlock { Text = label, Foreground = brush, FontSize = 10, Background = new SolidColorBrush(Color.FromArgb(170, 22, 14, 36)), IsHitTestVisible = false };
            Canvas.SetLeft(t, a.X + (vertical ? 6 : 8)); Canvas.SetTop(t, a.Y + (vertical ? 4 : -14));
            _canvas.Children.Add(t);
        }
    }

    // ── jump ports: drag onto a card to retarget ────────────────────────────

    private (FlowBlock Block, int Index)? _dragPort;
    private Line? _rubber;

    private void AddPort(FlowBlock b, int k, Point at)
    {
        var port = new Ellipse { Width = 10, Height = 10, Fill = JumpLine, Stroke = Brushes.Black, StrokeThickness = 1, Cursor = Cursors.Cross, ToolTip = "Drag onto a card to make this jump go there" };
        Canvas.SetLeft(port, at.X - 5); Canvas.SetTop(port, at.Y - 5);
        port.MouseLeftButtonDown += (_, e) =>
        {
            _dragPort = (b, k);
            _rubber = new Line { X1 = at.X, Y1 = at.Y, X2 = at.X, Y2 = at.Y, Stroke = JumpLine, StrokeThickness = 2, StrokeDashArray = new DoubleCollection { 3, 2 }, IsHitTestVisible = false };
            _canvas.Children.Add(_rubber);
            _canvas.CaptureMouse();
            e.Handled = true;
        };
        _canvas.Children.Add(port);
    }

    // ── card dragging and selection ─────────────────────────────────────────

    private FlowBlock? _dragCard;
    private Point _dragStart, _cardStart;
    private bool _moved;

    private void Card_MouseDown(object sender, MouseButtonEventArgs e)
    {
        if (sender is not Border { Tag: FlowBlock b }) return;
        _dragCard = b; _dragStart = e.GetPosition(_canvas); _cardStart = b.Pos; _moved = false;
        _canvas.CaptureMouse();
        e.Handled = true;
    }

    private void Canvas_MouseMove(object sender, MouseEventArgs e)
    {
        var p = e.GetPosition(_canvas);
        if (_dragPort != null && _rubber != null) { _rubber.X2 = p.X; _rubber.Y2 = p.Y; return; }
        if (_dragCard == null || e.LeftButton != MouseButtonState.Pressed) return;
        var d = p - _dragStart;
        if (!_moved && Math.Abs(d.X) + Math.Abs(d.Y) < 5) return;
        _moved = true;
        _dragCard.Pos = new Point(Math.Max(0, _cardStart.X + d.X), Math.Max(0, _cardStart.Y + d.Y));
        Canvas.SetLeft(_dragCard.Card, _dragCard.Pos.X); Canvas.SetTop(_dragCard.Card, _dragCard.Pos.Y);
    }

    private void Canvas_MouseUp(object sender, MouseButtonEventArgs e)
    {
        _canvas.ReleaseMouseCapture();
        var p = e.GetPosition(_canvas);
        if (_dragPort is { } port)
        {
            _dragPort = null;
            if (_rubber != null) _canvas.Children.Remove(_rubber);
            var target = _blocks.FirstOrDefault(b => p.X >= b.Pos.X && p.X <= b.Pos.X + CardW && p.Y >= b.Pos.Y && p.Y <= b.Pos.Y + CardHeight(b));
            if (target != null && port.Index < port.Block.Last.Targets.Count && _panel != null)
            {
                port.Block.Last.Targets[port.Index].Op = target.First;
                _panel.Changed();
            }
            return;
        }
        if (_dragCard is { } card)
        {
            _dragCard = null;
            if (_moved) { _custom[card.First] = card.Pos; Rebuild(); }
            else Select(card);
        }
    }

    public void Select(FlowBlock? b)
    {
        _selected = b;
        Highlight();
        if (b != null) _panel?.SelectLine(b.Start, fromBytes: true);
        BlockSelected?.Invoke(b);
    }

    /// <summary>Select the card holding a line (from the Lines view).</summary>
    public void SelectLine(int index)
    {
        var b = _blocks.FirstOrDefault(x => index >= x.Start && index < x.End);
        if (b == null) return;
        _selected = b;
        Highlight();
        BlockSelected?.Invoke(b);
        b.Card.BringIntoView();
    }

    private void Highlight()
    {
        foreach (var b in _blocks)
        {
            if (b.Card == null) continue;
            b.Card.BorderBrush = b == _selected ? Brushes.Gold : new SolidColorBrush(Color.FromArgb(120, 255, 255, 255));
            b.Card.BorderThickness = new Thickness(b == _selected ? 2.5 : 1);
        }
    }

    // ── context menus ───────────────────────────────────────────────────────

    private void Card_RightDown(object sender, MouseButtonEventArgs e)
    {
        if (sender is not Border { Tag: FlowBlock b } card || _panel == null) return;
        Select(b);
        var menu = new ContextMenu { PlacementTarget = card };
        void Add(string h, Action a) { var mi = new MenuItem { Header = h }; mi.Click += (_, _) => a(); menu.Items.Add(mi); }
        Add("＋ Insert a line after this card…", () => _panel.ShowInsertMenuAt(card, b.End));
        Add("⧉ Duplicate these lines", () => _panel.DuplicateLines(b.Start, b.End));
        Add("✕ Delete these lines", () => _panel.DeleteLines(b.Start, b.End));
        menu.IsOpen = true;
        e.Handled = true;
    }

    private void Canvas_RightDown(object sender, MouseButtonEventArgs e)
    {
        if (e.Handled || _panel == null) return;
        var menu = new ContextMenu { PlacementTarget = _canvas };
        var add = new MenuItem { Header = "＋ Add a line at the end…" };
        add.Click += (_, _) => _panel.ShowInsertMenuAt(_canvas, Math.Max(0, _panel.Script.Ops.Count - 1));
        menu.Items.Add(add);
        var layout = new MenuItem { Header = "⟲ Auto-layout" };
        layout.Click += (_, _) => AutoLayout();
        menu.Items.Add(layout);
        menu.IsOpen = true;
    }

    // ── minimap ─────────────────────────────────────────────────────────────

    private void BuildMinimap(double w, double h)
    {
        _miniCanvas.Children.Clear();
        _miniScale = Math.Min(_minimap.Width / w, _minimap.Height / h);
        foreach (var b in _blocks)
        {
            var r = new Rectangle { Width = Math.Max(2, CardW * _miniScale), Height = Math.Max(2, CardHeight(b) * _miniScale), Fill = b.Card.Background, IsHitTestVisible = false };
            Canvas.SetLeft(r, b.Pos.X * _miniScale); Canvas.SetTop(r, b.Pos.Y * _miniScale);
            _miniCanvas.Children.Add(r);
        }
        _miniCanvas.Children.Add(_miniView);
        UpdateMiniView();
    }

    private void UpdateMiniView()
    {
        double z = _zoom.ScaleX;
        _miniView.Width = Math.Max(4, _scroll.ViewportWidth / z * _miniScale);
        _miniView.Height = Math.Max(4, _scroll.ViewportHeight / z * _miniScale);
        Canvas.SetLeft(_miniView, _scroll.HorizontalOffset / z * _miniScale);
        Canvas.SetTop(_miniView, _scroll.VerticalOffset / z * _miniScale);
    }

    private void Minimap_Click(object sender, MouseButtonEventArgs e)
    {
        var p = e.GetPosition(_miniCanvas);
        double z = _zoom.ScaleX;
        _scroll.ScrollToHorizontalOffset(p.X / _miniScale * z - _scroll.ViewportWidth / 2);
        _scroll.ScrollToVerticalOffset(p.Y / _miniScale * z - _scroll.ViewportHeight / 2);
        e.Handled = true;
    }
}
