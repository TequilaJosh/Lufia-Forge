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

// bits for the Lufia II map windows (they're built in code, styled from the app's theme)
internal static class L2Ui
{
    private static ResourceDictionary? _gameDataStyles;
    // a style from the app or from the Game Data styles (GdNumber, GdCombo...)
    public static Style? S(string key)
    {
        _gameDataStyles ??= new ResourceDictionary { Source = new Uri("/LufiaForge;component/Modules/GameData/GameDataStyles.xaml", UriKind.Relative) };
        return (_gameDataStyles.Contains(key) ? _gameDataStyles[key] : System.Windows.Application.Current.TryFindResource(key)) as Style;
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
        var button = new Button { Content = text, Style = S(gold ? "LufiaButtonGold" : "LufiaButton"), Padding = new Thickness(12, 4, 12, 4), Margin = new Thickness(0, 0, 6, 0) };
        button.Click += click;
        return button;
    }

    public static BitmapSource Bitmap(int w, int h, uint[] pixels) =>
        BitmapSource.Create(w, h, 96, 96, PixelFormats.Bgra32, null, pixels.Select(c => (int)(c == 0 ? 0xFF000000u : c)).ToArray(), w * 4);
}

// pick a block on a map (where an exit leads)
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
    // pixels per 16-pixel block in the picture (the world map is drawn at half size here)
    private int Unit => Map == 0 ? 8 : 16;

    public L2PickPlaceWindow(RomBuffer rom, int map, int x, int y, Func<int, string> name)
    {
        _rom = rom; Map = map; X = x; Y = y; _name = name;
        Title = "Where the exit leads - Lufia Forge"; Width = 980; Height = 760; WindowStartupLocation = WindowStartupLocation.CenterOwner; Background = L2Ui.B("MidPurpleBrush"); ShowInTaskbar = false;
        var maps = new ComboBox { Style = L2Ui.S("LufiaComboBox"), Width = 320, HorizontalAlignment = HorizontalAlignment.Left, Margin = new Thickness(0, 0, 0, 6) };
        // the world map first, then the towns and dungeons (the sea floor is reached by events only)
        var mapIds = new List<int> { 0 };
        mapIds.AddRange(Enumerable.Range(2, L2Maps.Count(rom) - 2));
        foreach (int m in mapIds) maps.Items.Add(m == 0 ? "00  World map" : $"{m:X2}  {name(m)}");
        maps.SelectedIndex = Math.Max(0, mapIds.IndexOf(map));
        maps.SelectionChanged += (_, _) => { Map = mapIds[Math.Max(0, maps.SelectedIndex)]; Draw(); _mark.BringIntoView(); };
        var canvas = new Canvas { IsHitTestVisible = false };
        canvas.Children.Add(_mark);
        var grid = new Grid { HorizontalAlignment = HorizontalAlignment.Left, VerticalAlignment = VerticalAlignment.Top };
        grid.Children.Add(_image); grid.Children.Add(_labels); grid.Children.Add(canvas);
        _image.MouseLeftButtonDown += (_, e) => { var click = e.GetPosition(_image); X = (int)click.X / Unit; Y = (int)click.Y / Unit; Mark(); if (e.ClickCount == 2) { DialogResult = true; } };
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
                var (worldWidth, worldHeight, worldPixels) = L2WorldMap.Render(_rom, 2);
                _image.Source = L2Ui.Bitmap(worldWidth, worldHeight, worldPixels);
                // label where each entrance leads so the towns can be found
                foreach (var exit in L2MapInfo.Read(_rom, 0).Where(e => e.Kind == "Exit" && e.DestMap >= 2))
                {
                    var label = new TextBlock { Text = _name(exit.DestMap), Foreground = Brushes.White, FontSize = 9, Background = new SolidColorBrush(Color.FromArgb(0xA0, 0, 0, 0)), Padding = new Thickness(2, 0, 2, 0) };
                    Canvas.SetLeft(label, exit.X1 * 8); Canvas.SetTop(label, exit.Y1 * 8 - 12);
                    _labels.Children.Add(label);
                    var outline = new Rectangle { Width = Math.Max(1, exit.X2 - exit.X1) * 8, Height = Math.Max(1, exit.Y2 - exit.Y1) * 8, Stroke = Brushes.LimeGreen, StrokeThickness = 1 };
                    Canvas.SetLeft(outline, exit.X1 * 8); Canvas.SetTop(outline, exit.Y1 * 8);
                    _labels.Children.Add(outline);
                }
            }
            else { var (w, h, pixels) = L2Maps.Render(_rom, Map); _image.Source = L2Ui.Bitmap(w, h, pixels); }
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

