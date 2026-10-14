using LufiaForge.Core;
using LufiaForge.Core.Maps;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using MouseEventArgs = System.Windows.Input.MouseEventArgs;

namespace LufiaForge.Modules.MapEditor;

/// <summary>Copy a piece of another map with the same tileset (drag a rectangle) to use as a stamp brush.</summary>
public partial class PiecesWindow : Window
{
    private readonly RomBuffer _rom;
    private LufiaMap? _shown;
    private (int x, int y)? _from;
    private (int x1, int y1, int x2, int y2)? _rect;

    public int ResultW { get; private set; }
    public int ResultH { get; private set; }
    public ushort[] ResultBlocks { get; private set; } = Array.Empty<ushort>();
    public string ResultSource { get; private set; } = "";

    public PiecesWindow(RomBuffer rom, IEnumerable<MapInfo> maps, int tileset, int currentMap)
    {
        InitializeComponent();
        _rom = rom;
        var same = maps.Where(m => m.MapId != LufiaMap.WorldMap && m.Tileset == tileset).ToList();
        HeaderText.Text = $"Pieces from other maps (tileset {tileset}: {same.Count} maps)";
        MapList.ItemsSource = same;
        MapList.SelectedItem = same.FirstOrDefault(m => m.MapId != currentMap) ?? same.FirstOrDefault();
    }

    private void MapList_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (MapList.SelectedItem is not MapInfo info) return;
        _rect = null; Pick.Visibility = Visibility.Collapsed; OkButton.IsEnabled = false;
        try { _shown = LufiaMap.Load(_rom, info.MapId); }
        catch (Exception ex) { _shown = null; MapImage.Source = null; ChoiceText.Text = $"Map {info.MapId:X2} can't be shown: {ex.Message}"; return; }
        var px = MapTileset.ForMap(_rom, _shown).RenderMap(_shown);
        int w = _shown.Width * 16, h = _shown.Height * 16;
        var bmp = new WriteableBitmap(w, h, 96, 96, PixelFormats.Bgra32, null);
        bmp.WritePixels(new Int32Rect(0, 0, w, h), px, w * 4, 0);
        MapImage.Source = bmp;
        ChoiceText.Text = "Drag a rectangle over the piece you want.";
    }

    private (int x, int y)? Cell(MouseEventArgs e)
    {
        if (_shown == null) return null;
        var p = e.GetPosition(MapImage);
        int x = (int)(p.X / 16), y = (int)(p.Y / 16);
        return x < 0 || y < 0 || x >= _shown.Width || y >= _shown.Height ? null : (x, y);
    }

    private void Surface_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (Cell(e) is not { } c) return;
        _from = c;
        Surface.CaptureMouse();
        SetRect(c, c);
    }

    private void Surface_MouseMove(object sender, MouseEventArgs e)
    {
        if (Cell(e) is not { } c) return;
        HoverText.Text = $"Block ({c.x}, {c.y})";
        if (_from is { } f && e.LeftButton == MouseButtonState.Pressed) SetRect(f, c);
    }

    private void Surface_MouseLeftButtonUp(object sender, MouseButtonEventArgs e) { _from = null; Surface.ReleaseMouseCapture(); }

    private void SetRect((int x, int y) a, (int x, int y) b)
    {
        int x1 = Math.Min(a.x, b.x), y1 = Math.Min(a.y, b.y), x2 = Math.Max(a.x, b.x), y2 = Math.Max(a.y, b.y);
        _rect = (x1, y1, x2, y2);
        Canvas.SetLeft(Pick, x1 * 16); Canvas.SetTop(Pick, y1 * 16);
        Pick.Width = (x2 - x1 + 1) * 16; Pick.Height = (y2 - y1 + 1) * 16;
        Pick.Visibility = Visibility.Visible;
        ChoiceText.Text = $"{x2 - x1 + 1} x {y2 - y1 + 1} blocks from ({x1}, {y1}). Use as brush, then click on your map to stamp it.";
        OkButton.IsEnabled = true;
    }

    private void ZoomIn_Click(object sender, RoutedEventArgs e) => Zoom.ScaleX = Zoom.ScaleY = Math.Min(4, Zoom.ScaleX * 1.5);
    private void ZoomOut_Click(object sender, RoutedEventArgs e) => Zoom.ScaleX = Zoom.ScaleY = Math.Max(0.25, Zoom.ScaleX / 1.5);
    private void Cancel_Click(object sender, RoutedEventArgs e) { DialogResult = false; Close(); }

    private void Ok_Click(object sender, RoutedEventArgs e)
    {
        if (_shown == null || _rect is not { } r) return;
        int w = r.x2 - r.x1 + 1, h = r.y2 - r.y1 + 1;
        var t = new ushort[w * h];
        for (int y = 0; y < h; y++)
            for (int x = 0; x < w; x++)
                t[y * w + x] = (ushort)(_shown.GetTile(r.x1 + x, r.y1 + y) & 0x3FF);
        ResultW = w; ResultH = h; ResultBlocks = t;
        ResultSource = (MapList.SelectedItem as MapInfo)?.Label ?? $"map {_shown.MapId:X2}";
        DialogResult = true;
        Close();
    }
}
