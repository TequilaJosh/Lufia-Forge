using System.IO;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Shapes;
using LufiaForge.Core;
using LufiaForge.Core.Lufia2;
using LufiaForge.ViewModels;
using Brush = System.Windows.Media.Brush;
using Brushes = System.Windows.Media.Brushes;
using Point = System.Windows.Point;
using Color = System.Windows.Media.Color;
using Rectangle = System.Windows.Shapes.Rectangle;
using Image = System.Windows.Controls.Image;
using ListBox = System.Windows.Controls.ListBox;
using ComboBox = System.Windows.Controls.ComboBox;
using Button = System.Windows.Controls.Button;
using TextBox = System.Windows.Controls.TextBox;
using CheckBox = System.Windows.Controls.CheckBox;
using Orientation = System.Windows.Controls.Orientation;
using HorizontalAlignment = System.Windows.HorizontalAlignment;
using VerticalAlignment = System.Windows.VerticalAlignment;

namespace LufiaForge.Modules.Lufia2;

/// <summary>Small helpers for the Lufia II map windows (built in code, styled from the app's theme).</summary>
internal static class L2Ui
{
    private static ResourceDictionary? _gd;
    /// <summary>A style of the app or of the Game Data styles (GdNumber, GdCombo...).</summary>
    public static Style? S(string key)
    {
        _gd ??= new ResourceDictionary { Source = new Uri("/LufiaForge;component/Modules/GameData/GameDataStyles.xaml", UriKind.Relative) };
        return (_gd.Contains(key) ? _gd[key] : System.Windows.Application.Current.TryFindResource(key)) as Style;
    }
    public static Brush B(string key) => System.Windows.Application.Current.TryFindResource(key) as Brush ?? Brushes.Black;

    public static Window Window(string title, double w, double h) => new()
    {
        Title = title + " - Lufia Forge", Width = w, Height = h, WindowStartupLocation = WindowStartupLocation.CenterOwner,
        Background = B("MidPurpleBrush"), ShowInTaskbar = false,
    };

    public static TextBlock Label(string text, bool hint = false) => new()
    {
        Text = text, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 0, 0, 6),
        Style = S(hint ? "LufiaLabel" : "LufiaLabel"), Foreground = B(hint ? "TextSecondaryBrush" : "TextPrimaryBrush"),
    };

    public static Button Button(string text, bool gold, RoutedEventHandler click)
    {
        var b = new Button { Content = text, Style = S(gold ? "LufiaButtonGold" : "LufiaButton"), Padding = new Thickness(12, 4, 12, 4), Margin = new Thickness(0, 0, 6, 0) };
        b.Click += click;
        return b;
    }

    public static BitmapSource Bitmap(int w, int h, uint[] px) =>
        BitmapSource.Create(w, h, 96, 96, PixelFormats.Bgra32, null, px.Select(c => (int)(c == 0 ? 0xFF000000u : c)).ToArray(), w * 4);
}

/// <summary>Pick a block on a map (where an exit leads).</summary>
public sealed class L2PickPlaceWindow : Window
{
    public int Map { get; private set; }
    public int X { get; private set; }
    public int Y { get; private set; }
    private readonly RomBuffer _rom;
    private readonly Image _image = new() { Stretch = Stretch.None, HorizontalAlignment = HorizontalAlignment.Left, VerticalAlignment = VerticalAlignment.Top, Cursor = System.Windows.Input.Cursors.Cross };
    private readonly Rectangle _mark = new() { Width = 16, Height = 16, Stroke = Brushes.White, StrokeThickness = 2, IsHitTestVisible = false };
    private readonly TextBlock _where = L2Ui.Label("");
    private readonly Canvas _labels = new() { IsHitTestVisible = false };
    private readonly Func<int, string> _name;
    /// <summary>Pixels per 16-pixel block in the picture (the world map is drawn at half size here).</summary>
    private int Unit => Map == 0 ? 8 : 16;