// pieces the user kept (an area of blocks with its walls, for a block set and tile set), saved in AppData
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
        // the map it was kept from and its palette set (-1 for presets kept before 2026-11-02)
        public int Map { get; set; } = -1;
        public int PaletteSet { get; set; } = -1;
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

    public static void Add(string name, L2MapsPage.Clip clip)
    {
        var list = Load();
        list.RemoveAll(p => p.Name == name && p.BlockSet == clip.BlockSet);
        list.Add(new Preset { Name = name, BlockSet = clip.BlockSet, TileSet = clip.TileSet, W = clip.W, H = clip.H, Ground = clip.Ground, Upper = clip.Upper, Flags = clip.Flags, Map = clip.SourceMap, PaletteSet = clip.PaletteSet });
        Save(list);
    }

    public static L2MapsPage.Clip ToClip(Preset preset) => new(preset.W, preset.H, preset.Ground, preset.Upper, preset.Flags, preset.BlockSet, preset.TileSet, preset.Map, preset.PaletteSet);
}

// copy part of another map (or a kept preset) to place on this one
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
    // the two lists: all of the user's presets together, or the maps with these blocks
    private readonly List<(string Label, int Map, L2Presets.Preset? Preset)> _presets = new(), _maps = new();
    private readonly System.Windows.Controls.RadioButton _showPresets = new() { GroupName = "PiecesShow", Margin = new Thickness(0, 0, 4, 0) };
    private readonly System.Windows.Controls.RadioButton _showMaps = new() { GroupName = "PiecesShow", Content = "Pieces of maps" };
    private readonly TextBlock _listNote = L2Ui.Label("", true);
    private L2Maps.Map? _map;
    private (int X, int Y)? _start;
    private (int X1, int Y1, int X2, int Y2)? _rect;

    public L2PiecesWindow(RomBuffer rom, int currentMap, int blockSet, int tileSet, Func<int, string> name)
    {
        _rom = rom; _blockSet = blockSet; _tileSet = tileSet;
        Title = "Pieces and presets - Lufia Forge"; Width = 1100; Height = 780; WindowStartupLocation = WindowStartupLocation.CenterOwner; Background = L2Ui.B("MidPurpleBrush"); ShowInTaskbar = false;
        // every preset in one list by name, wherever it was kept. ones from other graphics get their blocks added
        // to this map's set when used
        foreach (var preset in L2Presets.Load().OrderBy(p => p.Name, StringComparer.CurrentCultureIgnoreCase))
            _presets.Add(($"★ {preset.Name}  ({preset.W} x {preset.H}{(preset.BlockSet == blockSet ? "" : ", other graphics")})", -1, preset));
        for (int m = 2; m < L2Maps.Count(rom); m++)
            if (rom.ReadByte(L2Maps.BlockSetAt(rom, m)) == blockSet)
                _maps.Add(($"{m:X2}  {name(m)}{(rom.ReadByte(L2Maps.TileSetAt(rom, m)) == tileSet ? "" : "  (other tiles)")}{(m == currentMap ? "  (this map)" : "")}", m, null));
        _list.Style = L2Ui.S("GdRecordList");
        _showPresets.Style = _showMaps.Style = L2Ui.S("LufiaToolRadio");
        _showPresets.Checked += (_, _) => Fill();
        _showMaps.Checked += (_, _) => Fill();
        _list.SelectionChanged += (_, _) => Show();
        var canvas = new Canvas { IsHitTestVisible = false }; canvas.Children.Add(_box);
        var grid = new Grid { HorizontalAlignment = HorizontalAlignment.Left, VerticalAlignment = VerticalAlignment.Top };
        grid.Children.Add(_image); grid.Children.Add(canvas);
        _image.MouseLeftButtonDown += (_, e) => { if (_map == null) return; var click = e.GetPosition(_image); _start = ((int)click.X / 16, (int)click.Y / 16); Drag(click); _image.CaptureMouse(); };
        _image.MouseMove += (_, e) => { if (_start != null && e.LeftButton == MouseButtonState.Pressed) Drag(e.GetPosition(_image)); };
        _image.MouseLeftButtonUp += (_, _) => { _start = null; _image.ReleaseMouseCapture(); };
        var scroll = new ScrollViewer { Content = grid, HorizontalScrollBarVisibility = ScrollBarVisibility.Auto, VerticalScrollBarVisibility = ScrollBarVisibility.Auto, Background = Brushes.Black };
        LufiaForge.Modules.Common.ScrollPan.SetMiddleDrag(scroll, true);
        var buttons = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 8, 0, 0) };
        buttons.Children.Add(L2Ui.Button("Use it", true, (_, _) => Use()));
        buttons.Children.Add(L2Ui.Button("Delete this preset", false, (_, _) => DeletePreset()));
        buttons.Children.Add(L2Ui.Button("Cancel", false, (_, _) => DialogResult = false));
        var left = new DockPanel { Width = 300, Margin = new Thickness(0, 0, 10, 0) };
        var show = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 0, 0, 6) };
        show.Children.Add(_showPresets); show.Children.Add(_showMaps);
        DockPanel.SetDock(show, Dock.Top); left.Children.Add(show);
        _listNote.Margin = new Thickness(0, 0, 0, 6);
        DockPanel.SetDock(_listNote, Dock.Top); left.Children.Add(_listNote); left.Children.Add(_list);
        var right = new DockPanel();
        DockPanel.SetDock(_note, Dock.Top); DockPanel.SetDock(buttons, Dock.Bottom);
        right.Children.Add(_note); right.Children.Add(buttons); right.Children.Add(scroll);
        var dock = new DockPanel { Margin = new Thickness(12) };
        DockPanel.SetDock(left, Dock.Left);
        dock.Children.Add(left); dock.Children.Add(right);
        Content = dock;
        // the presets first, when there are any
        if (_presets.Count > 0) _showPresets.IsChecked = true; else _showMaps.IsChecked = true;
    }

    // fills the list with the presets or the maps
    private void Fill()
    {
        bool showingPresets = _showPresets.IsChecked == true;
        _showPresets.Content = $"★ My presets ({_presets.Count})";
        _entries.Clear(); _entries.AddRange(showingPresets ? _presets : _maps);
        _list.Items.Clear();
        foreach (var entry in _entries) _list.Items.Add(entry.Label);
        _listNote.Text = showingPresets
            ? "Every preset you kept (Save as preset), on any map. Those from other graphics have their blocks added to this map's block set when used."
            : $"The maps with block set {_blockSet} (the same blocks as this map): drag around a piece of one.";
        _list.SelectedIndex = _entries.Count > 0 ? 0 : -1;
        if (_entries.Count == 0)
        {
            _image.Source = null;
            _note.Text = showingPresets ? "No presets yet: select a piece with the Area tool and click Save as preset." : "No other map uses this block set.";
        }
    }

    private (string Label, int Map, L2Presets.Preset? Preset)? Entry => _list.SelectedIndex >= 0 ? _entries[_list.SelectedIndex] : null;

    private void Show()
    {
        _rect = null; _box.Visibility = Visibility.Collapsed; _map = null;
        if (Entry is not { } entry) return;
        try
        {
            if (entry.Preset is { } preset)
            {
                var clip = L2Presets.ToClip(preset);
                // a preset gets drawn as a little map of its own
                var presetMap = new L2Maps.Map { Id = preset.Map >= 2 && preset.Map < L2Maps.Count(_rom) && _rom.ReadByte(L2Maps.BlockSetAt(_rom, preset.Map)) == preset.BlockSet ? preset.Map : FirstMapWith(preset.BlockSet), PaletteSet = preset.PaletteSet, BlockSet = L2Maps.BlockSetFileBase + preset.BlockSet, TileSet = L2Maps.TileSetFileBase + preset.TileSet,
                    Layers = new[] { new L2Maps.Layer { Width = preset.W, Height = preset.H, Blocks = preset.Ground, Flags = new byte[preset.W * preset.H] }, new L2Maps.Layer { Width = preset.W, Height = preset.H, Blocks = preset.Upper, Flags = preset.Flags } } };
                var (w, h, pixels) = L2Maps.Render(_rom, presetMap);
                _image.Source = L2Ui.Bitmap(w, h, pixels);
                _note.Text = preset.BlockSet == _blockSet && preset.TileSet == _tileSet ? $"Your preset \"{preset.Name}\". Use it to place it on the map."
                    : $"Your preset \"{preset.Name}\", kept on block set {preset.BlockSet} / tile set {preset.TileSet}. Use it: the blocks this map's block set lacks are added to it (and tiles to its tile set), then place it.";
                return;
            }
            _map = L2Maps.Read(_rom, entry.Map);
            var (mapWidth, mapHeight, mapPixels) = L2Maps.Render(_rom, _map);
            _image.Source = L2Ui.Bitmap(mapWidth, mapHeight, mapPixels);
            _note.Text = "Drag a rectangle around the piece you want (both layers and their walls are copied), then Use it.";
        }
        catch (Exception ex) { _image.Source = null; _note.Text = "Couldn't draw it: " + ex.Message; }
    }

    private int FirstMapWith(int blockSet) { for (int m = 2; m < L2Maps.Count(_rom); m++) if (_rom.ReadByte(L2Maps.BlockSetAt(_rom, m)) == blockSet) return m; return 3; }

    private void Drag(Point mouse)
    {
        if (_start is not { } start || _map == null) return;
        int x = Math.Clamp((int)mouse.X / 16, 0, _map.Layers[0].Width - 1), y = Math.Clamp((int)mouse.Y / 16, 0, _map.Layers[0].Height - 1);
        _rect = (Math.Min(start.X, x), Math.Min(start.Y, y), Math.Max(start.X, x) + 1, Math.Max(start.Y, y) + 1);
        var rect = _rect.Value;
        Canvas.SetLeft(_box, rect.X1 * 16); Canvas.SetTop(_box, rect.Y1 * 16); _box.Width = (rect.X2 - rect.X1) * 16; _box.Height = (rect.Y2 - rect.Y1) * 16;
        _box.Visibility = Visibility.Visible;
        _note.Text = $"{rect.X2 - rect.X1} x {rect.Y2 - rect.Y1} blocks from ({rect.X1},{rect.Y1}).";
    }

    private void Use()
    {
        if (Entry is not { } entry) return;
        if (entry.Preset is { } preset) { Picked = L2Presets.ToClip(preset); DialogResult = true; return; }
        if (_map == null || _rect is not { } rect) { _note.Text = "Drag a rectangle on the map first."; return; }
        int w = rect.X2 - rect.X1, h = rect.Y2 - rect.Y1;
        var ground = new int[w * h]; var upper = new int[w * h]; var flags = new byte[w * h];
        var groundLayer = _map.Layers[0]; var upperLayer = _map.Layers.Length > 1 ? _map.Layers[1] : null;
        for (int y = 0; y < h; y++)
            for (int x = 0; x < w; x++)
            {
                int i = (rect.Y1 + y) * groundLayer.Width + rect.X1 + x;
                ground[y * w + x] = groundLayer.Blocks[i];
                if (upperLayer != null) { upper[y * w + x] = upperLayer.Blocks[i]; flags[y * w + x] = upperLayer.Flags[i]; }
            }
        Picked = new L2MapsPage.Clip(w, h, ground, upper, flags, _rom.ReadByte(L2Maps.BlockSetAt(_rom, entry.Map)), _rom.ReadByte(L2Maps.TileSetAt(_rom, entry.Map)));
        DialogResult = true;
    }

    private void DeletePreset()
    {
        if (Entry is not { Preset: { } preset }) { _note.Text = "Pick one of your presets (★) first."; return; }
        var list = L2Presets.Load();
        list.RemoveAll(x => x.Name == preset.Name && x.BlockSet == preset.BlockSet);
        L2Presets.Save(list);
        _presets.RemoveAll(x => x.Preset == preset);
        Fill();
        _note.Text = $"Deleted \"{preset.Name}\".";
    }

    // asks for a preset's name
    public static string? AskName(Window? owner)
    {
        var window = L2Ui.Window("Save as preset", 420, 180);
        window.Owner = owner;
        var box = new TextBox { Style = L2Ui.S("GdText"), Width = 360, HorizontalAlignment = HorizontalAlignment.Left, Text = "My piece" };
        var buttons = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 10, 0, 0) };
        buttons.Children.Add(L2Ui.Button("Save", true, (_, _) => window.DialogResult = true));
        buttons.Children.Add(L2Ui.Button("Cancel", false, (_, _) => window.DialogResult = false));
        var panel = new StackPanel { Margin = new Thickness(12) };
        panel.Children.Add(L2Ui.Label("A name for the piece (\"Pieces and ★ presets...\", under the blocks, lists it with a ★ on every map):"));
        panel.Children.Add(box); panel.Children.Add(buttons);
        window.Content = panel;
        window.Loaded += (_, _) => { box.Focus(); box.SelectAll(); };
        return window.ShowDialog() == true ? box.Text : null;
    }
}

