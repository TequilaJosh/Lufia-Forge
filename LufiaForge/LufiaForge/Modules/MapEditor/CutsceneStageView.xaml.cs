using CommunityToolkit.Mvvm.ComponentModel;
using LufiaForge.Core;
using LufiaForge.Core.Maps;
using System.Collections.ObjectModel;
using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Shapes;
using System.Windows.Threading;
using Brush = System.Windows.Media.Brush;
using MouseEventArgs = System.Windows.Input.MouseEventArgs;
using KeyEventArgs = System.Windows.Input.KeyEventArgs;
using ContextMenu = System.Windows.Controls.ContextMenu;
using MenuItem = System.Windows.Controls.MenuItem;
using Brushes = System.Windows.Media.Brushes;
using Color = System.Windows.Media.Color;
using Image = System.Windows.Controls.Image;
using Point = System.Windows.Point;
using Rectangle = System.Windows.Shapes.Rectangle;

namespace LufiaForge.Modules.MapEditor;

/// <summary>One step of the path being edited.</summary>
public partial class StepVm : ObservableObject
{
    private readonly CutsceneStageView _owner;
    public MoveStep Step { get; }
    public int Number { get; set; }
    public StepVm(CutsceneStageView owner, MoveStep step) { _owner = owner; Step = step; }

    public static IReadOnlyList<string> Directions { get; } = EventCommands.Directions;
    public static IReadOnlyList<string> Speeds { get; } = EventCommands.Choices(ParamKind.ScrollSpeed, null!)!.Select(c => c.Label).ToList();
    public static IReadOnlyList<string> Facings { get; } = new[] { "the way it walks", "right", "left", "down", "up", "don't turn" };

    public string Tiles
    {
        get => Step.Tiles.ToString();
        set { if (int.TryParse(value, out int t) && t is >= 1 and <= 255) { Step.Tiles = t; _owner.PathEdited(); } }
    }
    public int Direction { get => Step.Direction; set { if (value >= 0) { Step.Direction = value; _owner.PathEdited(); } } }
    public int Speed { get => Step.Speed; set { if (value >= 0) { Step.Speed = value; _owner.PathEdited(); } } }
    public int Facing { get => Math.Min(Step.Facing, 5); set { if (value >= 0) { Step.Facing = value; _owner.PathEdited(); } } }
}

/// <summary>
/// The cutscene stage: the map with every character where it stands after the selected line, the walks that line starts,
/// the camera frame and the dialogue. Clicks set positions of the selected command; the path editor edits the map's
/// movement paths. Play animates the event from the selected line.
/// </summary>
public partial class CutsceneStageView : UserControl
{
    private RomBuffer? _rom;
    private EventEditorPanel? _panel;
    private LufiaMap? _map;
    private int _mapId = -1;
    private List<StageState> _states = new();
    private StageState? _initial;
    private int _line = -1;
    private EventScript? _boundScript;
    private int _startX, _startY;
    private bool _startSet;
    private bool _pickStart, _pickPathStart;
    /// <summary>Character picked on the stage (actor id), or -1. A click on a tile then offers walk / appear / face.</summary>
    private int _picked = -1;
    private bool _pathsDirty;
    private readonly Dictionary<int, BitmapSource?> _sprites = new();
    private readonly ObservableCollection<StepVm> _steps = new();
    private bool _filling;

    public CutsceneStageView()
    {
        InitializeComponent();
        // Ctrl + wheel zooms around the mouse, middle button drags the stage
        LufiaForge.Modules.Common.MapNavigation.Attach(Scroller, () => Zoom.ScaleX, z => ZoomSlider.Value = z, ZoomSlider.Minimum, ZoomSlider.Maximum);
        foreach (var p in CutsceneStage.Parties) PartyBox.Items.Add(p.Name);
        StepList.ItemsSource = _steps;
        _timer = new DispatcherTimer(DispatcherPriority.Render) { Interval = TimeSpan.FromMilliseconds(1000.0 / 60) };
        _timer.Tick += (_, _) => Tick();
    }

