using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using LufiaForge.Core;
using LufiaForge.Core.Lufia2;
using MouseEventArgs = System.Windows.Input.MouseEventArgs;
using Point = System.Windows.Point;
using Brush = System.Windows.Media.Brush;
using Brushes = System.Windows.Media.Brushes;
using Color = System.Windows.Media.Color;
using Rectangle = System.Windows.Shapes.Rectangle;
using Orientation = System.Windows.Controls.Orientation;
using Image = System.Windows.Controls.Image;

namespace LufiaForge.Modules.Lufia2;

// makes a new Lufia II cutscene on any map. it can start when the party enters the map (once, while a story flag
// allows), when they step onto an area dragged on the map, when they talk to one of the map's characters (or to a
// new one, put where you click), or it's a scene another cutscene's "Go to another map" step sends the party to
public partial class L2NewCutsceneWindow : Window
{
    private readonly L2CutscenesPage _page;
    private readonly RomBuffer _rom;
    private int _map = -1;
    private readonly List<int> _maps = new();
    private Point? _dragFrom;
    private (int X1, int Y1, int X2, int Y2)? _area;
    private (int X, int Y)? _spot;
    private HashSet<int> _talked = new();
    private readonly Dictionary<int, BitmapSource?> _sprites = new();

    public L2NewCutsceneWindow(L2CutscenesPage page, int map)
    {
        InitializeComponent();
        _page = page;
        _rom = page.CurrentRom!;
        for (int mapNumber = 0; mapNumber < L2Maps.Count(_rom); mapNumber++)
        {
            int block;
            try { block = L2Events.Block(_rom, mapNumber); } catch (Exception) { continue; }
            if (block < 0) continue;
            _maps.Add(mapNumber);
            MapCombo.Items.Add($"{mapNumber:X2}  {L2CutscenesPage.MapName(_rom, mapNumber)}");
        }
        for (int s = 0; s < L2Sprites.Count(_rom); s++)
        {
            var row = new StackPanel { Orientation = Orientation.Horizontal, Tag = s };
            var spriteImage = new Image { Source = Sprite(s), Width = 18, Height = 24, Stretch = Stretch.Uniform, Margin = new Thickness(0, 0, 6, 0) };
            RenderOptions.SetBitmapScalingMode(spriteImage, BitmapScalingMode.NearestNeighbor);
            row.Children.Add(spriteImage);
            row.Children.Add(new TextBlock { Text = $"{s:X2}  {(s < 7 ? L2Rom.Heroes[s] : L2Sprites.Name(s))}", VerticalAlignment = VerticalAlignment.Center });
            SpriteCombo.Items.Add(row);
        }
        SpriteCombo.SelectedIndex = Math.Min(0x0C, SpriteCombo.Items.Count - 1);
        int mapIndex = _maps.IndexOf(map);
        MapCombo.SelectedIndex = mapIndex >= 0 ? mapIndex : _maps.IndexOf(3) >= 0 ? _maps.IndexOf(3) : 0;
    }

    private BitmapSource? Sprite(int spriteId)
    {
        if (_sprites.TryGetValue(spriteId, out var cached)) return cached;
        BitmapSource? bitmap = null;
        try
        {
            if (L2Graphics.MapSprite(_rom, spriteId) is var (w, h, pixels))
            {
                bitmap = BitmapSource.Create(w, h, 96, 96, PixelFormats.Bgra32, null, pixels.Select(c => (int)c).ToArray(), w * 4);
                bitmap.Freeze();
            }
        }
        catch (Exception) { }
        return _sprites[spriteId] = bitmap;
    }