// the block editor. a block of the map's block set is four 8x8 tiles (top left, bottom left, top right, bottom right),
// each one a tile of the tile set with a colour row, flips and priority (over sprites). changes get written into the
// block set file, so every map using it changes too. Edit > Undo takes them back
public sealed class L2BlockEditorWindow : Window
{
    public bool Changed { get; private set; }
    private readonly RomBuffer _rom;
    private readonly MainViewModel _main;
    private readonly L2Maps.Map _map;
    private byte[] _set;
    private readonly byte[] _tiles;
    private readonly uint[][] _palettes;
    private int _block, _quarter;
    private readonly Image _blocksImage = new() { Stretch = Stretch.None, Cursor = System.Windows.Input.Cursors.Hand, HorizontalAlignment = HorizontalAlignment.Left, VerticalAlignment = VerticalAlignment.Top };
    private readonly Image _big = new() { Width = 128, Height = 128, Stretch = Stretch.Fill, Cursor = System.Windows.Input.Cursors.Hand };
    private readonly Image _tilesImage = new() { Stretch = Stretch.None, Cursor = System.Windows.Input.Cursors.Hand, HorizontalAlignment = HorizontalAlignment.Left, VerticalAlignment = VerticalAlignment.Top };
    private readonly Rectangle _blockBox = new() { Width = 16, Height = 16, Stroke = Brushes.Gold, StrokeThickness = 1.5, IsHitTestVisible = false };
    private readonly Rectangle _quarterBox = new() { Width = 64, Height = 64, Stroke = Brushes.Gold, StrokeThickness = 2, IsHitTestVisible = false };
    private readonly Rectangle _tileBox = new() { Width = 8, Height = 8, Stroke = Brushes.Gold, StrokeThickness = 1, IsHitTestVisible = false };
    private readonly TextBox _tileText = new() { Width = 60 };
    private readonly ComboBox _row = new() { Width = 70 };
    private readonly CheckBox _flipAcross = new() { Content = "Flip across" }, _flipUpDown = new() { Content = "Flip up-down" }, _overSprites = new() { Content = "Over sprites" };
    private readonly TextBlock _info = L2Ui.Label("");
    private bool _filling;
    private const int Cols = 16;