    /// <summary>Show the event of the panel (called when an event is loaded or edited).</summary>
    public void Bind(RomBuffer rom, EventEditorPanel panel)
    {
        _rom = rom; _panel = panel;
        var script = panel.Script;
        if (script.MapId != _mapId || _map == null)
        {
            if (_pathsDirty && _map != null &&
                MessageBox.Show("The paths of the previous map were changed but not written to the ROM. Write them now?", "Paths",
                    MessageBoxButton.YesNo, MessageBoxImage.Question) == MessageBoxResult.Yes) SavePaths();
            _pathsDirty = false;
            _mapId = script.MapId;
            try
            {
                _map = LufiaMap.Load(rom, _mapId);
                var tiles = MapTileset.ForMap(rom, _map);
                var px = tiles.RenderMap(_map);
                int w = _map.Width * 16, h = _map.Height * 16;
                var bmp = new WriteableBitmap(w, h, 96, 96, PixelFormats.Bgra32, null);
                bmp.WritePixels(new Int32Rect(0, 0, w, h), px, w * 4, 0);
                MapImage.Source = bmp;
                Overlay.Width = w; Overlay.Height = h;
            }
            catch (Exception ex)
            {
                _map = null; MapImage.Source = null;
                StageInfo.Text = "This map can't be shown: " + ex.Message;
                return;
            }
            _filling = true;
            PartyBox.SelectedIndex = CutsceneStage.DefaultParty(_mapId);
            _filling = false;
            _startSet = false;
        }
        if (!_startSet) (_startX, _startY) = CutsceneStage.DefaultStart(_map!, script.Event);
        if (!ReferenceEquals(script, _boundScript)) { _line = -1; _boundScript = script; }
        Resimulate();
    }

    private StageParty Party => CutsceneStage.Parties[Math.Max(0, PartyBox.SelectedIndex)];

    /// <summary>Recompute the stage after an edit.</summary>
    public void Resimulate()
    {
        if (_map == null || _panel == null) return;
        _initial = CutsceneStage.Initial(_map, Party, _startX, _startY);
        _states = CutsceneStage.Simulate(_map, _panel.Script, Party, _startX, _startY);
        bool first = _line < 0;
        ShowLine(_line < 0 ? 0 : Math.Min(_line, _states.Count - 1), scroll: first);
    }

    /// <summary>Show the stage after line <paramref name="index"/>.</summary>
    public void ShowLine(int index, bool scroll = true)
    {
        if (_map == null) return;
        _line = index;
        var st = index >= 0 && index < _states.Count ? _states[index] : _initial;
        if (st == null) return;
        Draw(st, null);
        ShowText(st);
        UpdatePathPanel();
        if (scroll) ScrollTo(st.CameraX, st.CameraY);
        var op = SelectedOp;
        string hint = op == null || op.IsText ? "" : op.Bytes[0] switch
        {
            >= 0x20 and <= 0x27 => " Click the stage to move this character there.",
            0x09 => " Click the stage to point the camera there.",
            0x14 or 0x15 => " Edit the path below; ✎ Draw steps lets you click its route.",
            0x55 or 0x4B => " Click the stage to set X and Y.",
            _ => "",
        };
        StageInfo.Text = $"After line {index + 1}: " + Summary(st) + hint;
    }

    private StageState? CurrentState => _line >= 0 && _line < _states.Count ? _states[_line] : _initial;

    private ScriptOp? SelectedOp => _panel != null && _line >= 0 && _line < _panel.Script.Ops.Count ? _panel.Script.Ops[_line] : null;

    private static string Summary(StageState st)
    {
        var parts = new List<string>();
        if (st.Black) parts.Add("screen black");
        if (st.Music >= 0) parts.Add(st.Music == 0xFF ? "music stopped" : $"music {st.Music:X2}");
        if (st.Shaking) parts.Add("screen shaking");
        parts.Add($"camera on ({st.CameraX}, {st.CameraY})");
        if (st.Leaves != null) parts.Add("the event " + st.Leaves);
        return string.Join(" · ", parts) + ".";
    }

    private void ShowText(StageState st)
    {
        if (st.Text == null) { TextBox.Visibility = Visibility.Collapsed; return; }
        TextBox.Visibility = Visibility.Visible;
        SpeakerText.Text = st.Speaker < 0 ? "" : st.Actors.TryGetValue(st.Speaker, out var a) ? a.Name : EventCommands.Actor(st.Speaker);
        DialogueText.Text = st.Text;
    }

    // ── drawing ─────────────────────────────────────────────────────────────

    private BitmapSource? Sprite(int id)
    {
        if (_rom == null) return null;
        if (_sprites.TryGetValue(id, out var s)) return s;
        if (NpcSprites.Render(_rom, id) is var (px, w, h))
        {
            var bmp = BitmapSource.Create(w, h, 96, 96, PixelFormats.Bgra32, null, px, w * 4);
            bmp.Freeze();
            s = bmp;
        }
        _sprites[id] = s;
        return s;
    }

    private static readonly Brush WalkBrush = new SolidColorBrush(Color.FromArgb(230, 255, 215, 0));
    private static readonly Brush OtherPathBrush = new SolidColorBrush(Color.FromArgb(150, 120, 200, 255));
    private static readonly Brush CameraBrush = new SolidColorBrush(Color.FromArgb(220, 255, 255, 255));
    private static readonly Brush SelectedBrush = new SolidColorBrush(Color.FromArgb(255, 255, 80, 80));

