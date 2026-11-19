using LufiaForge.Core;
using LufiaForge.Core.Maps;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Brushes = System.Windows.Media.Brushes;
using MouseEventArgs = System.Windows.Input.MouseEventArgs;
using Color = System.Windows.Media.Color;
using Rectangle = System.Windows.Shapes.Rectangle;

namespace LufiaForge.Modules.Events;

// makes a new (empty) event on a map and hooks it up. it starts when you walk onto a new step-on area (dragged on
// the map), right after another event, or when you talk to a character that has no event yet. can be once only
public partial class NewEventWindow : Window
{
    private readonly RomBuffer _rom;
    private LufiaMap? _map;
    private (int x1, int y1, int x2, int y2)? _area;
    private (int x, int y)? _dragFrom;

    public int ResultMap { get; private set; } = -1;
    public int ResultEvent { get; private set; } = -1;
    // what got made, for the history and the status line
    public string ResultText { get; private set; } = "";

    private sealed record Choice(int Event, string Label) { public override string ToString() => Label; }

    public NewEventWindow(RomBuffer rom, IEnumerable<MapInfo> maps, int mapId, string title, bool cutscene)
    {
        InitializeComponent();
        _rom = rom;
        Title = title;
        HeaderText.Text = title;
        var list = maps.ToList();
        MapList.ItemsSource = list;
        MapList.SelectedItem = list.FirstOrDefault(m => m.MapId == mapId) ?? list.FirstOrDefault();
        Loaded += (_, _) => MapList.ScrollIntoView(MapList.SelectedItem);
        if (!cutscene) TalkRadio.IsChecked = false;
    }

    private void MapList_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (MapList.SelectedItem is not MapInfo info) return;
        _area = null;
        try { _map = LufiaMap.Load(_rom, info.MapId); }
        catch (Exception ex) { _map = null; MapImage.Source = null; ChoiceText.Text = $"Map {info.MapId:X2} can't be shown: {ex.Message}"; Update(); return; }
        var tileset = MapTileset.ForMap(_rom, _map);
        var pixels = tileset.RenderMap(_map);
        int width = _map.Width * 16, height = _map.Height * 16;
        var bitmap = new WriteableBitmap(width, height, 96, 96, PixelFormats.Bgra32, null);
        bitmap.WritePixels(new Int32Rect(0, 0, width, height), pixels, width * 4, 0);
        MapImage.Source = bitmap;
        Marks.Width = width; Marks.Height = height;
        Zoom.ScaleX = Zoom.ScaleY = _map.IsWorld ? 0.5 : 1.0;

