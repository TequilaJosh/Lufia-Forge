using CommunityToolkit.Mvvm.ComponentModel;
using LufiaForge.Core;
using LufiaForge.Core.Audio;
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

// one step of the path being edited
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
        set { if (int.TryParse(value, out int tiles) && tiles is >= 1 and <= 255) { Step.Tiles = tiles; _owner.PathEdited(); } }
    }
    public int Direction { get => Step.Direction; set { if (value >= 0) { Step.Direction = value; _owner.PathEdited(); } } }
    public int Speed { get => Step.Speed; set { if (value >= 0) { Step.Speed = value; _owner.PathEdited(); } } }
    public int Facing { get => Math.Min(Step.Facing, 5); set { if (value >= 0) { Step.Facing = value; _owner.PathEdited(); } } }
}

// the cutscene stage: the map with every character where it stands after the selected line, the walks that line starts,
// the camera frame and the dialogue. clicks set the selected command's positions, the path editor edits the map's
// movement paths, and Play animates the event from the selected line
public partial class CutsceneStageView : UserControl
{
    private RomBuffer? _rom;
    private EventEditorPanel? _panel;
    private LufiaMap? _map;
    // draws the game's screen (both map layers, sprites, narration, fades) for playback and the playhead
    private StageScreen? _screen;
    private int _mapId = -1;
    private List<StageState> _states = new();
    private StageState? _initial;
    private int _line = -1;
    private EventScript? _boundScript;
    private int _startX, _startY;
    private bool _startSet;
    private bool _pickStart, _pickPathStart;
    // character picked on the stage (actor id), or -1. a click on a tile then offers walk / appear / face
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
        foreach (var party in CutsceneStage.Parties) PartyBox.Items.Add(party.Name);
        StepList.ItemsSource = _steps;
        PathSplitter.DragCompleted += (_, e) =>
        {
            if (LufiaForge.Modules.Common.LayoutMemory.Moved(e))
                LufiaForge.Modules.Common.LayoutMemory.Set("stage.walkeditor", LufiaForge.Modules.Common.LayoutMemory.Format(new GridLength(PathRow.ActualHeight)));
        };
        PathSplitter.MouseDoubleClick += (_, _) => { LufiaForge.Modules.Common.LayoutMemory.Set("stage.walkeditor", null); PathRow.Height = GridLength.Auto; };
        SoundBox.IsChecked = SoundOn;
        if (!SpcNative.Available) { SoundBox.IsEnabled = false; SoundBox.ToolTip = "lufia_spc.dll is missing, so the music can't be played"; }
        _timer = new DispatcherTimer(DispatcherPriority.Render) { Interval = TimeSpan.FromMilliseconds(1000.0 / 60) };
        _timer.Tick += (_, _) => Tick();
    }

    // shows the panel's event (called when an event is loaded or edited)
    public void Bind(RomBuffer rom, EventEditorPanel panel)
    {
        _rom = rom; _panel = panel;
        MusicSync.UseRom(rom);   // sync points for songs we haven't measured come from the game's sound driver
        var script = panel.Script;
        // same map but another tab, Undo or a patch changed it in the meantime: read it again (walks we haven't
        // written yet get put into the newer map when they're written, see MapToWrite)
        bool stale = script.MapId == _mapId && _map != null && !_pathsDirty && MapIsStale();
        if (script.MapId != _mapId || _map == null || stale)
        {
            if (_pathsDirty && _map != null) SavePaths(quiet: true);   // the previous map's paths get written on their own
            _pathsDirty = false;
            _mapId = script.MapId;
            try
            {
                _map = LufiaMap.Load(rom, _mapId);
                RememberMap();
                // the intro shows its map in a different palette than the map's own (the intro's setup code sets it)
                var tiles = MapTileset.ForMap(rom, _map, IntroScene.PaletteFor(rom, _map));
                var pixels = tiles.RenderMap(_map);
                _screen = new StageScreen(tiles, _map, rom);
                int width = _map.Width * 16, height = _map.Height * 16;
                var bmp = new WriteableBitmap(width, height, 96, 96, PixelFormats.Bgra32, null);
                bmp.WritePixels(new Int32Rect(0, 0, width, height), pixels, width * 4, 0);
                MapImage.Source = bmp;
                Overlay.Width = width; Overlay.Height = height;
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

    // the stage's map as we read it: its data, where it was, its walks (so we can tell if something else wrote it since)
    private byte[]? _mapData;
    private int _mapOffset = -1, _mapVersion = -1;
    private string _mapPaths = "";

    private static string PathsKey(LufiaMap map) => string.Join("|", map.Paths.Select(LufiaMap.State.Key));

    private void RememberMap()
    {
        if (_map == null || _rom == null) return;
        _mapData = (byte[])_map.Original.Clone();
        _mapOffset = _map.CompressedOffset;
        _mapVersion = _rom.Version;
        _mapPaths = PathsKey(_map);
    }

    // true when the ROM's copy of the stage's map isn't the one we read anymore (another tab, Undo, a patch)
    private bool MapIsStale()
    {
        if (_map == null || _rom == null || _mapData == null || _rom.Version == _mapVersion) return false;
        try
        {
            var romMap = LufiaMap.Load(_rom, _map.MapId);
            bool stale = romMap.CompressedOffset != _mapOffset || !romMap.Original.AsSpan().SequenceEqual(_mapData);
            if (!stale) _mapVersion = _rom.Version;
            return stale;
        }
        catch { return true; }
    }

    // the map the walks get written with. normally the stage's copy, but if the ROM's map changed since we read it
    // (blocks, characters... in the Map Editor) it's the ROM's map with the stage's walks put in, so writing them doesn't
    // put the older map back. throws if the walks themselves were changed somewhere else too
    private LufiaMap MapToWrite()
    {
        if (!MapIsStale()) return _map!;
        var romMap = LufiaMap.Load(_rom!, _map!.MapId);
        if (PathsKey(romMap) != _mapPaths)
            throw new InvalidOperationException($"The walks of map {_map.MapId:X2} were changed somewhere else since this cutscene was opened, " +
                                                "so these would write over them. Open the cutscene again to see them, then change them again.");
        romMap.Paths.Clear();
        romMap.Paths.AddRange(_map.Paths);
        return romMap;
    }

    private StageParty Party => CutsceneStage.Parties[Math.Max(0, PartyBox.SelectedIndex)];

    // works the stage out again after an edit
    public void Resimulate()
    {
        if (_map == null || _panel == null) return;
        _initial = CutsceneStage.Initial(_map, Party, _startX, _startY);
        _states = CutsceneStage.Simulate(_map, _panel.Script, Party, _startX, _startY);
        try { _timeline = CutsceneTimeline.Build(_panel.Script, _states); CutsceneStage.ApplyTime(_states, _timeline, _panel.Script); } catch { _timeline = new(); }
        bool first = _line < 0;
        ShowLine(_line < 0 ? 0 : Math.Min(_line, _states.Count - 1), scroll: first);
        Resimulated?.Invoke();
    }

    // shows the stage after line index
    public void ShowLine(int index, bool scroll = true)
    {
        if (_map == null) return;
        _line = index;
        var state = index >= 0 && index < _states.Count ? _states[index] : _initial;
        if (state == null) return;
        Draw(state, null);
        ShowText(state);
        UpdatePathPanel();
        if (scroll) ScrollTo(state.CameraX, state.CameraY);
        var op = SelectedOp;
        string hint = op == null || op.IsText ? "" : op.Bytes[0] switch
        {
            >= 0x20 and <= 0x27 => " Click the stage to move this character there.",
            0x09 => " Click the stage to point the camera there.",
            0x14 or 0x15 => " Edit the path below; ✎ Draw steps lets you click its route.",
            0x55 or 0x4B => " Click the stage to set X and Y.",
            _ => "",
        };
        StageInfo.Text = $"After line {index + 1}: " + Summary(state) + hint;
    }

    private StageState? CurrentState => _line >= 0 && _line < _states.Count ? _states[_line] : _initial;

    private ScriptOp? SelectedOp => _panel != null && _line >= 0 && _line < _panel.Script.Ops.Count ? _panel.Script.Ops[_line] : null;

    private static string Summary(StageState state)
    {
        var parts = new List<string>();
        if (state.Black) parts.Add("screen black");
        if (state.Music >= 0) parts.Add(state.Music == 0xFF ? "music stopped" : $"music {state.Music:X2}");
        if (state.Shaking) parts.Add("screen shaking");
        parts.Add($"camera on ({state.CameraX}, {state.CameraY})");
        if (state.Leaves != null) parts.Add("the event " + state.Leaves);
        return string.Join(" · ", parts) + ".";
    }

    private void ShowText(StageState state)
    {
        // a box that closed by itself stays on screen until something replaces or clears it (the intro's narration)
        string? text = state.Text ?? state.ShownText;
        int speaker = state.Text != null ? state.Speaker : state.ShownSpeaker;
        if (text == null) { TextBox.Visibility = Visibility.Collapsed; return; }
        if (state.TextWindow is { } window && speaker < 0)
        {
            TextBox.Visibility = Visibility.Collapsed;
            DrawNarration(text, CutsceneText.Printed(text), window, state.CameraX, state.CameraY);
            return;
        }
        TextBox.Visibility = Visibility.Visible;
        SpeakerText.Text = speaker < 0 ? "" : state.Actors.TryGetValue(speaker, out var actor) ? actor.Name : EventCommands.Actor(speaker);
        DialogueText.Text = text;
    }

    // drawing

    private readonly Dictionary<int, BitmapSource> _emotes = new();

    private BitmapSource Emote(int icon)
    {
        if (_emotes.TryGetValue(icon, out var cached)) return cached;
        var bmp = BitmapSource.Create(16, 16, 96, 96, PixelFormats.Bgra32, null, EmoteGraphics.Pixels(icon), 16 * 4);
        bmp.Freeze();
        return _emotes[icon] = bmp;
    }

    private BitmapSource? Sprite(int id)
    {
        if (_rom == null) return null;
        if (_sprites.TryGetValue(id, out var sprite)) return sprite;
        if (NpcSprites.Render(_rom, id) is var (pixels, width, height))
        {
            var bmp = BitmapSource.Create(width, height, 96, 96, PixelFormats.Bgra32, null, pixels, width * 4);
            bmp.Freeze();
            sprite = bmp;
        }
        _sprites[id] = sprite;
        return sprite;
    }

    private static readonly Brush WalkBrush = new SolidColorBrush(Color.FromArgb(230, 255, 215, 0));
    private static readonly Brush OtherPathBrush = new SolidColorBrush(Color.FromArgb(150, 120, 200, 255));
    private static readonly Brush CameraBrush = new SolidColorBrush(Color.FromArgb(220, 255, 255, 255));
    private static readonly Brush SelectedBrush = new SolidColorBrush(Color.FromArgb(255, 255, 80, 80));

    // moving = pixel positions of actors in mid-walk (playback), or null
    // darkness = fade level while playing (0 = full brightness, 1 = black), or -1 to show the line's state
    // gameScreen = playback / playhead: the camera's screen gets drawn the way the game shows it (see DrawScreen),
    // so characters, darkness and flash aren't drawn here
    private void Draw(StageState state, Dictionary<int, Point>? moving, double darkness = -1, bool gameScreen = false)
    {
        Overlay.Children.Clear();
        if (_map == null) return;

        if (ShowAllPathsBox.IsChecked == true)
            for (int i = 0; i < _map.Paths.Count; i++) DrawWalk(_map.Paths[i].Points(), OtherPathBrush, $"{i + 1}", 1.5);
        if (moving == null)
            foreach (var walk in state.Walks) DrawWalk(walk.Points, WalkBrush, walk.PathNumber > 0 ? $"path {walk.PathNumber}" : "", 3);

        int selActor = SelectedActor();
        // lower on screen goes in front. on a shared tile the leader is drawn last (in front of its followers)
        foreach (var actor in gameScreen ? Enumerable.Empty<StageActor>() : state.Actors.Values.OrderBy(a => a.Y).ThenByDescending(a => a.Id))
        {
            if (!actor.Visible) continue;   // hidden characters stay hidden while they walk (the intro's camera actor)
            var pos = moving != null && moving.TryGetValue(actor.Id, out var movingPos) ? movingPos : new Point(actor.X * 16, actor.Y * 16);
            var img = Sprite(actor.Sprite);
            double spriteW = img?.PixelWidth ?? 16, spriteH = img?.PixelHeight ?? 24;
            double left = pos.X + 8 - spriteW / 2, top = pos.Y + 16 - spriteH;
            if (img != null)
            {
                var spriteImage = new Image { Source = img, Width = spriteW, Height = spriteH, Opacity = actor.Blinking ? 0.55 : 1, IsHitTestVisible = false };
                RenderOptions.SetBitmapScalingMode(spriteImage, BitmapScalingMode.NearestNeighbor);
                Canvas.SetLeft(spriteImage, left); Canvas.SetTop(spriteImage, top);
                Overlay.Children.Add(spriteImage);
            }
            if (state.Emote is { } emote && emote.Actor == actor.Id)
            {
                var bubble = new Image { Source = Emote(emote.Icon), Width = 16, Height = 16, IsHitTestVisible = false };
                RenderOptions.SetBitmapScalingMode(bubble, BitmapScalingMode.NearestNeighbor);
                Canvas.SetLeft(bubble, left); Canvas.SetTop(bubble, top - 16);
                Overlay.Children.Add(bubble);
            }
            if (actor.Id == _picked)
            {
                var ring = new Ellipse { Width = 24, Height = 12, Stroke = WalkBrush, StrokeThickness = 2, IsHitTestVisible = false };
                Canvas.SetLeft(ring, pos.X - 4); Canvas.SetTop(ring, pos.Y + 10);
                Overlay.Children.Add(ring);
            }
            if (actor.Id == selActor)
            {
                var box = new Rectangle { Width = 16, Height = 16, Stroke = SelectedBrush, StrokeThickness = 1.5, IsHitTestVisible = false };
                Canvas.SetLeft(box, pos.X); Canvas.SetTop(box, pos.Y);
                Overlay.Children.Add(box);
            }
            // facing marker
            var arrow = new Polygon { Fill = Brushes.White, Opacity = 0.85, IsHitTestVisible = false };
            double cx = pos.X + 8, cy = pos.Y + 12;
            arrow.Points = actor.Facing switch
            {
                0 => new PointCollection { new(cx + 9, cy), new(cx + 5, cy - 3), new(cx + 5, cy + 3) },
                1 => new PointCollection { new(cx - 9, cy), new(cx - 5, cy - 3), new(cx - 5, cy + 3) },
                2 => new PointCollection { new(cx, cy + 7), new(cx - 3, cy + 3), new(cx + 3, cy + 3) },
                _ => new PointCollection { new(cx, cy - 19), new(cx - 3, cy - 15), new(cx + 3, cy - 15) },
            };
            Overlay.Children.Add(arrow);
            var label = new TextBlock
            {
                Text = actor.Id >= 7 ? $"{actor.Id - 7}" : actor.Name, Foreground = Brushes.White, FontSize = 8, IsHitTestVisible = false,
                Background = new SolidColorBrush(Color.FromArgb(150, 20, 10, 40)),
            };
            Canvas.SetLeft(label, pos.X); Canvas.SetTop(label, pos.Y + 16);
            // one label per tile is enough when the party stands together
            if (actor.Id is >= 2 and <= 5 && state.Actors.TryGetValue(1, out var lead) && lead.X == actor.X && lead.Y == actor.Y && moving?.ContainsKey(actor.Id) != true) continue;
            Overlay.Children.Add(label);
        }

        if (ShowCameraBox.IsChecked == true)
        {
            var cam = new Rectangle
            {
                Width = CutsceneStage.ScreenTilesX * 16, Height = CutsceneStage.ScreenTilesY * 16,
                Stroke = CameraBrush, StrokeThickness = 2, StrokeDashArray = new DoubleCollection { 4, 3 }, IsHitTestVisible = false,
                Fill = gameScreen ? null
                     : darkness > 0 ? new SolidColorBrush(Color.FromArgb((byte)Math.Round(230 * darkness), 0, 0, 0))
                     : darkness < 0 && state.Black ? new SolidColorBrush(Color.FromArgb(150, 0, 0, 0)) : null,
            };
            Point camCenter = moving != null && moving.TryGetValue(-1, out var movingCam) ? movingCam : new Point(state.CameraX * 16, state.CameraY * 16);
            Canvas.SetLeft(cam, camCenter.X - 7 * 16); Canvas.SetTop(cam, camCenter.Y - 7 * 16);
            Overlay.Children.Add(cam);
        }
        else if (!gameScreen && (darkness > 0 || (darkness < 0 && state.Black)))
        {
            byte alpha = darkness >= 0 ? (byte)Math.Round(230 * darkness) : (byte)120;
            var dark = new Rectangle { Width = Overlay.Width, Height = Overlay.Height, Fill = new SolidColorBrush(Color.FromArgb(alpha, 0, 0, 0)), IsHitTestVisible = false };
            Overlay.Children.Add(dark);
        }
    }

    private void DrawWalk(IReadOnlyList<(int X, int Y)> points, Brush brush, string label, double thickness)
    {
        if (points.Count == 0) return;
        var line = new Polyline { Stroke = brush, StrokeThickness = thickness, IsHitTestVisible = false, StrokeLineJoin = PenLineJoin.Round };
        foreach (var (x, y) in points) line.Points.Add(new Point(x * 16 + 8, y * 16 + 8));
        Overlay.Children.Add(line);
        var start = new Ellipse { Width = 6, Height = 6, Fill = brush, IsHitTestVisible = false };
        Canvas.SetLeft(start, points[0].X * 16 + 5); Canvas.SetTop(start, points[0].Y * 16 + 5);
        Overlay.Children.Add(start);
        var end = points[^1];
        var head = new Rectangle { Width = 8, Height = 8, Stroke = brush, StrokeThickness = 2, IsHitTestVisible = false };
        Canvas.SetLeft(head, end.X * 16 + 4); Canvas.SetTop(head, end.Y * 16 + 4);
        Overlay.Children.Add(head);
        if (label.Length > 0)
        {
            var labelText = new TextBlock { Text = label, Foreground = brush, FontSize = 9, FontWeight = FontWeights.Bold, IsHitTestVisible = false };
            Canvas.SetLeft(labelText, points[0].X * 16 + 10); Canvas.SetTop(labelText, points[0].Y * 16 - 4);
            Overlay.Children.Add(labelText);
        }
    }

    // the actor the selected command acts on, or -1
    private int SelectedActor()
    {
        var op = SelectedOp;
        if (op == null || op.IsText || op.Bytes.Length == 0) return -1;
        var opBytes = op.Bytes;
        try
        {
            return opBytes[0] switch
            {
                >= 0x20 and <= 0x27 => CutsceneStage.ActorOf(ParamDef.Who(1, 0x7F).Get(opBytes)),
                >= 0x28 and <= 0x2F or 0x38 or 0x39 or >= 0x5C and <= 0x5F => CutsceneStage.ActorOf(ParamDef.Who(1).Get(opBytes)),
                0x14 or 0x15 => CutsceneStage.ActorOf(ParamDef.Who(2).Get(opBytes)),
                >= 0xB0 and <= 0xBF => (opBytes[0] & 3) + 1,
                _ => -1,
            };
        }
        catch { return -1; }
    }

    private void ScrollTo(int tileX, int tileY)
    {
        // wait until the stage has a size (right after loading an event it doesn't yet)
        Dispatcher.BeginInvoke(() => ScrollNow(tileX, tileY), DispatcherPriority.Loaded);
    }

    private void ScrollNow(int tileX, int tileY)
    {
        double zoom = Zoom.ScaleX;
        Scroller.ScrollToHorizontalOffset(Math.Max(0, tileX * 16 * zoom - Scroller.ViewportWidth / 2));
        Scroller.ScrollToVerticalOffset(Math.Max(0, tileY * 16 * zoom - Scroller.ViewportHeight / 2));
    }

    // clicks

    private (int X, int Y) TileAt(MouseEventArgs e)
    {
        var mousePos = e.GetPosition(Overlay);
        return ((int)(mousePos.X / 16), (int)(mousePos.Y / 16));
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
        if (_pickPathStart && CurrentPath is { } pickedPath)
        {
            _pickPathStart = false;
            pickedPath.StartX = x; pickedPath.StartY = y;
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
        var state = CurrentState;
        var hit = state?.Actors.Values.Where(a => a.Visible && a.X == x && (a.Y == y || a.Y == y + 1))
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
            def.Params.FirstOrDefault(p => p.Label == "X") is { } xParam && def.Params.FirstOrDefault(p => p.Label == "Y") is { } yParam)
        {
            var bytes = (byte[])op.Bytes.Clone();
            xParam.Set(bytes, x); yParam.Set(bytes, y);
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

    // what can happen at a tile: the picked character (or any character) walks / appears / turns, or the camera moves
    private void ShowActionMenu(int x, int y)
    {
        var state = CurrentState;
        if (state == null || _panel == null) return;
        var menu = new ContextMenu { PlacementTarget = Overlay };
        menu.Items.Add(new MenuItem { Header = $"Tile ({x}, {y}), added after line {_line + 1}", IsEnabled = false });
        void Add(string header, Action act) { var menuItem = new MenuItem { Header = header }; menuItem.Click += (_, _) => act(); menu.Items.Add(menuItem); }

        IEnumerable<StageActor> who = _picked >= 0 && state.Actors.TryGetValue(_picked, out var pickedActor) ? new[] { pickedActor }
                                    : state.Actors.Values.Where(a => a.Id is 1 or >= 7).OrderBy(a => a.Id);
        foreach (var listed in who)
        {
            var actor = listed;
            if (_picked < 0)
            {
                var sub = new MenuItem { Header = actor.Name + (actor.Id >= 7 ? $" (character {actor.Id - 7})" : "") };
                void SubAdd(string header, Action act) { var menuItem = new MenuItem { Header = header }; menuItem.Click += (_, _) => act(); sub.Items.Add(menuItem); }
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

    // opcode bit 0 and operand for a character. map characters use the even opcode with their number
    private static (int Abs, int Operand) Who(StageActor actor) => actor.Id >= 7 ? (0, actor.Id - 7) : (1, actor.Id);

    private void Insert(byte[] bytes)
    {
        if (_panel == null) return;
        _panel.InsertCommand(_line, bytes);
    }

    private void AddPlace(StageActor actor, int x, int y)
    {
        var (abs, operand) = Who(actor);
        Insert(new byte[] { (byte)(0x20 | ((actor.Facing & 3) << 1) | abs), (byte)((operand & 0x7F) | ((x >> 8 & 1) << 7)), (byte)x, (byte)y });
    }

    private void AddFace(StageActor actor, int x, int y)
    {
        int dx = x - actor.X, dy = y - actor.Y;
        int dir = Math.Abs(dx) >= Math.Abs(dy) ? (dx >= 0 ? 0 : 1) : (dy >= 0 ? 2 : 3);
        var (abs, operand) = Who(actor);
        Insert(new byte[] { (byte)(0x28 | (dir << 1) | abs), (byte)operand });
    }

    private void AddWalk(StageActor actor, int x, int y)
    {
        if (_map == null) return;
        if (actor.X == x && actor.Y == y) { StageInfo.Text = $"{actor.Name} is already there."; return; }
        if (_map.Paths.Count >= 255) { StageInfo.Text = "This map already has 255 paths."; return; }
        var path = new MovePath { StartX = actor.X, StartY = actor.Y };
        const int normal = 4;
        if (x != actor.X) path.Steps.Add(new MoveStep { Direction = x > actor.X ? 0 : 1, Tiles = Math.Abs(x - actor.X), Speed = normal });
        if (y != actor.Y) path.Steps.Add(new MoveStep { Direction = y > actor.Y ? 2 : 3, Tiles = Math.Abs(y - actor.Y), Speed = normal });
        _map.Paths.Add(path);
        _pathsDirty = true;
        var (abs, operand) = Who(actor);
        Insert(new byte[] { (byte)(0x14 | abs), (byte)_map.Paths.Count, (byte)operand });
        StageInfo.Text = $"{actor.Name} walks to ({x}, {y}) on new path {_map.Paths.Count} (sideways first, then up/down). " +
                         "Change the route or speed under the stage; ✔ Apply writes the walk too.";
    }

    // true when walks were added or changed and aren't written yet
    public bool HasPathChanges => _pathsDirty && _map != null;

    // writes the changed walks (Apply calls this). returns what was done, or null if nothing changed
    public string? SavePathsWithEvent()
    {
        if (!HasPathChanges || _rom == null || _map == null || _panel == null) return null;
        bool expand = _rom.Length >= MapWriter.ExpandedSize;
        var map = MapToWrite();
        if (MapWriter.NeedsMoreSpace(map, out _) && !expand)
        {
            if (!_panel.Host.ConfirmExpand("The map's walks no longer fit where its data was.")) throw new InvalidOperationException("The walks weren't written.");
            expand = true;
        }
        var result = MapWriter.Save(_rom, map, allowExpand: expand);
        _pathsDirty = false;
        _panel.Host.EventSaved($"Map {_map.MapId:X2}: movement paths");
        _map = LufiaMap.Load(_rom, _map.MapId);
        RememberMap();
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

    // path editor

    // the path the selected line uses (commands 14/15), or null
    private MovePath? CurrentPath
    {
        get
        {
            var op = SelectedOp;
            if (_map == null || op == null || op.IsText || op.Bytes.Length < 3 || op.Bytes[0] is not (0x14 or 0x15)) return null;
            int pathNumber = op.Bytes[1];
            return pathNumber >= 1 && pathNumber <= _map.Paths.Count ? _map.Paths[pathNumber - 1] : null;
        }
    }

    private void UpdatePathPanel()
    {
        var op = SelectedOp;
        bool isPath = _map != null && op != null && !op.IsText && op.Bytes.Length >= 3 && op.Bytes[0] is 0x14 or 0x15;
        PathPanel.Visibility = isPath ? Visibility.Visible : Visibility.Collapsed;
        // the splitter above the walk editor only shows while the editor does, at the height the user last dragged it to
        PathSplitter.Visibility = PathPanel.Visibility;
        PathSplitRow.Height = new GridLength(isPath ? 8 : 0);
        PathRow.Height = isPath ? LufiaForge.Modules.Common.LayoutMemory.Parse(LufiaForge.Modules.Common.LayoutMemory.Get("stage.walkeditor")) ?? GridLength.Auto : GridLength.Auto;
        if (!isPath) { DrawToggle.IsChecked = false; return; }
        _filling = true;
        PathBox.Items.Clear();
        for (int i = 0; i < _map!.Paths.Count; i++)
        {
            var listedPath = _map.Paths[i];
            PathBox.Items.Add($"{i + 1}: from ({listedPath.StartX}, {listedPath.StartY}), {listedPath.Steps.Count} step{(listedPath.Steps.Count == 1 ? "" : "s")}");
        }
        int pathNumber = op!.Bytes[1];
        PathBox.SelectedIndex = pathNumber >= 1 && pathNumber <= _map.Paths.Count ? pathNumber - 1 : -1;
        var path = CurrentPath;
        PathHeader.Text = path == null ? $"Path {pathNumber} doesn't exist on this map" : $"Path {pathNumber} of this map";
        _steps.Clear();
        if (path != null)
        {
            WaitBox.IsChecked = !path.NoWait; RepeatBox.IsChecked = path.Repeat; CameraBox.IsChecked = path.CameraFollows; WalkStartBox.IsChecked = path.WalkToStart;
            StartXBox.Text = path.StartX.ToString(); StartYBox.Text = path.StartY.ToString();
            for (int i = 0; i < path.Steps.Count; i++) _steps.Add(new StepVm(this, path.Steps[i]) { Number = i + 1 });
            int users = UsersOf(pathNumber);
            PathStatus.Text = (users > 1 ? $"Path {pathNumber} is used by {users} lines of this map's events; changing it changes all of them. " : "") +
                              (_pathsDirty ? "Paths changed: write them to the ROM to keep them." : "");
        }
        _filling = false;
    }

    // how many commands in the map's events use path pathNumber
    private int UsersOf(int pathNumber)
    {
        if (_rom == null || _map == null) return 0;
        int count = 0;
        foreach (int ev in EventScript.PlausibleEvents(_rom, _map.MapId))
        {
            var script = EventScript.Load(_rom, _map.MapId, ev);
            if (script != null) count += script.Ops.Count(o => !o.IsText && o.Bytes.Length >= 3 && o.Bytes[0] is 0x14 or 0x15 && o.Bytes[1] == pathNumber);
        }
        return count;
    }

    // a path value changed: redraw and remember to write the map
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
        var state = _line > 0 && _line - 1 < _states.Count ? _states[_line - 1] : _initial;
        int actor = SelectedActor();
        var startTile = state != null && state.Actors.TryGetValue(actor, out var stageActor) ? (stageActor.X, stageActor.Y) : (_startX, _startY);
        _map.Paths.Add(new MovePath { StartX = startTile.Item1, StartY = startTile.Item2 });
        _pathsDirty = true;
        var bytes = (byte[])op.Bytes.Clone();
        bytes[1] = (byte)_map.Paths.Count;
        _panel.SetLineBytes(_line, bytes);
        DrawToggle.IsChecked = true;
        StageInfo.Text = "New path: click tiles on the stage to add its steps.";
    }

    private void PathFlags_Click(object sender, RoutedEventArgs e)
    {
        if (CurrentPath is not { } path) return;
        path.NoWait = WaitBox.IsChecked != true; path.Repeat = RepeatBox.IsChecked == true;
        path.CameraFollows = CameraBox.IsChecked == true; path.WalkToStart = WalkStartBox.IsChecked == true;
        PathEdited();
    }

    private void Start_LostFocus(object sender, RoutedEventArgs e)
    {
        if (_filling || CurrentPath is not { } path) return;
        if (int.TryParse(StartXBox.Text, NumberStyles.Integer, CultureInfo.InvariantCulture, out int x) && x is >= 0 and <= 0xFFFF &&
            int.TryParse(StartYBox.Text, NumberStyles.Integer, CultureInfo.InvariantCulture, out int y) && y is >= 0 and <= 0xFFFF &&
            (x != path.StartX || y != path.StartY))
        { path.StartX = x; path.StartY = y; PathEdited(); }
    }

    private void PickPathStart_Click(object sender, RoutedEventArgs e)
    {
        _pickPathStart = true;
        StageInfo.Text = "Click the tile where the path starts.";
    }

    private void AddStep_Click(object sender, RoutedEventArgs e)
    {
        if (CurrentPath is not { } path) return;
        path.Steps.Add(new MoveStep { Direction = 2, Tiles = 1, Speed = path.Steps.Count > 0 ? path.Steps[^1].Speed : 4 });
        PathEdited();
    }

    private void ClearSteps_Click(object sender, RoutedEventArgs e)
    {
        if (CurrentPath is not { } path) return;
        path.Steps.Clear();
        PathEdited();
    }

    private void RemoveStep_Click(object sender, RoutedEventArgs e)
    {
        if (CurrentPath is not { } path || sender is not FrameworkElement { DataContext: StepVm stepVm }) return;
        path.Steps.Remove(stepVm.Step);
        PathEdited();
    }


    private void SavePaths(bool quiet)
    {
        if (_rom == null || _map == null || _panel == null) return;
        try
        {
            bool expand = _rom.Length >= MapWriter.ExpandedSize || quiet;
            var map = MapToWrite();
            if (MapWriter.NeedsMoreSpace(map, out _) && !expand)
            {
                if (!_panel.Host.ConfirmExpand("The map's data no longer fits where it was.")) { PathStatus.Text = "Not written."; return; }
                expand = true;
            }
            var result = MapWriter.Save(_rom, map, allowExpand: expand);
            _pathsDirty = false;
            _panel.Host.EventSaved($"Map {_map.MapId:X2}: movement paths");
            PathStatus.Text = "✔ Paths saved in the project.";
            if (!quiet) _panel.Host.ConfirmWritten($"Paths of map {_map.MapId:X2} saved in the project", result.Describe());
            _map = LufiaMap.Load(_rom, _map.MapId);
            RememberMap();
            Resimulate();
        }
        catch (Exception ex) { PathStatus.Text = "Not written: " + ex.Message; }
    }

    // playback

    private readonly DispatcherTimer _timer;
    private int _playLine, _frame, _playStart, _playEnd;
    private readonly System.Diagnostics.Stopwatch _clock = new();

    private List<TimelineItem> _timeline = new();

    // the stage after every line, the map shown, and the lines placed in time (for the timeline)
    public IReadOnlyList<StageState> States => _states;
    public LufiaMap? StageMap => _map;
    public IReadOnlyList<TimelineItem> TimelineItems => _timeline;
    public bool IsPlaying => _timer.IsEnabled;

    // fires after the stage gets worked out again (edits, party/start changes)
    public event Action? Resimulated;
    // fires every frame while playing: (line, frames into the line)
    public event Action<int, int>? PlaybackProgress;
    public event Action? PlaybackStopped;

    // play from a line (the timeline's play / rewind)
    public void PlayFrom(int line)
    {
        _line = Math.Clamp(line, 0, Math.Max(0, _states.Count - 1));
        Play_Click(this, new RoutedEventArgs());
    }

    public void Stop() { if (_timer.IsEnabled) StopPlayback(); }

    // sets the speed of every step of a path (resizing a walk on the timeline)
    public void SetPathSpeed(int pathNumber, int speed)
    {
        if (_map == null || pathNumber < 1 || pathNumber > _map.Paths.Count) return;
        foreach (var step in _map.Paths[pathNumber - 1].Steps) step.Speed = speed;
        PathEdited();
    }

    private void Play_Click(object sender, RoutedEventArgs e)
    {
        if (_states.Count == 0 || _initial == null || _panel == null) return;
        _playLine = Math.Max(0, _line);
        // start where the line starts on the timeline, then the frame counter runs at the console's rate
        _playStart = _timeline.Where(t => t.Line >= _playLine).Select(t => t.Start).DefaultIfEmpty(0).Min();
        _playEnd = CutsceneFrame.Length(_states, _timeline);
        _frame = _playStart;
        StartMusic(_playStart);
        _clock.Restart();
        _timer.Start();
        PlayButton.IsEnabled = false;
        Tick();
    }

    // music

    private CutsceneMusic? _music;
    private AudioOut? _audio;

    private static readonly string SoundPrefs = System.IO.Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "LufiaForge", "cutscenesound.txt");

    // play the music while previewing (remembered, on by default)
    private static bool SoundOn
    {
        get { try { return !System.IO.File.Exists(SoundPrefs) || System.IO.File.ReadAllText(SoundPrefs).Trim() != "0"; } catch { return true; } }
        set { try { System.IO.Directory.CreateDirectory(System.IO.Path.GetDirectoryName(SoundPrefs)!); System.IO.File.WriteAllText(SoundPrefs, value ? "1" : "0"); } catch { } }
    }

    private void Sound_Click(object sender, RoutedEventArgs e)
    {
        SoundOn = SoundBox.IsChecked == true;
        if (!_timer.IsEnabled) return;
        // switched while playing: carry on from the frame on screen
        int currentFrame = _frame;
        StopMusic();
        _playStart = currentFrame; _clock.Restart();
        StartMusic(currentFrame);
    }

    // plays the cutscene's songs from frame on, through the game's sound driver
    private void StartMusic(int frame)
    {
        StopMusic();
        if (SoundBox.IsChecked != true || _rom == null || _panel == null || !SpcNative.Available) return;
        var cues = CutsceneMusic.Cues(_rom, _panel.Script, _timeline);
        if (cues.Count == 0) return;
        try
        {
            _music = new CutsceneMusic(_rom, cues);
            _music.Seek(frame);
            var music = _music;
            _audio = new AudioOut(LufiaSound.SampleRate, (buffer, pairs) => { lock (music) music.Render(buffer, pairs); });
            if (!_audio.Start()) StopMusic();
        }
        catch { StopMusic(); }
    }

    private void StopMusic()
    {
        _audio?.Dispose(); _audio = null;
        if (_music != null) { lock (_music) _music.Dispose(); _music = null; }
    }

    private void Stop_Click(object sender, RoutedEventArgs e) => StopPlayback();

    private void StopPlayback()
    {
        _timer.Stop();
        _clock.Stop();
        StopMusic();
        PlaybackStopped?.Invoke();
        PlayButton.IsEnabled = true;
        ShowLine(Math.Min(_playLine, _states.Count - 1), scroll: false);
        _panel?.HighlightLine(Math.Min(_playLine, _states.Count - 1));
    }

    // SNES (NTSC) frames per second. playback runs in game frames, not timer ticks
    private const double ConsoleFps = 60.0988;

    private void Tick()
    {
        if (_panel == null || _initial == null) return;
        // with music on, the sound device is the clock (what has been heard), so picture and sound stay together
        _frame = _audio != null
            ? _playStart + (int)(_audio.PlayedPairs / CutsceneMusic.SamplesPerFrame)
            : _playStart + (int)(_clock.Elapsed.TotalSeconds * ConsoleFps);
        if (_frame >= _playEnd) { StopPlayback(); return; }
        var view = CutsceneFrame.At(_initial, _states, _timeline, _panel.Script, _frame);
        if (view.Line != _playLine) { _playLine = view.Line; _line = view.Line; _panel.HighlightLine(view.Line); }
        if (view.State.Leaves != null && _timeline.Where(t => t.Line == view.Line).All(t => _frame >= t.Start + Math.Max(1, t.VisibleLength))) { StopPlayback(); return; }

        RenderFrame(view, _frame);
        var item = _timeline.FirstOrDefault(t => t.Line == view.Line);
        PlaybackProgress?.Invoke(view.Line, item == null ? 0 : _frame - item.Start);
        double zoom = Zoom.ScaleX;
        Scroller.ScrollToHorizontalOffset(Math.Max(0, (view.CamX * 16 + 8) * zoom - Scroller.ViewportWidth / 2));
        Scroller.ScrollToVerticalOffset(Math.Max(0, (view.CamY * 16 + 8) * zoom - Scroller.ViewportHeight / 2));
        StageInfo.Text = $"Playing line {view.Line + 1} of {_states.Count} · frame {_frame} ({_frame / ConsoleFps:0.0} s)";
    }

    // shows the stage at one frame of the cutscene (the timeline's playhead), the way the game has it then
    public void ShowFrame(int frame)
    {
        if (_panel == null || _initial == null || _states.Count == 0) return;
        if (_timer.IsEnabled) StopPlayback();
        var view = CutsceneFrame.At(_initial, _states, _timeline, _panel.Script, Math.Max(0, frame));
        _line = view.Line;
        _panel.HighlightLine(view.Line);
        RenderFrame(view, Math.Max(0, frame));
        double zoom = Zoom.ScaleX;
        Scroller.ScrollToHorizontalOffset(Math.Max(0, (view.CamX * 16 + 8) * zoom - Scroller.ViewportWidth / 2));
        Scroller.ScrollToVerticalOffset(Math.Max(0, (view.CamY * 16 + 8) * zoom - Scroller.ViewportHeight / 2));
        StageInfo.Text = $"Frame {frame} ({frame / ConsoleFps:0.0} s) · line {view.Line + 1} · camera on ({view.CamX:0.##}, {view.CamY:0.##})";
    }

    // the stage at a frame: the map, the editor's marks, and the camera's screen exactly like the game draws it
    private void RenderFrame(FrameView view, int frame)
    {
        var moving = view.Positions.ToDictionary(kv => kv.Key, kv => new Point(kv.Value.X * 16, kv.Value.Y * 16));
        moving[-1] = new Point(view.CamX * 16, view.CamY * 16);
        if (_screen == null || _rom == null || _map == null || _panel == null || _initial == null)
        {
            Draw(view.State, moving, view.Darkness);
            DrawFlash(view.Flash, view.CamX, view.CamY);
            ShowFrameText(view);
            return;
        }
        Draw(view.State, moving, view.Darkness, gameScreen: true);
        var prevView = frame > 0 ? CutsceneFrame.At(_initial, _states, _timeline, _panel.Script, frame - 1) : view;
        DrawScreen(view, prevView);
        // dialogue boxes (not narration) still use the box under the stage
        if (view.Text != null && !(view.TextWindow != null && view.Speaker < 0)) ShowFrameText(view);
        else TextBox.Visibility = Visibility.Collapsed;
    }

    // the camera's 256x224 screen the way the console shows it on this frame: the map's two layers in the cutscene's
    // palette, the characters, the narration being typed, the palette fade and the lightning flash
    private void DrawScreen(FrameView view, FrameView prevView)
    {
        var (left, top) = StageScreen.Origin(prevView.CamX, prevView.CamY);
        var (bgScrollX, bgScrollY) = IntroScene.Layer2Scroll(_map!, view.FieldFrames);
        var sprites = new List<(uint[] Px, int W, int H, int X, int Y)>();
        foreach (var actor in view.State.Actors.Values.Where(a => a.Visible).OrderBy(a => view.Positions.TryGetValue(a.Id, out var p) ? p.Y : a.Y).ThenByDescending(a => a.Id))
        {
            if (NpcSprites.Render(_rom!, actor.Sprite) is not var (spritePixels, spriteW, spriteH)) continue;
            var (actorX, actorY) = view.Positions.TryGetValue(actor.Id, out var pos) ? pos : (actor.X, actor.Y);
            sprites.Add((spritePixels, spriteW, spriteH, (int)Math.Round(actorX * 16 + 8 - spriteW / 2.0) - left, (int)Math.Round(actorY * 16 + 16 - spriteH) - top));
            if (view.State.Emote is { } emote && emote.Actor == actor.Id)
                sprites.Add((EmoteGraphics.Pixels(emote.Icon), 16, 16, (int)Math.Round(actorX * 16 + 8 - spriteW / 2.0) - left, (int)Math.Round(actorY * 16 + 16 - spriteH) - top - 16));
        }
        var lines = view.Text != null && view.TextWindow is { } window && view.Speaker < 0
            ? CutsceneText.Layout(view.Text, view.TypedText ?? "", window.Column, window.Row) : null;
        var pixels = _screen!.Render(left, top, bgScrollX, bgScrollY, view.FadeLevel, view.FlashAdd, sprites, lines);
        var bmp = new WriteableBitmap(StageScreen.Width, StageScreen.Height, 96, 96, PixelFormats.Bgra32, null);
        bmp.WritePixels(new Int32Rect(0, 0, StageScreen.Width, StageScreen.Height), pixels, StageScreen.Width * 4, 0);
        var img = new Image { Source = bmp, Width = StageScreen.Width, Height = StageScreen.Height, IsHitTestVisible = false };
        RenderOptions.SetBitmapScalingMode(img, BitmapScalingMode.NearestNeighbor);
        Canvas.SetLeft(img, left); Canvas.SetTop(img, top);
        Overlay.Children.Insert(0, img);   // under the editor's marks
    }

    private void ShowFrameText(FrameView view)
    {
        if (view.Text == null) { TextBox.Visibility = Visibility.Collapsed; return; }
        // narration placed with 6B gets printed straight on the screen, the way the game types it
        if (view.TextWindow is { } window && view.Speaker < 0)
        {
            TextBox.Visibility = Visibility.Collapsed;
            DrawNarration(view.Text, view.TypedText ?? "", window, view.CamX, view.CamY);
            return;
        }
        TextBox.Visibility = Visibility.Visible;
        SpeakerText.Text = view.Speaker < 0 ? "" : view.State.Actors.TryGetValue(view.Speaker, out var actor) ? actor.Name : EventCommands.Actor(view.Speaker);
        DialogueText.Text = CutsceneText.Printed(view.TypedText ?? view.Text);
    }

    private static readonly Brush NarrationShadow = new SolidColorBrush(Color.FromRgb(0x18, 0x20, 0x88));

    // narration text on the stage: 8x16 pixel characters at the 6B window, relative to the camera's screen
    private void DrawNarration(string full, string shown, (int Column, int Row) window, double camX, double camY)
    {
        double left = (camX - 7) * 16, top = (camY - 7) * 16;   // the screen's top-left corner on the map
        foreach (var (line, x, y) in CutsceneText.Layout(full, shown, window.Column, window.Row))
        {
            if (line.Length == 0) continue;
            foreach (var (brush, offset) in new[] { (NarrationShadow, 1.0), ((Brush)Brushes.White, 0.0) })
            {
                var lineText = new TextBlock
                {
                    Text = line, Foreground = brush, FontFamily = new System.Windows.Media.FontFamily("Consolas"), FontSize = 14.55,   // 8 pixels per character
                    IsHitTestVisible = false,
                };
                Canvas.SetLeft(lineText, left + x + offset); Canvas.SetTop(lineText, top + y + offset - 2);
                Overlay.Children.Add(lineText);
            }
        }
    }

    // lightning flash: the screen washes to white
    private void DrawFlash(double flash, double camX, double camY)
    {
        if (flash <= 0) return;
        var white = new Rectangle
        {
            Width = CutsceneStage.ScreenTilesX * 16, Height = CutsceneStage.ScreenTilesY * 16, IsHitTestVisible = false,
            Fill = new SolidColorBrush(Color.FromArgb((byte)Math.Round(255 * flash), 255, 255, 255)),
        };
        Canvas.SetLeft(white, (camX - 7) * 16); Canvas.SetTop(white, (camY - 7) * 16);
        Overlay.Children.Add(white);
    }
}