    /// <param name="moving">Pixel positions of actors in mid-walk (playback), or null.</param>
    private void Draw(StageState st, Dictionary<int, Point>? moving)
    {
        Overlay.Children.Clear();
        if (_map == null) return;

        if (ShowAllPathsBox.IsChecked == true)
            for (int i = 0; i < _map.Paths.Count; i++) DrawWalk(_map.Paths[i].Points(), OtherPathBrush, $"{i + 1}", 1.5);
        if (moving == null)
            foreach (var w in st.Walks) DrawWalk(w.Points, WalkBrush, w.PathNumber > 0 ? $"path {w.PathNumber}" : "", 3);

        int selActor = SelectedActor();
        // lower on screen in front; on a shared tile the leader is drawn last (in front of its followers)
        foreach (var a in st.Actors.Values.OrderBy(a => a.Y).ThenByDescending(a => a.Id))
        {
            if (!a.Visible && moving?.ContainsKey(a.Id) != true) continue;
            var pos = moving != null && moving.TryGetValue(a.Id, out var p) ? p : new Point(a.X * 16, a.Y * 16);
            var img = Sprite(a.Sprite);
            double w = img?.PixelWidth ?? 16, h = img?.PixelHeight ?? 24;
            double left = pos.X + 8 - w / 2, top = pos.Y + 16 - h;
            if (img != null)
            {
                var el = new Image { Source = img, Width = w, Height = h, Opacity = a.Blinking ? 0.55 : 1, IsHitTestVisible = false };
                RenderOptions.SetBitmapScalingMode(el, BitmapScalingMode.NearestNeighbor);
                Canvas.SetLeft(el, left); Canvas.SetTop(el, top);
                Overlay.Children.Add(el);
            }
            if (a.Id == _picked)
            {
                var ring = new Ellipse { Width = 24, Height = 12, Stroke = WalkBrush, StrokeThickness = 2, IsHitTestVisible = false };
                Canvas.SetLeft(ring, pos.X - 4); Canvas.SetTop(ring, pos.Y + 10);
                Overlay.Children.Add(ring);
            }
            if (a.Id == selActor)
            {
                var box = new Rectangle { Width = 16, Height = 16, Stroke = SelectedBrush, StrokeThickness = 1.5, IsHitTestVisible = false };
                Canvas.SetLeft(box, pos.X); Canvas.SetTop(box, pos.Y);
                Overlay.Children.Add(box);
            }
            // facing marker
            var tri = new Polygon { Fill = Brushes.White, Opacity = 0.85, IsHitTestVisible = false };
            double cx = pos.X + 8, cy = pos.Y + 12;
            tri.Points = a.Facing switch
            {
                0 => new PointCollection { new(cx + 9, cy), new(cx + 5, cy - 3), new(cx + 5, cy + 3) },
                1 => new PointCollection { new(cx - 9, cy), new(cx - 5, cy - 3), new(cx - 5, cy + 3) },
                2 => new PointCollection { new(cx, cy + 7), new(cx - 3, cy + 3), new(cx + 3, cy + 3) },
                _ => new PointCollection { new(cx, cy - 19), new(cx - 3, cy - 15), new(cx + 3, cy - 15) },
            };
            Overlay.Children.Add(tri);
            var label = new TextBlock
            {
                Text = a.Id >= 7 ? $"{a.Id - 7}" : a.Name, Foreground = Brushes.White, FontSize = 8, IsHitTestVisible = false,
                Background = new SolidColorBrush(Color.FromArgb(150, 20, 10, 40)),
            };
            Canvas.SetLeft(label, pos.X); Canvas.SetTop(label, pos.Y + 16);
            // one label per tile is enough when the party stands together
            if (a.Id is >= 2 and <= 5 && st.Actors.TryGetValue(1, out var lead) && lead.X == a.X && lead.Y == a.Y && moving?.ContainsKey(a.Id) != true) continue;
            Overlay.Children.Add(label);
        }

        if (ShowCameraBox.IsChecked == true)
        {
            var cam = new Rectangle
            {
                Width = CutsceneStage.ScreenTilesX * 16, Height = CutsceneStage.ScreenTilesY * 16,
                Stroke = CameraBrush, StrokeThickness = 2, StrokeDashArray = new DoubleCollection { 4, 3 }, IsHitTestVisible = false,
                Fill = st.Black ? new SolidColorBrush(Color.FromArgb(150, 0, 0, 0)) : null,
            };
            Point c = moving != null && moving.TryGetValue(-1, out var cp) ? cp : new Point(st.CameraX * 16, st.CameraY * 16);
            Canvas.SetLeft(cam, c.X - 7 * 16); Canvas.SetTop(cam, c.Y - 7 * 16);
            Overlay.Children.Add(cam);
        }
        else if (st.Black)
        {
            var dark = new Rectangle { Width = Overlay.Width, Height = Overlay.Height, Fill = new SolidColorBrush(Color.FromArgb(120, 0, 0, 0)), IsHitTestVisible = false };
            Overlay.Children.Add(dark);
        }
    }

