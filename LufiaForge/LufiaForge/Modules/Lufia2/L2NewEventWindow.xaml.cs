using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Shapes;
using LufiaForge.Core;
using LufiaForge.Core.Lufia2;
using MouseEventArgs = System.Windows.Input.MouseEventArgs;
using Point = System.Windows.Point;
using Brush = System.Windows.Media.Brush;
using Brushes = System.Windows.Media.Brushes;
using Color = System.Windows.Media.Color;
using Rectangle = System.Windows.Shapes.Rectangle;

namespace LufiaForge.Modules.Lufia2;

/// <summary>
/// Makes a new Lufia II event on the picked map: talking to one of its characters (or to a new one, added to the map
/// where the user clicks), stepping onto an area dragged on the map, or more steps at the end of another event. The
/// event starts with one line of text and, when it plays once, a free story flag (checked first, then turned on).
/// </summary>
public partial class L2NewEventWindow : Window
{
    private readonly L2EventsPage _page;
    private readonly RomBuffer _rom;
    private readonly int _map;
    private Point? _dragFrom;
    private (int X1, int Y1, int X2, int Y2)? _area;
    private (int X, int Y)? _spot;
    private readonly int _flag;
    private readonly HashSet<int> _talked;

    public L2NewEventWindow(L2EventsPage page)
    {
        InitializeComponent();
        _page = page;
        _rom = page.CurrentRom!;
        _map = page.CurrentMap;
        HeaderText.Text = $"New event on {page.CurrentMapName} (map {_map:X2})";
        _flag = page.FreeFlag();
        OnceCheck.Content = _flag >= 0 ? $"Plays only once (story flag {_flag:X2} is free: it's checked first, then turned on)" : "Plays only once (no story flag is free)";
        OnceCheck.IsEnabled = _flag >= 0;
        if (_flag < 0) OnceCheck.IsChecked = false;

        int block = L2Events.Block(_rom, _map);
        _talked = block >= 0 ? L2EventBlocks.ReadLists(_rom, block).Where(e => e.Head == L2EventBlocks.TalkList).Select(e => e.Id).ToHashSet() : new();
        foreach (var (id, c) in page.Characters.Where(c => c.Key >= 0x50).OrderBy(c => c.Key))
            TalkCombo.Items.Add(new L2Option(id, L2EventCommands.Cap(c.Name) + (_talked.Contains(id) ? " (has an event already)" : "")));
        TalkCombo.SelectedIndex = TalkCombo.Items.Cast<L2Option>().ToList().FindIndex(o => !_talked.Contains(o.Value));
        foreach (var e in page.Events.Where(e => e.Entry != null)) AfterCombo.Items.Add(e);
        if (AfterCombo.Items.Count > 0) AfterCombo.SelectedIndex = 0;
        foreach (var (v, n) in L2EventCommands.MoveModes.Where(m => m.Item1 is 0x05 or 0x06 or 0x07 or 0x08 or 0x00))
            ModeCombo.Items.Add(new L2Option(v, v == 0 ? "Wanders around (2 tiles each way)" : n));
        ModeCombo.SelectedIndex = 1;
        if (block < 0) { TalkRadio.IsEnabled = NewCharRadio.IsEnabled = AreaRadio.IsEnabled = AfterRadio.IsEnabled = false; ChoiceText.Text = "This map has no event block."; }
        DrawMap();
        Sprite_Changed(this, null!);
        Start_Changed(this, new RoutedEventArgs());
    }

    private void DrawMap()
    {
        try
        {
            var (w, h, px) = L2Maps.Render(_rom, _map);
            var bmp = BitmapSource.Create(w, h, 96, 96, PixelFormats.Bgra32, null, px.Select(c => (int)c).ToArray(), w * 4);
            bmp.Freeze();
            MapImage.Source = bmp;
            Surface.Width = w; Surface.Height = h;
        }
        catch (Exception ex) { MapHint.Text = "The map can't be drawn: " + ex.Message; }
        DrawMarks();
    }