    private void Map_Changed(object sender, SelectionChangedEventArgs e)
    {
        if (MapCombo.SelectedIndex < 0) return;
        _map = _maps[MapCombo.SelectedIndex];
        _area = null; _spot = null;
        int block = L2Events.Block(_rom, _map);
        _talked = block >= 0 ? L2EventBlocks.ReadLists(_rom, block).Where(x => x.Head == L2EventBlocks.TalkList).Select(x => x.Id).ToHashSet() : new();
        var sprites = L2MapInfo.CharacterSprites(_rom, _map);
        var cast = L2Cutscene.CastIds(_rom, _map);
        TalkCombo.Items.Clear();
        foreach (var (_, charRecord) in L2MapInfo.Records(_rom, _map, L2MapInfo.Characters, 8).OrderBy(r => r.Bytes[0]))
        {
            int id = 0x4F + charRecord[0];
            if (cast.Contains(id)) continue;
            string spriteText = sprites.TryGetValue(id, out int spriteId) ? $", sprite {L2Sprites.Name(spriteId)}" : "";
            TalkCombo.Items.Add(new L2Option(id, $"Character {charRecord[0]} at ({charRecord[1]},{charRecord[2]}){spriteText}" + (_talked.Contains(id) ? " (has an event already)" : "")));
        }
        TalkCombo.SelectedIndex = TalkCombo.Items.Cast<L2Option>().ToList().FindIndex(o => !_talked.Contains(o.Value));
        PointCombo.Items.Clear();
        foreach (var point in L2StoryClear.PresetPoints(_rom, _map)) PointCombo.Items.Add(new L2Option(point.Id, $"point {point.Id} ({point.X}, {point.Y})"));
        if (PointCombo.Items.Count > 0) PointCombo.SelectedIndex = 0;
        DrawMap();
        Start_Changed(this, new RoutedEventArgs());
    }

    private void DrawMap()
    {
        try
        {
            var (w, h, pixels) = L2Maps.Render(_rom, _map);
            var bitmap = BitmapSource.Create(w, h, 96, 96, PixelFormats.Bgra32, null, pixels.Select(c => (int)c).ToArray(), w * 4);
            bitmap.Freeze();
            MapImage.Source = bitmap;
            Surface.Width = w; Surface.Height = h;
        }
        catch (Exception ex) { MapHint.Text = "The map can't be drawn: " + ex.Message; }
        DrawMarks();
    }

    private void DrawMarks()
    {
        Marks.Children.Clear();
        if (_map < 0) return;
        void Box(int x1, int y1, int x2, int y2, Brush stroke, Brush fill, string? label = null)
        {
            var rect = new Rectangle { Width = (x2 - x1 + 1) * 16, Height = (y2 - y1 + 1) * 16, Stroke = stroke, StrokeThickness = 2, Fill = fill };
            Canvas.SetLeft(rect, x1 * 16); Canvas.SetTop(rect, y1 * 16);
            Marks.Children.Add(rect);
            if (label == null) return;
            var labelBlock = new TextBlock { Text = label, Foreground = Brushes.White, FontSize = 10, Background = new SolidColorBrush(Color.FromArgb(160, 0, 0, 0)) };
            Canvas.SetLeft(labelBlock, x1 * 16 + 1); Canvas.SetTop(labelBlock, y1 * 16 - 13);
            Marks.Children.Add(labelBlock);
        }
        foreach (var (_, areaRecord) in L2MapInfo.Records(_rom, _map, L2MapInfo.Triggers, 5))
            Box(areaRecord[1], areaRecord[2], areaRecord[3] - 1, areaRecord[4] - 1, Brushes.HotPink, new SolidColorBrush(Color.FromArgb(50, 255, 105, 180)), $"area {areaRecord[0]}");
        foreach (var (_, charRecord) in L2MapInfo.Records(_rom, _map, L2MapInfo.Characters, 8))
            Box(charRecord[1], charRecord[2], charRecord[1], charRecord[2], Brushes.Gold, Brushes.Transparent, $"{charRecord[0]}");
        foreach (var point in L2StoryClear.PresetPoints(_rom, _map))
            Box(point.X, point.Y, point.X, point.Y, Brushes.Cyan, Brushes.Transparent, $"P{point.Id}");
        if (_area is var (x1, y1, x2, y2) && AreaRadio.IsChecked == true)
            Box(x1, y1, x2, y2, Brushes.Gold, new SolidColorBrush(Color.FromArgb(90, 255, 215, 0)));
        if (_spot is var (sx, sy) && NewCharRadio.IsChecked == true)
            Box(sx, sy, sx, sy, Brushes.Gold, new SolidColorBrush(Color.FromArgb(120, 255, 215, 0)), "new");
        if (TalkRadio.IsChecked == true && TalkCombo.SelectedItem is L2Option picked)
            foreach (var (_, charRecord) in L2MapInfo.Records(_rom, _map, L2MapInfo.Characters, 8).Where(r => 0x4F + r.Bytes[0] == picked.Value))
                Box(charRecord[1] - 1, charRecord[2] - 1, charRecord[1] + 1, charRecord[2] + 1, Brushes.Gold, Brushes.Transparent);
        if (LoadRadio.IsChecked == true && PointCombo.SelectedItem is L2Option pickedPoint)
            foreach (var point in L2StoryClear.PresetPoints(_rom, _map).Where(p => p.Id == pickedPoint.Value))
                Box(point.X - 1, point.Y - 1, point.X + 1, point.Y + 1, Brushes.Gold, Brushes.Transparent);
    }