    private void DrawWalk(IReadOnlyList<(int X, int Y)> pts, Brush brush, string label, double thickness)
    {
        if (pts.Count == 0) return;
        var line = new Polyline { Stroke = brush, StrokeThickness = thickness, IsHitTestVisible = false, StrokeLineJoin = PenLineJoin.Round };
        foreach (var (x, y) in pts) line.Points.Add(new Point(x * 16 + 8, y * 16 + 8));
        Overlay.Children.Add(line);
        var start = new Ellipse { Width = 6, Height = 6, Fill = brush, IsHitTestVisible = false };
        Canvas.SetLeft(start, pts[0].X * 16 + 5); Canvas.SetTop(start, pts[0].Y * 16 + 5);
        Overlay.Children.Add(start);
        var end = pts[^1];
        var head = new Rectangle { Width = 8, Height = 8, Stroke = brush, StrokeThickness = 2, IsHitTestVisible = false };
        Canvas.SetLeft(head, end.X * 16 + 4); Canvas.SetTop(head, end.Y * 16 + 4);
        Overlay.Children.Add(head);
        if (label.Length > 0)
        {
            var t = new TextBlock { Text = label, Foreground = brush, FontSize = 9, FontWeight = FontWeights.Bold, IsHitTestVisible = false };
            Canvas.SetLeft(t, pts[0].X * 16 + 10); Canvas.SetTop(t, pts[0].Y * 16 - 4);
            Overlay.Children.Add(t);
        }
    }

    /// <summary>The actor the selected command acts on, or -1.</summary>
    private int SelectedActor()
    {
        var op = SelectedOp;
        if (op == null || op.IsText || op.Bytes.Length == 0) return -1;
        var b = op.Bytes;
        try
        {
            return b[0] switch
            {
                >= 0x20 and <= 0x27 => CutsceneStage.ActorOf(ParamDef.Who(1, 0x7F).Get(b)),
                >= 0x28 and <= 0x2F or 0x38 or 0x39 or >= 0x5C and <= 0x5F => CutsceneStage.ActorOf(ParamDef.Who(1).Get(b)),
                0x14 or 0x15 => CutsceneStage.ActorOf(ParamDef.Who(2).Get(b)),
                >= 0xB0 and <= 0xBF => (b[0] & 3) + 1,
                _ => -1,
            };
        }
        catch { return -1; }
    }

    private void ScrollTo(int tx, int ty)
    {
        // wait until the stage has its size (right after loading an event it has none yet)
        Dispatcher.BeginInvoke(() => ScrollNow(tx, ty), DispatcherPriority.Loaded);
    }

    private void ScrollNow(int tx, int ty)
    {
        double z = Zoom.ScaleX;
        Scroller.ScrollToHorizontalOffset(Math.Max(0, tx * 16 * z - Scroller.ViewportWidth / 2));
        Scroller.ScrollToVerticalOffset(Math.Max(0, ty * 16 * z - Scroller.ViewportHeight / 2));
    }

    // ── clicks ──────────────────────────────────────────────────────────────

    private (int X, int Y) TileAt(MouseEventArgs e)
    {
        var p = e.GetPosition(Overlay);
        return ((int)(p.X / 16), (int)(p.Y / 16));
    }

    private void Stage_MouseMove(object sender, MouseEventArgs e)
    {
        if (_map == null) return;
        var (x, y) = TileAt(e);
        Overlay.ToolTip = $"Tile ({x}, {y})";
    }

    private void Stage_MouseDown(object sender, MouseButtonEventArgs e)
    {
        if (_map == null || _panel == null) return;
        var (x, y) = TileAt(e);
        if (_pickStart)
        {
            _pickStart = false; _startSet = true; _startX = x; _startY = y;
            Resimulate();
            return;
        }
        if (_pickPathStart && CurrentPath is { } ps)
        {
            _pickPathStart = false;
            ps.StartX = x; ps.StartY = y;
            PathEdited();
            return;
        }
        if (DrawToggle.IsChecked == true && CurrentPath is { } path)
        {
            var end = path.Points()[^1];
            int speed = path.Steps.Count > 0 ? path.Steps[^1].Speed : 4;
            if (x != end.X) path.Steps.Add(new MoveStep { Direction = x > end.X ? 0 : 1, Tiles = Math.Abs(x - end.X), Speed = speed });
            if (y != end.Y) path.Steps.Add(new MoveStep { Direction = y > end.Y ? 2 : 3, Tiles = Math.Abs(y - end.Y), Speed = speed });
            PathEdited();
            return;
        }
        // a character under the cursor: pick it
        var st = CurrentState;
        var hit = st?.Actors.Values.Where(a => a.Visible && a.X == x && (a.Y == y || a.Y == y + 1))
                     .OrderBy(a => a.Y == y ? 0 : 1).ThenBy(a => a.Id).FirstOrDefault();
        if (hit != null && hit.Id != _picked)
        {
            _picked = hit.Id;
            ShowLine(_line, scroll: false);
            StageInfo.Text = $"{hit.Name} picked. Click a tile: walk there, appear there, face that way or point the camera there. " +
                             "Click it again (or Esc) to let go.";
            return;
        }
        if (hit != null && hit.Id == _picked) { _picked = -1; ShowLine(_line, scroll: false); return; }

        // the selected line has a position: move it here (unless a character is picked)
        var op = SelectedOp;
        if (_picked < 0 && op != null && !op.IsText && op.Bytes.Length > 0 && EventCommands.Find(op.Bytes[0]) is { } def &&
            def.Params.FirstOrDefault(p => p.Label == "X") is { } px && def.Params.FirstOrDefault(p => p.Label == "Y") is { } py)
        {
            var bytes = (byte[])op.Bytes.Clone();
            px.Set(bytes, x); py.Set(bytes, y);
            _panel.SetLineBytes(_line, bytes);
            return;
        }
        ShowActionMenu(x, y);
    }