    public L2BlockEditorWindow(RomBuffer rom, MainViewModel main, L2Maps.Map map, int block)
    {
        _rom = rom; _main = main; _map = map; _block = Math.Max(0, block);
        _set = L2Rom.ReadFile(rom, map.BlockSet);
        _tiles = L2Rom.ReadFile(rom, map.TileSet);
        _palettes = L2Maps.Palettes(rom, map.Id, map.PaletteSet);
        Title = "Block editor - Lufia Forge"; Width = 1150; Height = 800; WindowStartupLocation = WindowStartupLocation.CenterOwner; Background = L2Ui.B("MidPurpleBrush"); ShowInTaskbar = false;
        _tileText.Style = L2Ui.S("GdNumber"); _row.Style = L2Ui.S("LufiaComboBox");
        foreach (var check in new[] { _flipAcross, _flipUpDown, _overSprites }) { check.Style = L2Ui.S("GdCheck"); check.Margin = new Thickness(0, 4, 12, 0); }
        for (int r = 0; r < 8; r++) _row.Items.Add(r.ToString());

        Grid Layered(Image img, params Rectangle[] boxes)
        {
            var layered = new Grid { HorizontalAlignment = HorizontalAlignment.Left, VerticalAlignment = VerticalAlignment.Top };
            layered.Children.Add(img);
            var overlay = new Canvas { IsHitTestVisible = false };
            foreach (var box in boxes) overlay.Children.Add(box);
            layered.Children.Add(overlay);
            layered.LayoutTransform = new ScaleTransform(2, 2);
            RenderOptions.SetBitmapScalingMode(layered, BitmapScalingMode.NearestNeighbor);
            return layered;
        }
        var blocksGrid = Layered(_blocksImage, _blockBox);
        var tilesGrid = Layered(_tilesImage, _tileBox);
        _blocksImage.MouseLeftButtonDown += (_, e) => { var click = e.GetPosition(_blocksImage); int clicked = (int)click.Y / 16 * Cols + (int)click.X / 16; if (clicked < Count) { _block = clicked; Fill(); } };
        _big.MouseLeftButtonDown += (_, e) => { var click = e.GetPosition(_big); _quarter = (click.X >= 64 ? 2 : 0) + (click.Y >= 64 ? 1 : 0); Fill(); };
        _tilesImage.MouseLeftButtonDown += (_, e) => { var click = e.GetPosition(_tilesImage); int tile = (int)click.Y / 8 * Cols + (int)click.X / 8; if (tile < _tiles.Length / 32) SetWord(w => w & ~0x3FF | tile); };
        _tileText.LostFocus += (_, _) => { if (int.TryParse(_tileText.Text, System.Globalization.NumberStyles.HexNumber, null, out int tile) && tile < _tiles.Length / 32) SetWord(w => w & ~0x3FF | tile); };
        _row.SelectionChanged += (_, _) => { if (_row.SelectedIndex >= 0) SetWord(w => w & ~0x1C00 | _row.SelectedIndex << 10); };
        _flipAcross.Click += (_, _) => SetWord(w => _flipAcross.IsChecked == true ? w | 0x4000 : w & ~0x4000);
        _flipUpDown.Click += (_, _) => SetWord(w => _flipUpDown.IsChecked == true ? w | 0x8000 : w & ~0x8000);
        _overSprites.Click += (_, _) => SetWord(w => _overSprites.IsChecked == true ? w | 0x2000 : w & ~0x2000);
        RenderOptions.SetBitmapScalingMode(_big, BitmapScalingMode.NearestNeighbor);

        var bigGrid = new Grid { Width = 128, Height = 128, HorizontalAlignment = HorizontalAlignment.Left };
        bigGrid.Children.Add(_big);
        var quarterCanvas = new Canvas { IsHitTestVisible = false }; quarterCanvas.Children.Add(_quarterBox); bigGrid.Children.Add(quarterCanvas);

        int mapCount = Enumerable.Range(2, L2Maps.Count(rom) - 2).Count(m => rom.ReadByte(L2Maps.BlockSetAt(rom, m)) == map.BlockSet - L2Maps.BlockSetFileBase);
        var mid = new StackPanel { Width = 300, Margin = new Thickness(12, 0, 12, 0) };
        mid.Children.Add(L2Ui.Label($"Block set {map.BlockSet - L2Maps.BlockSetFileBase} ({mapCount} map{(mapCount == 1 ? "" : "s")} use it: they all change). Click a block on the left, a quarter of it here, then a tile on the right."));
        mid.Children.Add(bigGrid);
        mid.Children.Add(_info);
        var tileRow = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 6, 0, 0) };
        tileRow.Children.Add(new TextBlock { Text = "Tile (hex) ", Style = L2Ui.S("GdFieldLabel"), VerticalAlignment = VerticalAlignment.Center }); tileRow.Children.Add(_tileText);
        tileRow.Children.Add(new TextBlock { Text = "  Colour row ", Style = L2Ui.S("GdFieldLabel"), VerticalAlignment = VerticalAlignment.Center }); tileRow.Children.Add(_row);
        mid.Children.Add(tileRow);
        mid.Children.Add(_flipAcross); mid.Children.Add(_flipUpDown); mid.Children.Add(_overSprites);
        var buttons = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 14, 0, 0) };
        buttons.Children.Add(L2Ui.Button("Write to the ROM", true, (_, _) => Write()));
        buttons.Children.Add(L2Ui.Button("Close", false, (_, _) => Close()));
        mid.Children.Add(buttons);
        mid.Children.Add(L2Ui.Label("Colour rows 2-7 are the map's colours (Graphics > Map palettes); rows 0 and 1 show as row 7.", true));

        ScrollViewer Scroll(UIElement content) => new() { Content = content, HorizontalScrollBarVisibility = ScrollBarVisibility.Auto, VerticalScrollBarVisibility = ScrollBarVisibility.Auto, Background = new SolidColorBrush(Color.FromRgb(0x10, 0x10, 0x18)) };
        var grid = new Grid { Margin = new Thickness(12) };
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        var blocksScroll = Scroll(blocksGrid); var tilesScroll = Scroll(tilesGrid);
        Grid.SetColumn(mid, 1); Grid.SetColumn(tilesScroll, 2);
        grid.Children.Add(blocksScroll); grid.Children.Add(mid); grid.Children.Add(tilesScroll);
        Content = grid;
        DrawBlocks(); DrawTiles(); Fill();
    }

    private int Count => Math.Max(0, (int)(short)(_set[2] | _set[3] << 8));
    private int Word(int block, int quarter) => _set[0x10 + block * 8 + quarter * 2] | _set[0x10 + block * 8 + quarter * 2 + 1] << 8;

    private void DrawTile(uint[] pixels, int w, int x0, int y0, int word, int rowOverride = -1)
    {
        int tile = word & 0x3FF, palette = rowOverride >= 0 ? rowOverride : word >> 10 & 7;
        bool flipX = (word & 0x4000) != 0, flipY = (word & 0x8000) != 0;
        var row = _palettes[palette >= 2 ? palette - 2 : 5];
        int tileOffset = tile * 32;
        if (tileOffset + 32 > _tiles.Length) return;
        for (int r = 0; r < 8; r++)
        {
            int sourceRow = flipY ? 7 - r : r;
            byte p0 = _tiles[tileOffset + sourceRow * 2], p1 = _tiles[tileOffset + sourceRow * 2 + 1], p2 = _tiles[tileOffset + 0x10 + sourceRow * 2], p3 = _tiles[tileOffset + 0x11 + sourceRow * 2];
            for (int c = 0; c < 8; c++)
            {
                int bit = flipX ? c : 7 - c;
                int colorIndex = (p0 >> bit & 1) | (p1 >> bit & 1) << 1 | (p2 >> bit & 1) << 2 | (p3 >> bit & 1) << 3;
                int i = (y0 + r) * w + x0 + c;
                if (i < pixels.Length) pixels[i] = colorIndex == 0 ? ((x0 + c) / 4 + (y0 + r) / 4) % 2 == 0 ? 0xFF202028u : 0xFF2A2A34u : row[colorIndex];
            }
        }
    }

    private void DrawBlocks()
    {
        int rows = Math.Max(1, (Count + Cols - 1) / Cols), w = Cols * 16, h = rows * 16;
        var pixels = new uint[w * h];
        for (int block = 0; block < Count; block++)
            for (int q = 0; q < 4; q++) DrawTile(pixels, w, block % Cols * 16 + (q >= 2 ? 8 : 0), block / Cols * 16 + (q & 1) * 8, Word(block, q));
        _blocksImage.Source = L2Ui.Bitmap(w, h, pixels);
    }

    private void DrawTiles()
    {
        int tileCount = _tiles.Length / 32, rows = Math.Max(1, (tileCount + Cols - 1) / Cols), w = Cols * 8, h = rows * 8;
        int row = Count > 0 ? Word(_block, _quarter) >> 10 & 7 : 2;
        var pixels = new uint[w * h];
        for (int tile = 0; tile < tileCount; tile++) DrawTile(pixels, w, tile % Cols * 8, tile / Cols * 8, tile, row);
        _tilesImage.Source = L2Ui.Bitmap(w, h, pixels);
    }

    private void Fill()
    {
        if (Count == 0) return;
        _block = Math.Min(_block, Count - 1);
        _filling = true;
        var pixels = new uint[256];
        for (int q = 0; q < 4; q++) DrawTile(pixels, 16, q >= 2 ? 8 : 0, (q & 1) * 8, Word(_block, q));
        _big.Source = L2Ui.Bitmap(16, 16, pixels);
        Canvas.SetLeft(_blockBox, _block % Cols * 16); Canvas.SetTop(_blockBox, _block / Cols * 16);
        Canvas.SetLeft(_quarterBox, _quarter >= 2 ? 64 : 0); Canvas.SetTop(_quarterBox, (_quarter & 1) * 64);
        int word = Word(_block, _quarter);
        _tileText.Text = (word & 0x3FF).ToString("X3");
        _row.SelectedIndex = word >> 10 & 7;
        _flipAcross.IsChecked = (word & 0x4000) != 0; _flipUpDown.IsChecked = (word & 0x8000) != 0; _overSprites.IsChecked = (word & 0x2000) != 0;
        Canvas.SetLeft(_tileBox, (word & 0x3FF) % Cols * 8); Canvas.SetTop(_tileBox, (word & 0x3FF) / Cols * 8);
        string[] quarterNames = { "top left", "bottom left", "top right", "bottom right" };
        _info.Text = $"Block {_block:X3}, {quarterNames[_quarter]} quarter: tile {word & 0x3FF:X3}, colour row {word >> 10 & 7}.";
        _filling = false;
        DrawTiles();
    }

    private void SetWord(Func<int, int> change)
    {
        if (_filling || Count == 0) return;
        int wordOffset = 0x10 + _block * 8 + _quarter * 2;
        int old = Word(_block, _quarter), word = change(old) & 0xFFFF;
        if (word == old) return;
        _set[wordOffset] = (byte)word; _set[wordOffset + 1] = (byte)(word >> 8);
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