    private void Start_Changed(object sender, RoutedEventArgs e)
    {
        if (!IsInitialized || TalkCombo == null || _map < 0) return;
        LoadPanel.IsEnabled = LoadRadio.IsChecked == true;
        TalkCombo.IsEnabled = TalkRadio.IsChecked == true;
        NewCharPanel.IsEnabled = NewCharRadio.IsChecked == true;
        OnceCheck.Visibility = LoadRadio.IsChecked == true || ArriveRadio.IsChecked == true ? Visibility.Collapsed : Visibility.Visible;
        MapHint.Text = AreaRadio.IsChecked == true ? "Drag a rectangle over the tiles that start the cutscene (the leader steps onto one of them)."
            : NewCharRadio.IsChecked == true ? "Click the tile where the new character stands."
            : "Yellow: the map's characters (start numbers). Pink: the areas that already start events. Cyan: the preset points.";
        DrawMarks();
        Describe();
    }

    private void Choice_Changed(object sender, SelectionChangedEventArgs e) { if (IsInitialized && _map >= 0) { DrawMarks(); Describe(); } }

    private void Sprite_Changed(object sender, SelectionChangedEventArgs e)
    {
        if (!IsInitialized || SpriteImage == null) return;
        SpriteImage.Source = SpriteCombo.SelectedItem is StackPanel { Tag: int s } ? Sprite(s) : null;
    }

    private void Describe()
    {
        if (LoadRadio.IsChecked == true)
            ChoiceText.Text = PointCombo.SelectedItem is L2Option ? "A scene plus a few steps in the map's \"on entering\" event that start it once, while the story flag allows." : "The map has no preset point for the party to stand on: add one on the Maps page.";
        else if (TalkRadio.IsChecked == true)
            ChoiceText.Text = TalkCombo.SelectedItem is L2Option picked
                ? _talked.Contains(picked.Value) ? "That character has an event already: pick it in the list to make it a cutscene, or pick another." : $"Talking to {picked.Label} starts it."
                : "The map has no characters; add a new one.";
        else if (NewCharRadio.IsChecked == true)
            ChoiceText.Text = _spot is var (x, y) ? $"A new character stands on ({x}, {y}); talking to it starts the cutscene." : "Click the tile where the new character stands.";
        else if (AreaRadio.IsChecked == true)
            ChoiceText.Text = _area is var (x1, y1, x2, y2) ? $"Stepping onto ({x1},{y1})-({x2},{y2}) starts it." : "Drag a rectangle on the map.";
        else ChoiceText.Text = "A scene of the map: a \"Go to another map\" step in another cutscene starts it with its number, or a new game (Test in the game).";
    }

    private (int X, int Y) Tile(MouseEventArgs e)
    {
        var mousePos = e.GetPosition(Surface);
        return ((int)Math.Max(0, mousePos.X) / 16, (int)Math.Max(0, mousePos.Y) / 16);
    }