    private void Stage_RightDown(object sender, MouseButtonEventArgs e)
    {
        if (_map == null) return;
        var (x, y) = TileAt(e);
        ShowActionMenu(x, y);
        e.Handled = true;
    }

    protected override void OnPreviewKeyDown(KeyEventArgs e)
    {
        if (e.Key == Key.Escape && _picked >= 0) { _picked = -1; ShowLine(_line, scroll: false); e.Handled = true; }
        base.OnPreviewKeyDown(e);
    }

    /// <summary>What can happen at a tile: the picked character (or any character) walks / appears / turns; the camera moves.</summary>
    private void ShowActionMenu(int x, int y)
    {
        var st = CurrentState;
        if (st == null || _panel == null) return;
        var menu = new ContextMenu { PlacementTarget = Overlay };
        menu.Items.Add(new MenuItem { Header = $"Tile ({x}, {y}) — added after line {_line + 1}", IsEnabled = false });
        void Add(string header, Action act) { var mi = new MenuItem { Header = header }; mi.Click += (_, _) => act(); menu.Items.Add(mi); }

        IEnumerable<StageActor> who = _picked >= 0 && st.Actors.TryGetValue(_picked, out var pa) ? new[] { pa }
                                    : st.Actors.Values.Where(a => a.Id is 1 or >= 7).OrderBy(a => a.Id);
        foreach (var a in who)
        {
            var actor = a;
            if (_picked < 0)
            {
                var sub = new MenuItem { Header = actor.Name + (actor.Id >= 7 ? $" (character {actor.Id - 7})" : "") };
                void SubAdd(string h, Action act) { var mi = new MenuItem { Header = h }; mi.Click += (_, _) => act(); sub.Items.Add(mi); }
                SubAdd("🚶 Walk here", () => AddWalk(actor, x, y));
                SubAdd("✨ Appear here", () => AddPlace(actor, x, y));
                SubAdd("👀 Turn to face this tile", () => AddFace(actor, x, y));
                menu.Items.Add(sub);
            }
            else
            {
                Add($"🚶 {actor.Name} walks here", () => AddWalk(actor, x, y));
                Add($"✨ {actor.Name} appears here", () => AddPlace(actor, x, y));
                Add($"👀 {actor.Name} turns to face this tile", () => AddFace(actor, x, y));
            }
        }
        menu.Items.Add(new Separator());
        Add("🎥 Camera jumps here", () => Insert(new byte[] { 0x09, (byte)x, (byte)(x >> 8), (byte)y, (byte)(y >> 8) }));
        Add("📍 The party starts the event here", () => { _startSet = true; _startX = x; _startY = y; Resimulate(); });
        menu.IsOpen = true;
    }

    /// <summary>Opcode bit 0 and operand for a character: map characters use the even opcode with their number.</summary>
    private static (int Abs, int Operand) Who(StageActor a) => a.Id >= 7 ? (0, a.Id - 7) : (1, a.Id);

    private void Insert(byte[] bytes)
    {
        if (_panel == null) return;
        _panel.InsertCommand(_line, bytes);
    }

    private void AddPlace(StageActor a, int x, int y)
    {
        var (abs, n) = Who(a);
        Insert(new byte[] { (byte)(0x20 | ((a.Facing & 3) << 1) | abs), (byte)((n & 0x7F) | ((x >> 8 & 1) << 7)), (byte)x, (byte)y });
    }

    private void AddFace(StageActor a, int x, int y)
    {
        int dx = x - a.X, dy = y - a.Y;
        int dir = Math.Abs(dx) >= Math.Abs(dy) ? (dx >= 0 ? 0 : 1) : (dy >= 0 ? 2 : 3);
        var (abs, n) = Who(a);
        Insert(new byte[] { (byte)(0x28 | (dir << 1) | abs), (byte)n });
    }