    public L2PickPlaceWindow(RomBuffer rom, int map, int x, int y, Func<int, string> name)
    {
        _rom = rom; Map = map; X = x; Y = y; _name = name;
        Title = "Where the exit leads - Lufia Forge"; Width = 980; Height = 760; WindowStartupLocation = WindowStartupLocation.CenterOwner; Background = L2Ui.B("MidPurpleBrush"); ShowInTaskbar = false;
        var maps = new ComboBox { Style = L2Ui.S("LufiaComboBox"), Width = 320, HorizontalAlignment = HorizontalAlignment.Left, Margin = new Thickness(0, 0, 0, 6) };
        // the world map first, then the towns and dungeons (the sea floor is reached by events only)
        var ids = new List<int> { 0 };
        ids.AddRange(Enumerable.Range(2, L2Maps.Count - 2));
        foreach (int m in ids) maps.Items.Add(m == 0 ? "00  World map" : $"{m:X2}  {name(m)}");
        maps.SelectedIndex = Math.Max(0, ids.IndexOf(map));
        maps.SelectionChanged += (_, _) => { Map = ids[Math.Max(0, maps.SelectedIndex)]; Draw(); _mark.BringIntoView(); };
        var canvas = new Canvas { IsHitTestVisible = false };
        canvas.Children.Add(_mark);
        var grid = new Grid { HorizontalAlignment = HorizontalAlignment.Left, VerticalAlignment = VerticalAlignment.Top };
        grid.Children.Add(_image); grid.Children.Add(_labels); grid.Children.Add(canvas);
        _image.MouseLeftButtonDown += (_, e) => { var p = e.GetPosition(_image); X = (int)p.X / Unit; Y = (int)p.Y / Unit; Mark(); if (e.ClickCount == 2) { DialogResult = true; } };
        var scroll = new ScrollViewer { Content = grid, HorizontalScrollBarVisibility = ScrollBarVisibility.Auto, VerticalScrollBarVisibility = ScrollBarVisibility.Auto, Background = Brushes.Black };
        LufiaForge.Modules.Common.ScrollPan.SetMiddleDrag(scroll, true);
        var buttons = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 8, 0, 0) };
        buttons.Children.Add(L2Ui.Button("Use this place", true, (_, _) => DialogResult = true));
        buttons.Children.Add(L2Ui.Button("Cancel", false, (_, _) => DialogResult = false));
        var dock = new DockPanel { Margin = new Thickness(12) };
        var top = new StackPanel();
        top.Children.Add(L2Ui.Label("Pick the map, then click the block where the party arrives (double-click to use it at once). On the world map the towns' entrances are labelled."));
        top.Children.Add(maps); top.Children.Add(_where);
        DockPanel.SetDock(top, Dock.Top); DockPanel.SetDock(buttons, Dock.Bottom);
        dock.Children.Add(top); dock.Children.Add(buttons); dock.Children.Add(scroll);
        Content = dock;
        Loaded += (_, _) => { Draw(); _mark.BringIntoView(); };
    }

    private void Draw()
    {
        _labels.Children.Clear();
        try
        {
            if (Map == 0)
            {
                var (ww, wh, wpx) = L2WorldMap.Render(_rom, 2);
                _image.Source = L2Ui.Bitmap(ww, wh, wpx);
                // where each entrance leads, so towns can be found
                foreach (var e in L2MapInfo.Read(_rom, 0).Where(e => e.Kind == "Exit" && e.DestMap >= 2))
                {
                    var t = new TextBlock { Text = _name(e.DestMap), Foreground = Brushes.White, FontSize = 9, Background = new SolidColorBrush(Color.FromArgb(0xA0, 0, 0, 0)), Padding = new Thickness(2, 0, 2, 0) };
                    Canvas.SetLeft(t, e.X1 * 8); Canvas.SetTop(t, e.Y1 * 8 - 12);
                    _labels.Children.Add(t);
                    var r = new Rectangle { Width = Math.Max(1, e.X2 - e.X1) * 8, Height = Math.Max(1, e.Y2 - e.Y1) * 8, Stroke = Brushes.LimeGreen, StrokeThickness = 1 };
                    Canvas.SetLeft(r, e.X1 * 8); Canvas.SetTop(r, e.Y1 * 8);
                    _labels.Children.Add(r);
                }
            }
            else { var (w, h, px) = L2Maps.Render(_rom, Map); _image.Source = L2Ui.Bitmap(w, h, px); }
        }
        catch { _image.Source = null; }
        Mark();
    }

    private void Mark()
    {
        _mark.Width = _mark.Height = Unit;
        Canvas.SetLeft(_mark, X * Unit); Canvas.SetTop(_mark, Y * Unit);
        _where.Text = Map == 0 ? $"Arrives on the world map at block ({X},{Y}) (16-pixel blocks)." : $"Arrives at block ({X},{Y}).";
    }
}

