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

// a run of lines that nothing jumps into the middle of and nothing jumps out of before its last line
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

// the event as a graph (phase 6.2): blocks of lines are cards, arrows for jumps and fall-through
// pan with the middle mouse button, zoom with Ctrl + wheel, drag cards around, drag a jump's port onto a card to
// retarget it, right-click for insert / duplicate / delete. card positions are only cosmetic
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
    private readonly Dictionary<ScriptOp, Point> _customPositions = new();
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

    // forgets the dragged positions and lays the graph out again
    public void AutoLayout() { _customPositions.Clear(); Rebuild(); }

    // model

    private static List<FlowBlock> Split(EventScript script)
    {
        var ops = script.Ops;
        var starts = new SortedSet<int> { 0 };
        for (int i = 0; i < ops.Count; i++)
        {
            var op = ops[i];
            foreach (var target in op.Targets) if (target.Op != null && ops.IndexOf(target.Op) is int targetLine and >= 0) starts.Add(targetLine);
            if (op.Targets.Count > 0 || op.EndsFlow) starts.Add(i + 1);
        }
        var list = new List<FlowBlock>();
        var blockStarts = starts.Where(i => i < ops.Count).ToList();
        for (int blockIndex = 0; blockIndex < blockStarts.Count; blockIndex++)
        {
            int start = blockStarts[blockIndex], end = blockIndex + 1 < blockStarts.Count ? blockStarts[blockIndex + 1] : ops.Count;
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

    private string JumpLabel(ScriptOp op, int targetIndex)
    {
        var opBytes = op.Bytes;
        if (opBytes.Length == 0) return "";
        string Flag(int flag) => EventScript.FlagName?.Invoke(flag) is { Length: > 0 } n ? $"{flag:X2} “{(n.Length > 18 ? n[..18] + "…" : n)}”" : $"{flag:X2}";
        return opBytes[0] switch
        {
            0x01 => "go to",
            0x02 => targetIndex == 0 ? "first time" : "later",
            0x03 => $"answer {targetIndex + 1}",
            0x04 => $"if {Flag(opBytes[1])} set",
            0x05 => $"if {Flag(opBytes[1])} not set",
            >= 0xC0 and <= 0xCF => $"if {Flag(0xF0 + (opBytes[0] & 15))} set",
            >= 0xD0 and <= 0xDF => $"if {Flag(0xF0 + (opBytes[0] & 15))} not set",
            0x45 => "if knows spell", 0x46 => "if has item", 0x47 => "if level ≥", 0x48 => "if HP ≥", 0x49 => "if MP ≥", 0x4F => "if key item",
            _ => "jump",
        };
    }

    // drawing

    public void Rebuild()
    {
        _canvas.Children.Clear();
        if (_panel?.Script is not { } script || script.Ops.Count == 0) return;
        var prevSelected = _selected?.First;
        _blocks = Split(script);
        Layout();
        foreach (var block in _blocks) block.Card = MakeCard(block, script);
        DrawEdges(script);
        foreach (var block in _blocks) { Canvas.SetLeft(block.Card, block.Pos.X); Canvas.SetTop(block.Card, block.Pos.Y); _canvas.Children.Add(block.Card); }
        double width = _blocks.Max(b => b.Pos.X + CardW) + 60, height = _blocks.Max(b => b.Pos.Y + CardHeight(b)) + 60;
        _canvas.Width = width; _canvas.Height = height;
        _selected = prevSelected != null ? _blocks.FirstOrDefault(b => b.First == prevSelected) ?? BlockOf(prevSelected) : null;
        Highlight();
        BuildMinimap(width, height);
    }

    private static double CardHeight(FlowBlock block) => 30 + 16 * Math.Min(block.End - block.Start, 7) + (block.End - block.Start > 7 ? 16 : 0) + 8;

    private void Layout()
    {
        var script = _panel!.Script;
        // layers: breadth-first from the first block, along jumps and fall-through
        var queue = new Queue<FlowBlock>();
        if (_blocks.Count > 0) { _blocks[0].Layer = 0; queue.Enqueue(_blocks[0]); }
        while (queue.Count > 0)
        {
            var block = queue.Dequeue();
            foreach (var next in Successors(block, script))
                if (next.Layer < 0) { next.Layer = block.Layer + 1; queue.Enqueue(next); }
        }
        int maxLayer = _blocks.Max(b => b.Layer);
        foreach (var block in _blocks.Where(b => b.Layer < 0)) block.Layer = maxLayer + 1;   // nothing reaches these
        foreach (var layer in _blocks.GroupBy(b => b.Layer))
        {
            double y = 20;
            foreach (var block in layer.OrderBy(b => b.Start))
            {
                block.Pos = _customPositions.TryGetValue(block.First, out var savedPos) ? savedPos : new Point(20 + block.Layer * (CardW + ColGap), y);
                y += CardHeight(block) + RowGap;
            }
        }
    }

    private IEnumerable<FlowBlock> Successors(FlowBlock block, EventScript script)
    {
        foreach (var target in block.Last.Targets)
            if (target.Op != null && _blocks.FirstOrDefault(x => x.First == target.Op) is { } targetBlock) yield return targetBlock;
        if (!block.Last.EndsFlow && block.End < script.Ops.Count && _blocks.FirstOrDefault(x => x.Start == block.End) is { } next) yield return next;
    }

    private Border MakeCard(FlowBlock block, EventScript script)
    {
        var stack = new StackPanel();
        string title = block.Kind switch { "dialogue" => "💬 Dialogue", "choice" => "❓ Choice", "branch" => "⑂ Branch", "end" => "■ End", _ => "▸ Steps" };
        stack.Children.Add(new TextBlock
        {
            Text = $"{title}   lines {block.Start + 1}{(block.End - block.Start > 1 ? $"-{block.End}" : "")}", Foreground = Brushes.Gold, FontWeight = FontWeights.SemiBold, FontSize = 11, Margin = new Thickness(0, 0, 0, 3),
        });
        for (int i = block.Start; i < Math.Min(block.End, block.Start + 7); i++)
        {
            var op = script.Ops[i];
            string text = op.IsText ? $"{EventScript.SpeakerName(op.Bytes)}: {op.Text.Replace("\n", " ")}" : script.Describe(op);
            stack.Children.Add(new TextBlock { Text = $"{i + 1,3}  {text}", Foreground = op.IsText ? Brushes.White : new SolidColorBrush(Color.FromRgb(0xD8, 0xD0, 0xF0)), FontSize = 11, TextTrimming = TextTrimming.CharacterEllipsis });
        }
        if (block.End - block.Start > 7) stack.Children.Add(new TextBlock { Text = $"     … {block.End - block.Start - 7} more line(s)", Foreground = Brushes.Gray, FontSize = 11 });
        var card = new Border
        {
            Width = CardW, MinHeight = CardHeight(block) - 8, Padding = new Thickness(8, 5, 8, 5), CornerRadius = new CornerRadius(block.Kind == "end" ? 14 : 4),
            Background = block.Kind switch { "dialogue" => DialogueBg, "choice" => ChoiceBg, "branch" => BranchBg, "end" => EndBg, _ => StepBg },
            BorderBrush = new SolidColorBrush(Color.FromArgb(120, 255, 255, 255)), BorderThickness = new Thickness(1), Child = stack, Tag = block, Cursor = Cursors.Hand,
            ToolTip = "Click to edit these lines below · drag to move · right-click for more",
        };
        card.MouseLeftButtonDown += Card_MouseDown;
        card.MouseRightButtonDown += Card_RightDown;
        return card;
    }

    private void DrawEdges(EventScript script)
    {
        foreach (var block in _blocks)
        {
            int targetIndex = 0;
            foreach (var target in block.Last.Targets)
            {
                var from = new Point(block.Pos.X + CardW, block.Pos.Y + 14 + 14 * targetIndex);
                var targetBlock = target.Op != null ? _blocks.FirstOrDefault(x => x.First == target.Op) : null;
                if (targetBlock != null) DrawArrow(from, new Point(targetBlock.Pos.X, targetBlock.Pos.Y + 14), JumpLine, JumpLabel(block.Last, targetIndex), false);
                else
                {
                    var stub = new Point(from.X + 50, from.Y);
                    DrawArrow(from, stub, JumpLine, JumpLabel(block.Last, targetIndex) + $" → {script.DescribeOutside(target.Absolute)}", false);
                }
                AddPort(block, targetIndex, from);
                targetIndex++;
            }
            if (!block.Last.EndsFlow && block.End < script.Ops.Count && _blocks.FirstOrDefault(x => x.Start == block.End) is { } next)
                DrawArrow(new Point(block.Pos.X + CardW / 2, block.Pos.Y + CardHeight(block) - 8), new Point(next.Pos.X + (next.Layer == block.Layer ? CardW / 2 : 0), next.Pos.Y + (next.Layer == block.Layer ? 0 : 14)),
                          NextLine, block.Last.Targets.Count > 0 && block.Last.Bytes[0] != 0x03 ? "otherwise" : "", next.Layer == block.Layer);
        }
    }

    private void DrawArrow(Point from, Point to, Brush brush, string label, bool vertical)
    {
        var figure = new PathFigure { StartPoint = from };
        if (vertical) figure.Segments.Add(new BezierSegment(new Point(from.X, from.Y + 20), new Point(to.X, to.Y - 20), to, true));
        else
        {
            double dx = Math.Max(40, Math.Abs(to.X - from.X) / 2);
            figure.Segments.Add(new BezierSegment(new Point(from.X + dx, from.Y), new Point(to.X - dx, to.Y), to, true));
        }
        var geometry = new PathGeometry(); geometry.Figures.Add(figure);
        _canvas.Children.Add(new Path { Data = geometry, Stroke = brush, StrokeThickness = 1.6, IsHitTestVisible = false });
        // arrow head
        var head = new Polygon { Fill = brush, IsHitTestVisible = false };
        head.Points = vertical
            ? new PointCollection { to, new(to.X - 5, to.Y - 8), new(to.X + 5, to.Y - 8) }
            : new PointCollection { to, new(to.X - 8, to.Y - 5), new(to.X - 8, to.Y + 5) };
        _canvas.Children.Add(head);
        if (label.Length > 0)
        {
            var labelText = new TextBlock { Text = label, Foreground = brush, FontSize = 10, Background = new SolidColorBrush(Color.FromArgb(170, 22, 14, 36)), IsHitTestVisible = false };
            Canvas.SetLeft(labelText, from.X + (vertical ? 6 : 8)); Canvas.SetTop(labelText, from.Y + (vertical ? 4 : -14));
            _canvas.Children.Add(labelText);
        }
    }

    // jump ports: drag one onto a card to retarget the jump

    private (FlowBlock Block, int Index)? _dragPort;
    private Line? _rubberLine;

    private void AddPort(FlowBlock block, int targetIndex, Point portPos)
    {
        var port = new Ellipse { Width = 10, Height = 10, Fill = JumpLine, Stroke = Brushes.Black, StrokeThickness = 1, Cursor = Cursors.Cross, ToolTip = "Drag onto a card to make this jump go there" };
        Canvas.SetLeft(port, portPos.X - 5); Canvas.SetTop(port, portPos.Y - 5);
        port.MouseLeftButtonDown += (_, e) =>
        {
            _dragPort = (block, targetIndex);
            _rubberLine = new Line { X1 = portPos.X, Y1 = portPos.Y, X2 = portPos.X, Y2 = portPos.Y, Stroke = JumpLine, StrokeThickness = 2, StrokeDashArray = new DoubleCollection { 3, 2 }, IsHitTestVisible = false };
            _canvas.Children.Add(_rubberLine);
            _canvas.CaptureMouse();
            e.Handled = true;
        };
        _canvas.Children.Add(port);
    }

    // card dragging and selection

    private FlowBlock? _dragCard;
    private Point _dragStart, _cardStart;
    private bool _moved;

    private void Card_MouseDown(object sender, MouseButtonEventArgs e)
    {
        if (sender is not Border { Tag: FlowBlock block }) return;
        _dragCard = block; _dragStart = e.GetPosition(_canvas); _cardStart = block.Pos; _moved = false;
        _canvas.CaptureMouse();
        e.Handled = true;
    }

    private void Canvas_MouseMove(object sender, MouseEventArgs e)
    {
        var mousePos = e.GetPosition(_canvas);
        if (_dragPort != null && _rubberLine != null) { _rubberLine.X2 = mousePos.X; _rubberLine.Y2 = mousePos.Y; return; }
        if (_dragCard == null || e.LeftButton != MouseButtonState.Pressed) return;
        var delta = mousePos - _dragStart;
        if (!_moved && Math.Abs(delta.X) + Math.Abs(delta.Y) < 5) return;
        _moved = true;
        _dragCard.Pos = new Point(Math.Max(0, _cardStart.X + delta.X), Math.Max(0, _cardStart.Y + delta.Y));
        Canvas.SetLeft(_dragCard.Card, _dragCard.Pos.X); Canvas.SetTop(_dragCard.Card, _dragCard.Pos.Y);
    }

    private void Canvas_MouseUp(object sender, MouseButtonEventArgs e)
    {
        _canvas.ReleaseMouseCapture();
        var mousePos = e.GetPosition(_canvas);
        if (_dragPort is { } port)
        {
            _dragPort = null;
            if (_rubberLine != null) _canvas.Children.Remove(_rubberLine);
            var target = _blocks.FirstOrDefault(b => mousePos.X >= b.Pos.X && mousePos.X <= b.Pos.X + CardW && mousePos.Y >= b.Pos.Y && mousePos.Y <= b.Pos.Y + CardHeight(b));
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
            if (_moved) { _customPositions[card.First] = card.Pos; Rebuild(); }
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

    // selects the card holding a line (coming from the Lines view)
    public void SelectLine(int index)
    {
        var block = _blocks.FirstOrDefault(x => index >= x.Start && index < x.End);
        if (block == null) return;
        _selected = block;
        Highlight();
        BlockSelected?.Invoke(block);
        block.Card.BringIntoView();
    }

    private void Highlight()
    {
        foreach (var block in _blocks)
        {
            if (block.Card == null) continue;
            block.Card.BorderBrush = block == _selected ? Brushes.Gold : new SolidColorBrush(Color.FromArgb(120, 255, 255, 255));
            block.Card.BorderThickness = new Thickness(block == _selected ? 2.5 : 1);
        }
    }

    // context menus

    private void Card_RightDown(object sender, MouseButtonEventArgs e)
    {
        if (sender is not Border { Tag: FlowBlock block } card || _panel == null) return;
        Select(block);
        var menu = new ContextMenu { PlacementTarget = card };
        void Add(string header, Action onClick) { var menuItem = new MenuItem { Header = header }; menuItem.Click += (_, _) => onClick(); menu.Items.Add(menuItem); }
        Add("＋ Insert a line after this card…", () => _panel.ShowInsertMenuAt(card, block.End));
        Add("⧉ Duplicate these lines", () => _panel.DuplicateLines(block.Start, block.End));
        Add("✕ Delete these lines", () => _panel.DeleteLines(block.Start, block.End));
        menu.IsOpen = true;
        e.Handled = true;
    }

    private void Canvas_RightDown(object sender, MouseButtonEventArgs e)
    {
        if (e.Handled || _panel == null) return;
        var menu = new ContextMenu { PlacementTarget = _canvas };
        var addItem = new MenuItem { Header = "＋ Add a line at the end…" };
        addItem.Click += (_, _) => _panel.ShowInsertMenuAt(_canvas, Math.Max(0, _panel.Script.Ops.Count - 1));
        menu.Items.Add(addItem);
        var layoutItem = new MenuItem { Header = "⟲ Auto-layout" };
        layoutItem.Click += (_, _) => AutoLayout();
        menu.Items.Add(layoutItem);
        menu.IsOpen = true;
    }

    // minimap

    private void BuildMinimap(double width, double height)
    {
        _miniCanvas.Children.Clear();
        _miniScale = Math.Min(_minimap.Width / width, _minimap.Height / height);
        foreach (var block in _blocks)
        {
            var miniCard = new Rectangle { Width = Math.Max(2, CardW * _miniScale), Height = Math.Max(2, CardHeight(block) * _miniScale), Fill = block.Card.Background, IsHitTestVisible = false };
            Canvas.SetLeft(miniCard, block.Pos.X * _miniScale); Canvas.SetTop(miniCard, block.Pos.Y * _miniScale);
            _miniCanvas.Children.Add(miniCard);
        }
        _miniCanvas.Children.Add(_miniView);
        UpdateMiniView();
    }

    private void UpdateMiniView()
    {
        double zoom = _zoom.ScaleX;
        _miniView.Width = Math.Max(4, _scroll.ViewportWidth / zoom * _miniScale);
        _miniView.Height = Math.Max(4, _scroll.ViewportHeight / zoom * _miniScale);
        Canvas.SetLeft(_miniView, _scroll.HorizontalOffset / zoom * _miniScale);
        Canvas.SetTop(_miniView, _scroll.VerticalOffset / zoom * _miniScale);
    }

    private void Minimap_Click(object sender, MouseButtonEventArgs e)
    {
        var clickPos = e.GetPosition(_miniCanvas);
        double zoom = _zoom.ScaleX;
        _scroll.ScrollToHorizontalOffset(clickPos.X / _miniScale * zoom - _scroll.ViewportWidth / 2);
        _scroll.ScrollToVerticalOffset(clickPos.Y / _miniScale * zoom - _scroll.ViewportHeight / 2);
        e.Handled = true;
    }
}