        // events this one can follow, characters without an event
        int slots = MapEvents.TableLength(_rom, info.MapId);
        var after = new List<Choice>();
        for (int ev = 0; ev < Math.Max(slots, 256); ev++)
        {
            if (!MapEvents.HasEvent(_rom, info.MapId, ev)) continue;
            string text = "";
            try { text = EventScript.Load(_rom, info.MapId, ev)?.Ops.FirstOrDefault(o => o.IsText && o.Text.Trim().Length > 0)?.Text.Replace("\n", " ") ?? ""; } catch { }
            if (text.Length > 50) text = text[..50] + "…";
            after.Add(new Choice(ev, $"Event {ev}" + (text.Length > 0 ? $": \"{text}\"" : "")));
        }
        AfterCombo.ItemsSource = after;
        AfterCombo.SelectedIndex = after.Count > 0 ? 0 : -1;
        var talk = _map.Npcs.Where(n => !n.IsUnused && !MapEvents.HasEvent(_rom, info.MapId, MapEvents.TalkEvent(n.Index)))
                            .Select(n => new Choice(MapEvents.TalkEvent(n.Index), $"Character {n.Index + 1} (sprite {n.Sprite:X2}) at ({n.X}, {n.Y})")).ToList();
        TalkCombo.ItemsSource = talk;
        TalkCombo.SelectedIndex = talk.Count > 0 ? 0 : -1;
        TalkRadio.IsEnabled = talk.Count > 0;
        TalkRadio.ToolTip = talk.Count > 0 ? null : "Every character on this map already has an event (add a character in the Map Editor).";
        if (TalkRadio.IsChecked == true && talk.Count == 0) AreaRadio.IsChecked = true;
        AfterRadio.IsEnabled = after.Count > 0;
        DrawMarks();
        Update();
    }

    private void DrawMarks()
    {
        Marks.Children.Clear();
        if (_map == null) return;
        var enabledAreas = MapEvents.EnabledAreas(_rom, _map.MapId);
        foreach (var trigger in _map.Triggers.Where(t => !t.IsUnused))
            AddBox(trigger.X1, trigger.Y1, trigger.X2, trigger.Y2, Brushes.HotPink, 40, enabledAreas.Contains(trigger.Index) ? null : new DoubleCollection { 3, 2 }, $"T{trigger.Index}");
        foreach (var npc in _map.Npcs.Where(n => !n.IsUnused))
            AddBox(npc.X, npc.Y, npc.X + 1, npc.Y + 1, Brushes.Gold, 30, null, $"{npc.Index + 1}");
        if (_area is { } area && AreaRadio.IsChecked == true)
            AddBox(area.x1, area.y1, area.x2, area.y2, Brushes.White, 110, null, "new");
    }

    private void AddBox(int x1, int y1, int x2, int y2, SolidColorBrush stroke, byte fill, DoubleCollection? dash, string label)
    {
        var box = new Rectangle
        {
            Width = Math.Max(1, x2 - x1) * 16, Height = Math.Max(1, y2 - y1) * 16, Stroke = stroke, StrokeThickness = 2,
            Fill = new SolidColorBrush(Color.FromArgb(fill, stroke.Color.R, stroke.Color.G, stroke.Color.B)), StrokeDashArray = dash,
        };
        Canvas.SetLeft(box, x1 * 16); Canvas.SetTop(box, y1 * 16);
        Marks.Children.Add(box);
        var tag = new TextBlock { Text = label, Foreground = Brushes.White, FontSize = 9, FontWeight = FontWeights.Bold, Background = new SolidColorBrush(Color.FromArgb(150, 0, 0, 0)) };
        Canvas.SetLeft(tag, x1 * 16 + 1); Canvas.SetTop(tag, y1 * 16);
        Marks.Children.Add(tag);
    }

    private (int x, int y)? Cell(MouseEventArgs e)
    {
        if (_map == null) return null;
        var mouse = e.GetPosition(MapImage);
        int x = (int)(mouse.X / 16), y = (int)(mouse.Y / 16);
        return x < 0 || y < 0 || x >= _map.Width || y >= _map.Height ? null : (x, y);
    }

    private void Surface_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (Cell(e) is not { } cell) return;
        AreaRadio.IsChecked = true;
        _dragFrom = cell;
        _area = (cell.x, cell.y, cell.x + 1, cell.y + 1);
        Surface.CaptureMouse();
        DrawMarks(); Update();
    }

    private void Surface_MouseMove(object sender, MouseEventArgs e)
    {
        if (_dragFrom is not { } from || e.LeftButton != MouseButtonState.Pressed || Cell(e) is not { } cell) return;
        _area = (Math.Min(from.x, cell.x), Math.Min(from.y, cell.y), Math.Max(from.x, cell.x) + 1, Math.Max(from.y, cell.y) + 1);
        DrawMarks(); Update();
    }

    private void Surface_MouseLeftButtonUp(object sender, MouseButtonEventArgs e)
    {
        _dragFrom = null;
        Surface.ReleaseMouseCapture();
    }

    private void Start_Changed(object sender, RoutedEventArgs e) { if (IsLoaded) { DrawMarks(); Update(); } }
    private void Choice_Changed(object sender, SelectionChangedEventArgs e) { if (IsLoaded) Update(); }

    private void Update()
    {
        if (OkButton == null) return;
        bool ok = _map != null;
        if (AreaRadio.IsChecked == true)
        {
            ok &= _area != null;
            ChoiceText.Text = _area is { } area
                ? $"New step-on area from ({area.x1}, {area.y1}), {area.x2 - area.x1} x {area.y2 - area.y1} blocks: walking onto it plays the new event."
                : "Drag a rectangle on the map where walking in should start the event.";
        }
        else if (AfterRadio.IsChecked == true)
        {
            ok &= AfterCombo.SelectedItem is Choice;
            ChoiceText.Text = AfterCombo.SelectedItem is Choice choice ? $"The new event plays right after event {choice.Event} ends." : "Choose the event it follows.";
        }
        else
        {
            ok &= TalkCombo.SelectedItem is Choice;
            ChoiceText.Text = TalkCombo.SelectedItem is Choice choice ? $"Talking to character {choice.Event + 1} plays the new event." : "Choose the character.";
        }
        OkButton.IsEnabled = ok;
    }

    private void ZoomIn_Click(object sender, RoutedEventArgs e) => Zoom.ScaleX = Zoom.ScaleY = Math.Min(4, Zoom.ScaleX * 1.5);
    private void ZoomOut_Click(object sender, RoutedEventArgs e) => Zoom.ScaleX = Zoom.ScaleY = Math.Max(0.125, Zoom.ScaleX / 1.5);
    private void Cancel_Click(object sender, RoutedEventArgs e) { DialogResult = false; Close(); }

    private void Ok_Click(object sender, RoutedEventArgs e)
    {
        if (_map == null) return;
        int mapId = _map.MapId;
        if (_rom.Length < MapWriter.ExpandedSize &&
            MessageBox.Show("New events go in the added space: expand the ROM to 2 MB? (Expanded ROMs work in BizHawk, Snes9x and bsnes.)",
                Title, MessageBoxButton.YesNo, MessageBoxImage.Question) != MessageBoxResult.Yes) return;
        // if it fails partway (the area written but not the event) the ROM goes back, and the map gets read again
        // (its new area was in this window's copy, so clicking OK again added a second one)
        var before = _rom.ToArray();
        try
        {
            int ev;
            string how;
            if (AreaRadio.IsChecked == true && _area is { } area)
            {
                int index = MapEvents.NextAreaIndex(_rom, _map);
                _map.AddTrigger(area.x1, area.y1, area.x2, area.y2, index);
                MapWriter.Save(_rom, _map, allowExpand: true);
                ev = MapEvents.AreaEvent(_rom, mapId, index);
                how = $"walking onto area T{index}";
            }
            else if (AfterRadio.IsChecked == true && AfterCombo.SelectedItem is Choice after)
            {
                ev = MapEvents.FreeChainedNumber(_rom, mapId);
                MapEvents.Create(_rom, mapId, ev);
                MapEvents.ChainAfter(_rom, mapId, after.Event, ev);
                how = $"right after event {after.Event}";
            }
            else if (TalkCombo.SelectedItem is Choice talk)
            {
                ev = talk.Event;   // (character n's: n + 1, MapEvents.TalkEvent)
                MapEvents.Create(_rom, mapId, ev);
                how = $"talking to character {ev}";
            }
            else return;
            if (OnceCheck.IsChecked == true)
            {
                if (StoryFlags.UsedByScripts == null) StoryFlags.BuildIndex(_rom, MapCatalog.Scan(_rom).Select(m => m.MapId));
                int flag = Enumerable.Range(0, 0xF0).FirstOrDefault(f => StoryFlags.IsFree(_rom, f) && StoryFlags.NameOf(f) == null, -1);
                if (flag < 0) MessageBox.Show("No free story flag is left, so the event plays every time.", Title);
                else
                {
                    MapEvents.MakeOnce(_rom, mapId, ev, flag);
                    string label = (MapList.SelectedItem as MapInfo)?.Label ?? $"map {mapId:X2}";
                    StoryFlags.SetName(flag, $"Once: {label}, event {ev}");
                    how += $", once (flag {flag:X2})";
                }
            }
            ResultMap = mapId; ResultEvent = ev;
            ResultText = $"New event {ev} on map {mapId:X2}: {how}";
            DialogResult = true;
            Close();
        }
        catch (Exception ex)
        {
            if (!_rom.SameAs(before)) _rom.ReplaceAll(before);
            try { _map = LufiaMap.Load(_rom, mapId); } catch { }
            MessageBox.Show("The event couldn't be made, so nothing was changed: " + ex.Message, Title, MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }
}