/// <summary>Pieces kept by the user (an area of blocks with its walls, for a block set and tile set), in AppData.</summary>
public static class L2Presets
{
    public sealed class Preset
    {
        public string Name { get; set; } = "";
        public int BlockSet { get; set; }
        public int TileSet { get; set; }
        public int W { get; set; }
        public int H { get; set; }
        public int[] Ground { get; set; } = Array.Empty<int>();
        public int[] Upper { get; set; } = Array.Empty<int>();
        public byte[] Flags { get; set; } = Array.Empty<byte>();
    }

    private static string PathOf => System.IO.Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "LufiaForge", "l2presets.json");

    public static List<Preset> Load()
    {
        try { return File.Exists(PathOf) ? JsonSerializer.Deserialize<List<Preset>>(File.ReadAllText(PathOf)) ?? new() : new(); }
        catch { return new(); }
    }

    public static void Save(List<Preset> list)
    {
        Directory.CreateDirectory(System.IO.Path.GetDirectoryName(PathOf)!);
        File.WriteAllText(PathOf, JsonSerializer.Serialize(list, new JsonSerializerOptions { WriteIndented = true }));
    }

    public static void Add(string name, L2MapsPage.Clip c)
    {
        var list = Load();
        list.RemoveAll(p => p.Name == name && p.BlockSet == c.BlockSet);
        list.Add(new Preset { Name = name, BlockSet = c.BlockSet, TileSet = c.TileSet, W = c.W, H = c.H, Ground = c.Ground, Upper = c.Upper, Flags = c.Flags });
        Save(list);
    }

    public static L2MapsPage.Clip ToClip(Preset p) => new(p.W, p.H, p.Ground, p.Upper, p.Flags, p.BlockSet, p.TileSet);
}

/// <summary>Copy part of another map (or a kept preset) to place on this one.</summary>
public sealed class L2PiecesWindow : Window
{
    public L2MapsPage.Clip? Picked { get; private set; }
    private readonly RomBuffer _rom;
    private readonly int _blockSet, _tileSet;
    private readonly ListBox _list = new();
    private readonly Image _image = new() { Stretch = Stretch.None, HorizontalAlignment = HorizontalAlignment.Left, VerticalAlignment = VerticalAlignment.Top, Cursor = System.Windows.Input.Cursors.Cross };
    private readonly Rectangle _box = new() { Stroke = Brushes.White, StrokeThickness = 1.5, StrokeDashArray = new DoubleCollection { 3, 2 }, Fill = new SolidColorBrush(Color.FromArgb(0x30, 0, 0xBF, 0xFF)), IsHitTestVisible = false, Visibility = Visibility.Collapsed };
    private readonly TextBlock _note = L2Ui.Label("");
    private readonly List<(string Label, int Map, L2Presets.Preset? Preset)> _entries = new();
    private L2Maps.Map? _map;
    private (int X, int Y)? _start;
    private (int X1, int Y1, int X2, int Y2)? _rect;