    private void AddWalk(StageActor a, int x, int y)
    {
        if (_map == null) return;
        if (a.X == x && a.Y == y) { StageInfo.Text = $"{a.Name} is already there."; return; }
        if (_map.Paths.Count >= 255) { StageInfo.Text = "This map already has 255 paths."; return; }
        var path = new MovePath { StartX = a.X, StartY = a.Y };
        const int normal = 4;
        if (x != a.X) path.Steps.Add(new MoveStep { Direction = x > a.X ? 0 : 1, Tiles = Math.Abs(x - a.X), Speed = normal });
        if (y != a.Y) path.Steps.Add(new MoveStep { Direction = y > a.Y ? 2 : 3, Tiles = Math.Abs(y - a.Y), Speed = normal });
        _map.Paths.Add(path);
        _pathsDirty = true;
        var (abs, n) = Who(a);
        Insert(new byte[] { (byte)(0x14 | abs), (byte)_map.Paths.Count, (byte)n });
        StageInfo.Text = $"{a.Name} walks to ({x}, {y}) on new path {_map.Paths.Count} (sideways first, then up/down). " +
                         "Change the route or speed under the stage; ✔ Apply writes the walk too.";
    }

    /// <summary>True when walks were added or changed and not written yet.</summary>
    public bool HasPathChanges => _pathsDirty && _map != null;

    /// <summary>Write changed walks (called by Apply). Returns what was done, or null when nothing changed.</summary>
    public string? SavePathsWithEvent()
    {
        if (!HasPathChanges || _rom == null || _map == null || _panel == null) return null;
        bool expand = _rom.Length >= MapWriter.ExpandedSize;
        if (MapWriter.NeedsMoreSpace(_map, out _) && !expand)
        {
            if (!_panel.Host.ConfirmExpand("The map's walks no longer fit where its data was.")) throw new InvalidOperationException("The walks weren't written.");
            expand = true;
        }
        var result = MapWriter.Save(_rom, _map, allowExpand: expand);
        _pathsDirty = false;
        _panel.Host.EventSaved($"Map {_map.MapId:X2}: movement paths");
        _map = LufiaMap.Load(_rom, _map.MapId);
        return "Walks: " + result.Describe();
    }

    private void PickStart_Click(object sender, RoutedEventArgs e)
    {
        _pickStart = true;
        StageInfo.Text = "Click the tile where the party stands when the event begins.";
    }

    private void Party_Changed(object sender, SelectionChangedEventArgs e) { if (!_filling) Resimulate(); }
    private void Redraw_Click(object sender, RoutedEventArgs e) => ShowLine(_line, scroll: false);

    private void Zoom_Changed(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (Zoom == null) return;
        Zoom.ScaleX = Zoom.ScaleY = e.NewValue;
    }

    // ── path editor ─────────────────────────────────────────────────────────

    /// <summary>The path used by the selected line (commands 14/15), or null.</summary>
    private MovePath? CurrentPath
    {
        get
        {
            var op = SelectedOp;
            if (_map == null || op == null || op.IsText || op.Bytes.Length < 3 || op.Bytes[0] is not (0x14 or 0x15)) return null;
            int n = op.Bytes[1];
            return n >= 1 && n <= _map.Paths.Count ? _map.Paths[n - 1] : null;
        }
    }

    private void UpdatePathPanel()
    {
        var op = SelectedOp;
        bool isPath = _map != null && op != null && !op.IsText && op.Bytes.Length >= 3 && op.Bytes[0] is 0x14 or 0x15;
        PathPanel.Visibility = isPath ? Visibility.Visible : Visibility.Collapsed;
        if (!isPath) { DrawToggle.IsChecked = false; return; }
        _filling = true;
        PathBox.Items.Clear();
        for (int i = 0; i < _map!.Paths.Count; i++)
        {
            var p = _map.Paths[i];
            PathBox.Items.Add($"{i + 1}: from ({p.StartX}, {p.StartY}), {p.Steps.Count} step{(p.Steps.Count == 1 ? "" : "s")}");
        }
        int n = op!.Bytes[1];
        PathBox.SelectedIndex = n >= 1 && n <= _map.Paths.Count ? n - 1 : -1;
        var path = CurrentPath;
        PathHeader.Text = path == null ? $"Path {n} doesn't exist on this map" : $"Path {n} of this map";
        _steps.Clear();
        if (path != null)
        {
            WaitBox.IsChecked = !path.NoWait; RepeatBox.IsChecked = path.Repeat; CameraBox.IsChecked = path.CameraFollows; WalkStartBox.IsChecked = path.WalkToStart;
            StartXBox.Text = path.StartX.ToString(); StartYBox.Text = path.StartY.ToString();
            for (int i = 0; i < path.Steps.Count; i++) _steps.Add(new StepVm(this, path.Steps[i]) { Number = i + 1 });
            int users = UsersOf(n);
            PathStatus.Text = (users > 1 ? $"Path {n} is used by {users} lines of this map's events; changing it changes all of them. " : "") +
                              (_pathsDirty ? "Paths changed: write them to the ROM to keep them." : "");
        }
        _filling = false;
    }