    private void DrawMarks()
    {
        Marks.Children.Clear();
        void Box(int x1, int y1, int x2, int y2, Brush stroke, Brush fill, string? label = null)
        {
            var r = new Rectangle { Width = (x2 - x1 + 1) * 16, Height = (y2 - y1 + 1) * 16, Stroke = stroke, StrokeThickness = 2, Fill = fill };
            Canvas.SetLeft(r, x1 * 16); Canvas.SetTop(r, y1 * 16);
            Marks.Children.Add(r);
            if (label == null) return;
            var t = new TextBlock { Text = label, Foreground = Brushes.White, FontSize = 10, Background = new SolidColorBrush(Color.FromArgb(160, 0, 0, 0)) };
            Canvas.SetLeft(t, x1 * 16 + 1); Canvas.SetTop(t, y1 * 16 - 13);
            Marks.Children.Add(t);
        }
        foreach (var (_, b) in L2MapInfo.Records(_rom, _map, L2MapInfo.Triggers, 5))
            Box(b[1], b[2], b[3] - 1, b[4] - 1, Brushes.HotPink, new SolidColorBrush(Color.FromArgb(50, 255, 105, 180)), $"area {b[0]}");
        foreach (var (_, b) in L2MapInfo.Records(_rom, _map, L2MapInfo.Characters, 8))
            Box(b[1], b[2], b[1], b[2], Brushes.Gold, Brushes.Transparent, $"{b[0]}");
        if (_area is var (x1, y1, x2, y2) && AreaRadio.IsChecked == true)
            Box(x1, y1, x2, y2, Brushes.Gold, new SolidColorBrush(Color.FromArgb(90, 255, 215, 0)));
        if (_spot is var (sx, sy) && NewCharRadio.IsChecked == true)
            Box(sx, sy, sx, sy, Brushes.Gold, new SolidColorBrush(Color.FromArgb(120, 255, 215, 0)), "new");
        if (TalkRadio.IsChecked == true && TalkCombo.SelectedItem is L2Option o)
            foreach (var (_, b) in L2MapInfo.Records(_rom, _map, L2MapInfo.Characters, 8).Where(r => 0x4F + r.Bytes[0] == o.Value))
                Box(b[1] - 1, b[2] - 1, b[1] + 1, b[2] + 1, Brushes.Gold, Brushes.Transparent);
    }

    private void Start_Changed(object sender, RoutedEventArgs e)
    {
        if (!IsInitialized || TalkCombo == null) return;
        TalkCombo.IsEnabled = TalkRadio.IsChecked == true;
        NewCharPanel.IsEnabled = NewCharRadio.IsChecked == true;
        AfterCombo.IsEnabled = AfterRadio.IsChecked == true;
        SpeakerText.Text = TalkRadio.IsChecked == true || NewCharRadio.IsChecked == true ? "Said by the character talked to."
            : AreaRadio.IsChecked == true ? "Said by Maxim." : "Said by Maxim, after the other event's steps.";
        OnceCheck.Visibility = AfterRadio.IsChecked == true ? Visibility.Collapsed : Visibility.Visible;
        MapHint.Text = AreaRadio.IsChecked == true ? "Drag a rectangle over the tiles that start the event (the leader steps onto one of them)."
            : NewCharRadio.IsChecked == true ? "Click the tile where the new character stands." : "Yellow: the map's characters (start numbers). Pink: the areas that already start events.";
        DrawMarks();
        Describe();
    }

    private void Choice_Changed(object sender, SelectionChangedEventArgs e) { if (IsInitialized) { DrawMarks(); Describe(); } }

    private void Sprite_Changed(object sender, TextChangedEventArgs e)
    {
        if (!IsInitialized || SpriteImage == null) return;
        SpriteImage.Source = int.TryParse(SpriteBox.Text.Trim(), NumberStyles.HexNumber, CultureInfo.InvariantCulture, out int s) && s < L2Graphics.MapSpriteCount
            ? Picture(L2Graphics.MapSprite(_rom, s)) : null;
    }

    private static BitmapSource? Picture((int Width, int Height, uint[] Pixels)? pic)
    {
        if (pic is not var (w, h, px)) return null;
        var bmp = BitmapSource.Create(w, h, 96, 96, PixelFormats.Bgra32, null, px.Select(c => (int)c).ToArray(), w * 4);
        bmp.Freeze();
        return bmp;
    }