    public L2PiecesWindow(RomBuffer rom, int current, int blockSet, int tileSet, Func<int, string> name)
    {
        _rom = rom; _blockSet = blockSet; _tileSet = tileSet;
        Title = "Pieces from maps - Lufia Forge"; Width = 1100; Height = 780; WindowStartupLocation = WindowStartupLocation.CenterOwner; Background = L2Ui.B("MidPurpleBrush"); ShowInTaskbar = false;
        foreach (var p in L2Presets.Load().Where(p => p.BlockSet == blockSet)) _entries.Add(($"★ {p.Name}  ({p.W} x {p.H})", -1, p));
        for (int m = 2; m < L2Maps.Count; m++)
            if (rom.ReadByte(L2Maps.BlockSetList + m) == blockSet)
                _entries.Add(($"{m:X2}  {name(m)}{(rom.ReadByte(L2Maps.TileSetList + m) == tileSet ? "" : "  (other tiles)")}{(m == current ? "  (this map)" : "")}", m, null));
        foreach (var e in _entries) _list.Items.Add(e.Label);
        _list.Style = L2Ui.S("GdRecordList");
        _list.SelectionChanged += (_, _) => Show();
        var canvas = new Canvas { IsHitTestVisible = false }; canvas.Children.Add(_box);
        var grid = new Grid { HorizontalAlignment = HorizontalAlignment.Left, VerticalAlignment = VerticalAlignment.Top };
        grid.Children.Add(_image); grid.Children.Add(canvas);
        _image.MouseLeftButtonDown += (_, e) => { if (_map == null) return; var p = e.GetPosition(_image); _start = ((int)p.X / 16, (int)p.Y / 16); Drag(p); _image.CaptureMouse(); };
        _image.MouseMove += (_, e) => { if (_start != null && e.LeftButton == MouseButtonState.Pressed) Drag(e.GetPosition(_image)); };
        _image.MouseLeftButtonUp += (_, _) => { _start = null; _image.ReleaseMouseCapture(); };
        var scroll = new ScrollViewer { Content = grid, HorizontalScrollBarVisibility = ScrollBarVisibility.Auto, VerticalScrollBarVisibility = ScrollBarVisibility.Auto, Background = Brushes.Black };
        LufiaForge.Modules.Common.ScrollPan.SetMiddleDrag(scroll, true);
        var buttons = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 8, 0, 0) };
        buttons.Children.Add(L2Ui.Button("Use it", true, (_, _) => Use()));
        buttons.Children.Add(L2Ui.Button("Delete this preset", false, (_, _) => DeletePreset()));
        buttons.Children.Add(L2Ui.Button("Cancel", false, (_, _) => DialogResult = false));
        var left = new DockPanel { Width = 300, Margin = new Thickness(0, 0, 10, 0) };
        var lt = L2Ui.Label($"Maps with block set {blockSet} (the same blocks as this map), and your presets for it (★).", true);
        DockPanel.SetDock(lt, Dock.Top); left.Children.Add(lt); left.Children.Add(_list);
        var right = new DockPanel();
        DockPanel.SetDock(_note, Dock.Top); DockPanel.SetDock(buttons, Dock.Bottom);
        right.Children.Add(_note); right.Children.Add(buttons); right.Children.Add(scroll);
        var dock = new DockPanel { Margin = new Thickness(12) };
        DockPanel.SetDock(left, Dock.Left);
        dock.Children.Add(left); dock.Children.Add(right);
        Content = dock;
        _list.SelectedIndex = _entries.Count > 0 ? 0 : -1;
        if (_entries.Count == 0) _note.Text = "No map uses this block set.";
    }

    private (string Label, int Map, L2Presets.Preset? Preset)? Entry => _list.SelectedIndex >= 0 ? _entries[_list.SelectedIndex] : null;

    private void Show()
    {
        _rect = null; _box.Visibility = Visibility.Collapsed; _map = null;
        if (Entry is not { } e) return;
        try
        {
            if (e.Preset is { } p)
            {
                var clip = L2Presets.ToClip(p);
                // a preset is drawn as a little map of its own
                var m = new L2Maps.Map { Id = Entry!.Value.Map >= 0 ? e.Map : FirstMapWith(_blockSet), BlockSet = L2Maps.BlockSetFileBase + p.BlockSet, TileSet = L2Maps.TileSetFileBase + p.TileSet,
                    Layers = new[] { new L2Maps.Layer { Width = p.W, Height = p.H, Blocks = p.Ground, Flags = new byte[p.W * p.H] }, new L2Maps.Layer { Width = p.W, Height = p.H, Blocks = p.Upper, Flags = p.Flags } } };
                var (w, h, px) = L2Maps.Render(_rom, m);
                _image.Source = L2Ui.Bitmap(w, h, px);
                _note.Text = $"Your preset \"{p.Name}\". Use it to place it on the map.";
                return;
            }
            _map = L2Maps.Read(_rom, e.Map);
            var (mw, mh, mpx) = L2Maps.Render(_rom, _map);
            _image.Source = L2Ui.Bitmap(mw, mh, mpx);
            _note.Text = "Drag a rectangle around the piece you want (both layers and their walls are copied), then Use it.";
        }
        catch (Exception ex) { _image.Source = null; _note.Text = "Couldn't draw it: " + ex.Message; }
    }

    private int FirstMapWith(int set) { for (int m = 2; m < L2Maps.Count; m++) if (_rom.ReadByte(L2Maps.BlockSetList + m) == set) return m; return 3; }

    private void Drag(Point p)
    {
        if (_start is not { } s || _map == null) return;
        int x = Math.Clamp((int)p.X / 16, 0, _map.Layers[0].Width - 1), y = Math.Clamp((int)p.Y / 16, 0, _map.Layers[0].Height - 1);
        _rect = (Math.Min(s.X, x), Math.Min(s.Y, y), Math.Max(s.X, x) + 1, Math.Max(s.Y, y) + 1);
        var r = _rect.Value;
        Canvas.SetLeft(_box, r.X1 * 16); Canvas.SetTop(_box, r.Y1 * 16); _box.Width = (r.X2 - r.X1) * 16; _box.Height = (r.Y2 - r.Y1) * 16;
        _box.Visibility = Visibility.Visible;
        _note.Text = $"{r.X2 - r.X1} x {r.Y2 - r.Y1} blocks from ({r.X1},{r.Y1}).";
    }

    private void Use()
    {
        if (Entry is not { } e) return;
        if (e.Preset is { } p) { Picked = L2Presets.ToClip(p); DialogResult = true; return; }
        if (_map == null || _rect is not { } r) { _note.Text = "Drag a rectangle on the map first."; return; }
        int w = r.X2 - r.X1, h = r.Y2 - r.Y1;
        var g = new int[w * h]; var u = new int[w * h]; var f = new byte[w * h];
        var L0 = _map.Layers[0]; var L1 = _map.Layers.Length > 1 ? _map.Layers[1] : null;
        for (int y = 0; y < h; y++)
            for (int x = 0; x < w; x++)
            {
                int i = (r.Y1 + y) * L0.Width + r.X1 + x;
                g[y * w + x] = L0.Blocks[i];
                if (L1 != null) { u[y * w + x] = L1.Blocks[i]; f[y * w + x] = L1.Flags[i]; }
            }
        Picked = new L2MapsPage.Clip(w, h, g, u, f, _rom.ReadByte(L2Maps.BlockSetList + e.Map), _rom.ReadByte(L2Maps.TileSetList + e.Map));
        DialogResult = true;
    }

    private void DeletePreset()
    {
        if (Entry is not { Preset: { } p }) { _note.Text = "Pick one of your presets (★) first."; return; }
        var list = L2Presets.Load();
        list.RemoveAll(x => x.Name == p.Name && x.BlockSet == p.BlockSet);
        L2Presets.Save(list);
        int i = _list.SelectedIndex;
        _entries.RemoveAt(i); _list.Items.RemoveAt(i);
        _note.Text = $"Deleted \"{p.Name}\".";
    }

    /// <summary>Asks for a preset's name.</summary>
    public static string? AskName(Window? owner)
    {
        var w = L2Ui.Window("Save as preset", 420, 180);
        w.Owner = owner;
        var box = new TextBox { Style = L2Ui.S("GdText"), Width = 360, HorizontalAlignment = HorizontalAlignment.Left, Text = "My piece" };
        var buttons = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 10, 0, 0) };
        buttons.Children.Add(L2Ui.Button("Save", true, (_, _) => w.DialogResult = true));
        buttons.Children.Add(L2Ui.Button("Cancel", false, (_, _) => w.DialogResult = false));
        var sp = new StackPanel { Margin = new Thickness(12) };
        sp.Children.Add(L2Ui.Label("A name for the piece (Pieces... lists it with a ★ on maps with the same blocks):"));
        sp.Children.Add(box); sp.Children.Add(buttons);
        w.Content = sp;
        w.Loaded += (_, _) => { box.Focus(); box.SelectAll(); };
        return w.ShowDialog() == true ? box.Text : null;
    }
}