    private void Surface_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (NewCharRadio.IsChecked == true) { _spot = Tile(e); DrawMarks(); Describe(); return; }
        if (AreaRadio.IsChecked != true) return;
        _dragFrom = e.GetPosition(Surface);
        Surface.CaptureMouse();
        var (x, y) = Tile(e);
        _area = (x, y, x, y);
        DrawMarks(); Describe();
    }

    private void Surface_MouseMove(object sender, MouseEventArgs e)
    {
        if (_dragFrom is not { } from || e.LeftButton != MouseButtonState.Pressed) return;
        int fromX = (int)from.X / 16, fromY = (int)from.Y / 16;
        var (x, y) = Tile(e);
        _area = (Math.Min(fromX, x), Math.Min(fromY, y), Math.Max(fromX, x), Math.Max(fromY, y));
        DrawMarks(); Describe();
    }

    private void Surface_MouseLeftButtonUp(object sender, MouseButtonEventArgs e)
    {
        _dragFrom = null;
        Surface.ReleaseMouseCapture();
    }

    private void ZoomIn_Click(object sender, RoutedEventArgs e) { Zoom.ScaleX = Zoom.ScaleY = Math.Min(4, Zoom.ScaleX * 1.5); }
    private void ZoomOut_Click(object sender, RoutedEventArgs e) { Zoom.ScaleX = Zoom.ScaleY = Math.Max(0.25, Zoom.ScaleX / 1.5); }

    private void Ok_Click(object sender, RoutedEventArgs e)
    {
        if (_map < 0) return;
        int flag = -1;
        if (LoadRadio.IsChecked == true && FlagBox.Text.Trim().Length > 0 && !int.TryParse(FlagBox.Text.Trim(), NumberStyles.HexNumber, CultureInfo.InvariantCulture, out flag))
        { ChoiceText.Text = "Write the story flag in hex (00-EF), or leave it empty."; return; }
        // a story flag the game keeps (not F0-FF, those get cleared on every map change), or an extra one if that patch is in
        if (flag >= 0 && (flag is >= 0xF0 and < 0x100 || flag > L2ExtraFlags.Last || flag >= L2ExtraFlags.First && !L2ExtraFlags.IsApplied(_rom)))
        { ChoiceText.Text = "That story flag can't be used: 00-EF" + (L2ExtraFlags.IsApplied(_rom) ? ", or the extra flags 100-17F." : " (F0-FF are cleared on every map change)."); return; }
        int character = -1;
        string madeCharacter = "";
        try
        {
            if (NewCharRadio.IsChecked == true)
            {
                if (_spot is not var (x, y)) { ChoiceText.Text = "Click the tile where the new character stands."; return; }
                if (SpriteCombo.SelectedItem is not StackPanel { Tag: int sprite }) { ChoiceText.Text = "Pick its sprite."; return; }
                character = L2Cutscene.AddCast(_rom, _map, sprite, -1, x, y, hidden: false);
                madeCharacter = $" Character {character - 0x4F} ({L2Sprites.Name(sprite)}) stands on ({x}, {y}).";
            }
            else if (TalkRadio.IsChecked == true)
            {
                if (TalkCombo.SelectedItem is not L2Option picked) { ChoiceText.Text = "Pick a character."; return; }
                if (_talked.Contains(picked.Value)) { ChoiceText.Text = "That character has an event already."; return; }
                character = picked.Value;
            }
            else if (AreaRadio.IsChecked == true && _area == null) { ChoiceText.Text = "Drag a rectangle on the map."; return; }
            else if (LoadRadio.IsChecked == true && PointCombo.SelectedItem is not L2Option) { ChoiceText.Text = "The map has no preset point: add one on the Maps page."; return; }
            var made = L2Cutscene.Create(_rom, _map, new L2Cutscene.NewOptions
            {
                Start = LoadRadio.IsChecked == true ? L2Cutscene.StartKind.MapLoads : AreaRadio.IsChecked == true ? L2Cutscene.StartKind.StepOn
                    : ArriveRadio.IsChecked == true ? L2Cutscene.StartKind.Arrive : L2Cutscene.StartKind.Talk,
                Once = OnceCheck.IsChecked == true, Flag = flag, FlagMustBeOn = FlagOnCheck.IsChecked == true,
                Point = PointCombo.SelectedItem is L2Option pickedPoint ? pickedPoint.Value : -1,
                X1 = _area?.X1 ?? 0, Y1 = _area?.Y1 ?? 0, X2 = _area?.X2 ?? 0, Y2 = _area?.Y2 ?? 0, Character = character,
            });
            _page.CutsceneCreated(_map, made.Head, made.Id, made.What + madeCharacter);
            try { DialogResult = true; } catch (InvalidOperationException) { Close(); }
        }
        catch (Exception ex)
        {
            if (madeCharacter.Length > 0) _page.CurrentMain?.NotifyRomModified($"Lufia II: character {character - 0x4F} on map {_map:X2}");
            ChoiceText.Text = "Not made: " + ex.Message + madeCharacter;
        }
    }

    private void Cancel_Click(object sender, RoutedEventArgs e) => DialogResult = false;
}