    /// <summary>How many commands of the map's events use path n.</summary>
    private int UsersOf(int n)
    {
        if (_rom == null || _map == null) return 0;
        int count = 0;
        foreach (int ev in EventScript.PlausibleEvents(_rom, _map.MapId))
        {
            var s = EventScript.Load(_rom, _map.MapId, ev);
            if (s != null) count += s.Ops.Count(o => !o.IsText && o.Bytes.Length >= 3 && o.Bytes[0] is 0x14 or 0x15 && o.Bytes[1] == n);
        }
        return count;
    }

    /// <summary>A path value changed: redraw and remember to write the map.</summary>
    public void PathEdited()
    {
        if (_filling) return;
        _pathsDirty = true;
        int keep = _line;
        Resimulate();
        _line = keep;
    }

    private void PathBox_Changed(object sender, SelectionChangedEventArgs e)
    {
        if (_filling || _panel == null || PathBox.SelectedIndex < 0 || SelectedOp is not { } op) return;
        var bytes = (byte[])op.Bytes.Clone();
        bytes[1] = (byte)(PathBox.SelectedIndex + 1);
        _panel.SetLineBytes(_line, bytes);
    }

    private void NewPath_Click(object sender, RoutedEventArgs e)
    {
        if (_map == null || _panel == null || SelectedOp is not { } op) return;
        var st = _line > 0 && _line - 1 < _states.Count ? _states[_line - 1] : _initial;
        int actor = SelectedActor();
        var at = st != null && st.Actors.TryGetValue(actor, out var a) ? (a.X, a.Y) : (_startX, _startY);
        _map.Paths.Add(new MovePath { StartX = at.Item1, StartY = at.Item2 });
        _pathsDirty = true;
        var bytes = (byte[])op.Bytes.Clone();
        bytes[1] = (byte)_map.Paths.Count;
        _panel.SetLineBytes(_line, bytes);
        DrawToggle.IsChecked = true;
        StageInfo.Text = "New path: click tiles on the stage to add its steps.";
    }

    private void PathFlags_Click(object sender, RoutedEventArgs e)
    {
        if (CurrentPath is not { } p) return;
        p.NoWait = WaitBox.IsChecked != true; p.Repeat = RepeatBox.IsChecked == true;
        p.CameraFollows = CameraBox.IsChecked == true; p.WalkToStart = WalkStartBox.IsChecked == true;
        PathEdited();
    }

    private void Start_LostFocus(object sender, RoutedEventArgs e)
    {
        if (_filling || CurrentPath is not { } p) return;
        if (int.TryParse(StartXBox.Text, NumberStyles.Integer, CultureInfo.InvariantCulture, out int x) && x is >= 0 and <= 0xFFFF &&
            int.TryParse(StartYBox.Text, NumberStyles.Integer, CultureInfo.InvariantCulture, out int y) && y is >= 0 and <= 0xFFFF &&
            (x != p.StartX || y != p.StartY))
        { p.StartX = x; p.StartY = y; PathEdited(); }
    }

    private void PickPathStart_Click(object sender, RoutedEventArgs e)
    {
        _pickPathStart = true;
        StageInfo.Text = "Click the tile where the path starts.";
    }

    private void AddStep_Click(object sender, RoutedEventArgs e)
    {
        if (CurrentPath is not { } p) return;
        p.Steps.Add(new MoveStep { Direction = 2, Tiles = 1, Speed = p.Steps.Count > 0 ? p.Steps[^1].Speed : 4 });
        PathEdited();
    }

    private void ClearSteps_Click(object sender, RoutedEventArgs e)
    {
        if (CurrentPath is not { } p) return;
        p.Steps.Clear();
        PathEdited();
    }

    private void RemoveStep_Click(object sender, RoutedEventArgs e)
    {
        if (CurrentPath is not { } p || sender is not FrameworkElement { DataContext: StepVm s }) return;
        p.Steps.Remove(s.Step);
        PathEdited();
    }

    private void SavePaths_Click(object sender, RoutedEventArgs e) => SavePaths();

    private void SavePaths()
    {
        if (_rom == null || _map == null || _panel == null) return;
        try
        {
            bool expand = _rom.Length >= MapWriter.ExpandedSize;
            if (MapWriter.NeedsMoreSpace(_map, out _) && !expand)
            {
                if (!_panel.Host.ConfirmExpand("The map's data no longer fits where it was.")) { PathStatus.Text = "Not written."; return; }
                expand = true;
            }
            var result = MapWriter.Save(_rom, _map, allowExpand: expand);
            _pathsDirty = false;
            _panel.Host.EventSaved($"Map {_map.MapId:X2}: movement paths");
            PathStatus.Text = "✔ Paths written to the ROM. Use Save ROM to write a dated copy.";
            _panel.Host.ConfirmWritten($"Paths of map {_map.MapId:X2} written to the ROM", result.Describe());
            _map = LufiaMap.Load(_rom, _map.MapId);
            Resimulate();
        }
        catch (Exception ex) { PathStatus.Text = "Not written: " + ex.Message; }
    }