/// <summary>
/// The block editor: a block of the map's block set is four 8x8 tiles (top left, bottom left, top right, bottom right),
/// each a tile of the tile set with a colour row, flips and priority (over sprites). Changes are written into the block
/// set file (every map using it changes too); Edit &gt; Undo takes them back.
/// </summary>
public sealed class L2BlockEditorWindow : Window
{
    public bool Changed { get; private set; }
    private readonly RomBuffer _rom;
    private readonly MainViewModel _main;
    private readonly L2Maps.Map _map;
    private byte[] _set;
    private readonly byte[] _tiles;
    private readonly uint[][] _pals;
    private int _block, _quarter;
    private readonly Image _blocksImage = new() { Stretch = Stretch.None, Cursor = System.Windows.Input.Cursors.Hand, HorizontalAlignment = HorizontalAlignment.Left, VerticalAlignment = VerticalAlignment.Top };
    private readonly Image _big = new() { Width = 128, Height = 128, Stretch = Stretch.Fill, Cursor = System.Windows.Input.Cursors.Hand };
    private readonly Image _tilesImage = new() { Stretch = Stretch.None, Cursor = System.Windows.Input.Cursors.Hand, HorizontalAlignment = HorizontalAlignment.Left, VerticalAlignment = VerticalAlignment.Top };
    private readonly Rectangle _blockBox = new() { Width = 16, Height = 16, Stroke = Brushes.Gold, StrokeThickness = 1.5, IsHitTestVisible = false };
    private readonly Rectangle _quarterBox = new() { Width = 64, Height = 64, Stroke = Brushes.Gold, StrokeThickness = 2, IsHitTestVisible = false };
    private readonly Rectangle _tileBox = new() { Width = 8, Height = 8, Stroke = Brushes.Gold, StrokeThickness = 1, IsHitTestVisible = false };
    private readonly TextBox _tileText = new() { Width = 60 };
    private readonly ComboBox _row = new() { Width = 70 };
    private readonly CheckBox _hf = new() { Content = "Flip across" }, _vf = new() { Content = "Flip up-down" }, _prio = new() { Content = "Over sprites" };
    private readonly TextBlock _info = L2Ui.Label("");
    private bool _filling;
    private const int Cols = 16;