    private void Describe()
    {
        if (TalkRadio.IsChecked == true)
            ChoiceText.Text = TalkCombo.SelectedItem is L2Option o
                ? _talked.Contains(o.Value) ? "That character has an event already: pick it in the list to change it, or pick another." : $"Talking to {_page.Who(o.Value)} starts it."
                : "The map has no characters; add a new one.";
        else if (NewCharRadio.IsChecked == true)
            ChoiceText.Text = _spot is var (x, y) ? $"A new character stands on ({x}, {y}); talking to it starts the event." : "Click the tile where the new character stands.";
        else if (AreaRadio.IsChecked == true)
            ChoiceText.Text = _area is var (x1, y1, x2, y2) ? $"Stepping onto ({x1},{y1})-({x2},{y2}) starts it, each time unless it plays once." : "Drag a rectangle on the map.";
        else ChoiceText.Text = AfterCombo.SelectedItem is L2EventItem ev ? $"\"{ev.Title}\" goes on with the new steps where it ended." : "";
    }

    private (int X, int Y) Tile(MouseEventArgs e)
    {
        var p = e.GetPosition(Surface);
        return ((int)Math.Max(0, p.X) / 16, (int)Math.Max(0, p.Y) / 16);
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
        int fx = (int)from.X / 16, fy = (int)from.Y / 16;
        var (x, y) = Tile(e);
        _area = (Math.Min(fx, x), Math.Min(fy, y), Math.Max(fx, x), Math.Max(fy, y));
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
        string text = TextBox.Text.Replace("\r", "");
        if (L2EventText.Check(text, _rom) is { } problem) { ChoiceText.Text = problem; return; }
        int flag = OnceCheck.IsChecked == true && AfterRadio.IsChecked != true ? _flag : -1;
        int block = L2Events.Block(_rom, _map);
        try
        {
            if (TalkRadio.IsChecked == true || NewCharRadio.IsChecked == true)
            {
                int actor;
                string what;
                if (NewCharRadio.IsChecked == true)
                {
                    if (_spot is not var (x, y)) { ChoiceText.Text = "Click the tile where the new character stands."; return; }
                    if (!int.TryParse(SpriteBox.Text.Trim(), NumberStyles.HexNumber, CultureInfo.InvariantCulture, out int sprite) || sprite >= L2Graphics.MapSpriteCount)
                    { ChoiceText.Text = $"The sprite is a hex number below {L2Graphics.MapSpriteCount:X2}."; return; }
                    actor = L2EventBlocks.AddCharacter(_rom, _map, x, y, sprite, ((L2Option)ModeCombo.SelectedItem).Value);
                    what = $"new character {actor - 0x4F} on {_page.CurrentMapName} at ({x}, {y}) with a talk event";
                }
                else
                {
                    if (TalkCombo.SelectedItem is not L2Option o) { ChoiceText.Text = "Pick a character."; return; }
                    if (_talked.Contains(o.Value)) { ChoiceText.Text = "That character has an event already."; return; }
                    actor = o.Value;
                    what = $"new event on {_page.CurrentMapName}: talking to {_page.Who(actor)}";
                }
                var script = L2EventBlocks.NewScript(block, new byte[] { 0x08 }, text, flag);
                L2EventBlocks.Add(_rom, _map, L2EventBlocks.TalkList, actor, script);
                _page.EventCreated(new L2EventBlocks.Entry(L2EventBlocks.TalkList, actor, 0), what + (flag >= 0 ? $" (plays once: story flag {flag:X2})" : ""));
            }
            else if (AreaRadio.IsChecked == true)
            {
                if (_area is not var (x1, y1, x2, y2)) { ChoiceText.Text = "Drag a rectangle on the map."; return; }
                var script = L2EventBlocks.NewScript(block, new byte[] { 0x61 }, text, flag);
                int id = L2EventBlocks.AddTrigger(_rom, _map, x1, y1, x2, y2, script);
                _page.EventCreated(new L2EventBlocks.Entry(L2EventBlocks.TriggerList, id, 0),
                    $"new event on {_page.CurrentMapName}: stepping onto ({x1},{y1})-({x2},{y2})" + (flag >= 0 ? $" (plays once: story flag {flag:X2})" : ""));
            }
            else
            {
                if (AfterCombo.SelectedItem is not L2EventItem ev) { ChoiceText.Text = "Pick the event to go on from."; return; }
                var steps = L2EventBlocks.NewScript(block, new byte[] { 0x61 }, text, -1).Steps;
                _page.ContinueEvent(ev, steps);
            }
            try { DialogResult = true; } catch (InvalidOperationException) { Close(); }
        }
        catch (Exception ex) { ChoiceText.Text = "Not made: " + ex.Message; }
    }

    private void Cancel_Click(object sender, RoutedEventArgs e) => DialogResult = false;
}