    // ── playback ────────────────────────────────────────────────────────────

    private readonly DispatcherTimer _timer;
    private int _playLine, _lineFrames, _frame;
    private readonly List<(int Actor, List<Point> Pts, int FramesPerTile, int Start)> _anims = new();
    private Point? _camFrom, _camTo;
    private int _camStart, _camFrames;

    private void Play_Click(object sender, RoutedEventArgs e)
    {
        if (_states.Count == 0) return;
        _playLine = Math.Max(0, _line);
        _frame = 0; _anims.Clear();
        StartLine();
        _timer.Start();
        PlayButton.IsEnabled = false;
    }

    private void Stop_Click(object sender, RoutedEventArgs e) => StopPlayback();

    private void StopPlayback()
    {
        _timer.Stop();
        PlayButton.IsEnabled = true;
        _anims.Clear();
        ShowLine(Math.Min(_playLine, _states.Count - 1), scroll: false);
        _panel?.HighlightLine(Math.Min(_playLine, _states.Count - 1));
    }

    private void StartLine()
    {
        var st = _states[_playLine];
        var before = _playLine > 0 ? _states[_playLine - 1] : _initial!;
        _panel?.HighlightLine(_playLine);
        _line = _playLine;
        int blocking = 0;
        foreach (var w in st.Walks)
        {
            var pts = w.Points.Select(p => new Point(p.X * 16, p.Y * 16)).ToList();
            int fpt = CutsceneStage.FramesPerTile(w.Speed);
            _anims.RemoveAll(a => a.Actor == w.Actor);
            _anims.Add((w.Actor, pts, fpt, _frame));
            int tiles = 0;
            for (int i = 1; i < w.Points.Count; i++) tiles += Math.Abs(w.Points[i].X - w.Points[i - 1].X) + Math.Abs(w.Points[i].Y - w.Points[i - 1].Y);
            if (w.Waits) blocking = Math.Max(blocking, tiles * fpt);
        }
        _camFrom = new Point(before.CameraX * 16, before.CameraY * 16);
        _camTo = new Point(st.CameraX * 16, st.CameraY * 16);
        _camStart = _frame;
        _camFrames = Math.Max(1, st.WaitFrames > 0 && (before.CameraX != st.CameraX || before.CameraY != st.CameraY) ? st.WaitFrames : blocking);
        int text = st.Text != null ? Math.Clamp(st.Text.Length * 3, 90, 360) : 0;
        _lineFrames = Math.Max(1, Math.Max(blocking, st.WaitFrames) + text);
        ShowText(st);
    }

    private void Tick()
    {
        _frame++;
        if (--_lineFrames <= 0)
        {
            if (_states[_playLine].Leaves != null || _playLine + 1 >= _states.Count) { StopPlayback(); return; }
            _playLine++;
            StartLine();
        }
        var st = _states[_playLine];
        var moving = new Dictionary<int, Point>();
        foreach (var (actor, pts, fpt, start) in _anims.ToList())
        {
            double t = (_frame - start) / (double)fpt;   // tiles walked so far
            double done = 0;
            Point pos = pts[^1];
            bool finished = true;
            for (int i = 1; i < pts.Count; i++)
            {
                double len = (Math.Abs(pts[i].X - pts[i - 1].X) + Math.Abs(pts[i].Y - pts[i - 1].Y)) / 16;
                if (t < done + len)
                {
                    double f = len == 0 ? 1 : (t - done) / len;
                    pos = new Point(pts[i - 1].X + (pts[i].X - pts[i - 1].X) * f, pts[i - 1].Y + (pts[i].Y - pts[i - 1].Y) * f);
                    finished = false;
                    break;
                }
                done += len;
            }
            if (finished) _anims.RemoveAll(a => a.Actor == actor && a.Start == start);
            else moving[actor] = pos;
        }
        if (_camFrom is { } cf && _camTo is { } ct)
        {
            double f = Math.Min(1, (_frame - _camStart) / (double)_camFrames);
            moving[-1] = new Point(cf.X + (ct.X - cf.X) * f, cf.Y + (ct.Y - cf.Y) * f);
        }
        Draw(st, moving);
        var cam = moving[-1];
        double z = Zoom.ScaleX;
        Scroller.ScrollToHorizontalOffset(Math.Max(0, cam.X * z - Scroller.ViewportWidth / 2));
        Scroller.ScrollToVerticalOffset(Math.Max(0, cam.Y * z - Scroller.ViewportHeight / 2));
        StageInfo.Text = $"Playing line {_playLine + 1} of {_states.Count}…";
    }
}