    public L2BlockEditorWindow(RomBuffer rom, MainViewModel main, L2Maps.Map map, int block)
    {
        _rom = rom; _main = main; _map = map; _block = Math.Max(0, block);
        _set = L2Rom.ReadFile(rom, map.BlockSet);
        _tiles = L2Rom.ReadFile(rom, map.TileSet);
        _pals = L2Maps.Palettes(rom, map.Id, map.PaletteSet);
        Title = "Block editor - Lufia Forge"; Width = 1150; Height = 800; WindowStartupLocation = WindowStartupLocation.CenterOwner; Background = L2Ui.B("MidPurpleBrush"); ShowInTaskbar = false;
        _tileText.Style = L2Ui.S("GdNumber"); _row.Style = L2Ui.S("LufiaComboBox");
        foreach (var c in new[] { _hf, _vf, _prio }) { c.Style = L2Ui.S("GdCheck"); c.Margin = new Thickness(0, 4, 12, 0); }
        for (int r = 0; r < 8; r++) _row.Items.Add(r.ToString());

        Grid Layered(Image img, params Rectangle[] boxes)
        {
            var g = new Grid { HorizontalAlignment = HorizontalAlignment.Left, VerticalAlignment = VerticalAlignment.Top };
            g.Children.Add(img);
            var c = new Canvas { IsHitTestVisible = false };
            foreach (var b in boxes) c.Children.Add(b);
            g.Children.Add(c);
            g.LayoutTransform = new ScaleTransform(2, 2);
            RenderOptions.SetBitmapScalingMode(g, BitmapScalingMode.NearestNeighbor);
            return g;
        }
        var blocksGrid = Layered(_blocksImage, _blockBox);
        var tilesGrid = Layered(_tilesImage, _tileBox);
        _blocksImage.MouseLeftButtonDown += (_, e) => { var p = e.GetPosition(_blocksImage); int b = (int)p.Y / 16 * Cols + (int)p.X / 16; if (b < Count) { _block = b; Fill(); } };
        _big.MouseLeftButtonDown += (_, e) => { var p = e.GetPosition(_big); _quarter = (p.X >= 64 ? 2 : 0) + (p.Y >= 64 ? 1 : 0); Fill(); };
        _tilesImage.MouseLeftButtonDown += (_, e) => { var p = e.GetPosition(_tilesImage); int t = (int)p.Y / 8 * Cols + (int)p.X / 8; if (t < _tiles.Length / 32) SetWord(w => w & ~0x3FF | t); };
        _tileText.LostFocus += (_, _) => { if (int.TryParse(_tileText.Text, System.Globalization.NumberStyles.HexNumber, null, out int t) && t < _tiles.Length / 32) SetWord(w => w & ~0x3FF | t); };
        _row.SelectionChanged += (_, _) => { if (_row.SelectedIndex >= 0) SetWord(w => w & ~0x1C00 | _row.SelectedIndex << 10); };
        _hf.Click += (_, _) => SetWord(w => _hf.IsChecked == true ? w | 0x4000 : w & ~0x4000);
        _vf.Click += (_, _) => SetWord(w => _vf.IsChecked == true ? w | 0x8000 : w & ~0x8000);
        _prio.Click += (_, _) => SetWord(w => _prio.IsChecked == true ? w | 0x2000 : w & ~0x2000);
        RenderOptions.SetBitmapScalingMode(_big, BitmapScalingMode.NearestNeighbor);

        var bigGrid = new Grid { Width = 128, Height = 128, HorizontalAlignment = HorizontalAlignment.Left };
        bigGrid.Children.Add(_big);
        var qc = new Canvas { IsHitTestVisible = false }; qc.Children.Add(_quarterBox); bigGrid.Children.Add(qc);

        int maps = Enumerable.Range(2, L2Maps.Count - 2).Count(m => rom.ReadByte(L2Maps.BlockSetList + m) == map.BlockSet - L2Maps.BlockSetFileBase);
        var mid = new StackPanel { Width = 300, Margin = new Thickness(12, 0, 12, 0) };
        mid.Children.Add(L2Ui.Label($"Block set {map.BlockSet - L2Maps.BlockSetFileBase} ({maps} map{(maps == 1 ? "" : "s")} use it: they all change). Click a block on the left, a quarter of it here, then a tile on the right."));
        mid.Children.Add(bigGrid);
        mid.Children.Add(_info);
        var tileRow = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 6, 0, 0) };
        tileRow.Children.Add(new TextBlock { Text = "Tile (hex) ", Style = L2Ui.S("GdFieldLabel"), VerticalAlignment = VerticalAlignment.Center }); tileRow.Children.Add(_tileText);
        tileRow.Children.Add(new TextBlock { Text = "  Colour row ", Style = L2Ui.S("GdFieldLabel"), VerticalAlignment = VerticalAlignment.Center }); tileRow.Children.Add(_row);
        mid.Children.Add(tileRow);
        mid.Children.Add(_hf); mid.Children.Add(_vf); mid.Children.Add(_prio);
        var buttons = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 14, 0, 0) };
        buttons.Children.Add(L2Ui.Button("Write to the ROM", true, (_, _) => Write()));
        buttons.Children.Add(L2Ui.Button("Close", false, (_, _) => Close()));
        mid.Children.Add(buttons);
        mid.Children.Add(L2Ui.Label("Colour rows 2-7 are the map's colours (Graphics > Map palettes); rows 0 and 1 show as row 7.", true));

        ScrollViewer Scroll(UIElement c) => new() { Content = c, HorizontalScrollBarVisibility = ScrollBarVisibility.Auto, VerticalScrollBarVisibility = ScrollBarVisibility.Auto, Background = new SolidColorBrush(Color.FromRgb(0x10, 0x10, 0x18)) };
        var grid = new Grid { Margin = new Thickness(12) };
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        var sb = Scroll(blocksGrid); var st = Scroll(tilesGrid);
        Grid.SetColumn(mid, 1); Grid.SetColumn(st, 2);
        grid.Children.Add(sb); grid.Children.Add(mid); grid.Children.Add(st);
        Content = grid;
        DrawBlocks(); DrawTiles(); Fill();
    }

    private int Count => Math.Max(0, (int)(short)(_set[2] | _set[3] << 8));
    private int Word(int b, int q) => _set[0x10 + b * 8 + q * 2] | _set[0x10 + b * 8 + q * 2 + 1] << 8;

    private void DrawTile(uint[] px, int w, int x0, int y0, int word, int rowOverride = -1)
    {
        int tile = word & 0x3FF, pal = rowOverride >= 0 ? rowOverride : word >> 10 & 7;
        bool hf = (word & 0x4000) != 0, vf = (word & 0x8000) != 0;
        var row = _pals[pal >= 2 ? pal - 2 : 5];
        int at = tile * 32;
        if (at + 32 > _tiles.Length) return;
        for (int r = 0; r < 8; r++)
        {
            int sr = vf ? 7 - r : r;
            byte p0 = _tiles[at + sr * 2], p1 = _tiles[at + sr * 2 + 1], p2 = _tiles[at + 0x10 + sr * 2], p3 = _tiles[at + 0x11 + sr * 2];
            for (int c = 0; c < 8; c++)
            {
                int bit = hf ? c : 7 - c;
                int ci = (p0 >> bit & 1) | (p1 >> bit & 1) << 1 | (p2 >> bit & 1) << 2 | (p3 >> bit & 1) << 3;
                int i = (y0 + r) * w + x0 + c;
                if (i < px.Length) px[i] = ci == 0 ? ((x0 + c) / 4 + (y0 + r) / 4) % 2 == 0 ? 0xFF202028u : 0xFF2A2A34u : row[ci];
            }
        }
    }

    private void DrawBlocks()
    {
        int rows = Math.Max(1, (Count + Cols - 1) / Cols), w = Cols * 16, h = rows * 16;
        var px = new uint[w * h];
        for (int b = 0; b < Count; b++)
            for (int q = 0; q < 4; q++) DrawTile(px, w, b % Cols * 16 + (q >= 2 ? 8 : 0), b / Cols * 16 + (q & 1) * 8, Word(b, q));
        _blocksImage.Source = L2Ui.Bitmap(w, h, px);
    }

    private void DrawTiles()
    {
        int n = _tiles.Length / 32, rows = Math.Max(1, (n + Cols - 1) / Cols), w = Cols * 8, h = rows * 8;
        int row = Count > 0 ? Word(_block, _quarter) >> 10 & 7 : 2;
        var px = new uint[w * h];
        for (int t = 0; t < n; t++) DrawTile(px, w, t % Cols * 8, t / Cols * 8, t, row);
        _tilesImage.Source = L2Ui.Bitmap(w, h, px);
    }

    private void Fill()
    {
        if (Count == 0) return;
        _block = Math.Min(_block, Count - 1);
        _filling = true;
        var px = new uint[256];
        for (int q = 0; q < 4; q++) DrawTile(px, 16, q >= 2 ? 8 : 0, (q & 1) * 8, Word(_block, q));
        _big.Source = L2Ui.Bitmap(16, 16, px);
        Canvas.SetLeft(_blockBox, _block % Cols * 16); Canvas.SetTop(_blockBox, _block / Cols * 16);
        Canvas.SetLeft(_quarterBox, _quarter >= 2 ? 64 : 0); Canvas.SetTop(_quarterBox, (_quarter & 1) * 64);
        int w = Word(_block, _quarter);
        _tileText.Text = (w & 0x3FF).ToString("X3");
        _row.SelectedIndex = w >> 10 & 7;
        _hf.IsChecked = (w & 0x4000) != 0; _vf.IsChecked = (w & 0x8000) != 0; _prio.IsChecked = (w & 0x2000) != 0;
        Canvas.SetLeft(_tileBox, (w & 0x3FF) % Cols * 8); Canvas.SetTop(_tileBox, (w & 0x3FF) / Cols * 8);
        string[] qn = { "top left", "bottom left", "top right", "bottom right" };
        _info.Text = $"Block {_block:X3}, {qn[_quarter]} quarter: tile {w & 0x3FF:X3}, colour row {w >> 10 & 7}.";
        _filling = false;
        DrawTiles();
    }

    private void SetWord(Func<int, int> change)
    {
        if (_filling || Count == 0) return;
        int at = 0x10 + _block * 8 + _quarter * 2;
        int old = Word(_block, _quarter), w = change(old) & 0xFFFF;
        if (w == old) return;
        _set[at] = (byte)w; _set[at + 1] = (byte)(w >> 8);
        _dirty = true;
        DrawBlocks(); Fill();
    }
    private bool _dirty;

    private void Write()
    {
        if (!_dirty) { _info.Text = "Nothing changed yet."; return; }
        try
        {
            L2Rom.WriteFile(_rom, _map.BlockSet, _set);
            _main.NotifyRomModified($"Lufia II: block set {_map.BlockSet - L2Maps.BlockSetFileBase}");
            _set = L2Rom.ReadFile(_rom, _map.BlockSet);
            _dirty = false; Changed = true;
            _info.Text = "Written. Edit > Undo takes it back.";
        }
        catch (Exception ex) { _info.Text = "Not written: " + ex.Message; }
    }

    protected override void OnClosing(System.ComponentModel.CancelEventArgs e)
    {
        if (_dirty && System.Windows.MessageBox.Show(this, "Write the changed blocks to the ROM before closing?", "Block editor", MessageBoxButton.YesNo) == MessageBoxResult.Yes) Write();
        base.OnClosing(e);
    }
}
