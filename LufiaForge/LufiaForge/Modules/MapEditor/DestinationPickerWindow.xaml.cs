using LufiaForge.Core;
using LufiaForge.Core.Maps;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Shapes;
using Brushes = System.Windows.Media.Brushes;
using Rectangle = System.Windows.Shapes.Rectangle;
using Color = System.Windows.Media.Color;

namespace LufiaForge.Modules.MapEditor;

/// <summary>Choose an exit's destination by clicking an arrival point (or a new spot) on the destination map.</summary>
public partial class DestinationPickerWindow : Window
{
    private readonly RomBuffer _rom;
    private readonly MapEditorViewModel _vm;
    private readonly MapExit _exit;
    private readonly int _currentMapId;
    private LufiaMap? _shown;

    /// <summary>Chosen map (real id; the current map's own id when it leads to the same map).</summary>
    public int ResultMap { get; private set; } = -1;
    public int ResultArrival { get; private set; } = -1;
    public (int x, int y)? ResultNewArrival { get; private set; }

    public DestinationPickerWindow(RomBuffer rom, MapEditorViewModel vm, MapExit exit, int currentMapId)
    {
        InitializeComponent();
        _rom = rom; _vm = vm; _exit = exit; _currentMapId = currentMapId;
        HeaderText.Text = $"Exit E{exit.Index} of map {currentMapId:X2}: choose where it leads";
        MapList.ItemsSource = vm.Maps;
        int dest = exit.DestMap == 0 ? currentMapId : exit.DestMap;
        if (exit.Arrival is >= 0 and < 255) ResultArrival = exit.Arrival;   // before the map is shown, so it's highlighted
        MapList.SelectedItem = vm.Maps.FirstOrDefault(m => m.MapId == dest) ?? vm.Maps.FirstOrDefault();
        Loaded += (_, _) => { MapList.ScrollIntoView(MapList.SelectedItem); FocusChoice(); };
    }

    private void MapList_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (MapList.SelectedItem is not MapInfo info) return;
        _shown = _vm.MapForPicker(info.MapId);
        if (_shown == null) { ChoiceText.Text = $"Map {info.MapId:X2} can't be shown."; return; }
        var ts = MapTileset.ForMap(_rom, _shown);
        var px = ts.RenderMap(_shown);
        int w = _shown.Width * 16, h = _shown.Height * 16;
        var bmp = new WriteableBitmap(w, h, 96, 96, PixelFormats.Bgra32, null);
        bmp.WritePixels(new Int32Rect(0, 0, w, h), px, w * 4, 0);
        MapImage.Source = bmp;
        Marks.Width = w; Marks.Height = h;
        Zoom.ScaleX = Zoom.ScaleY = _shown.IsWorld ? 0.5 : 1.0;
        ResultMap = info.MapId;
        if (e.RemovedItems.Count > 0) { ResultArrival = -1; ResultNewArrival = null; }
        DrawMarks();
        UpdateChoice();
    }

    private void DrawMarks()
    {
        Marks.Children.Clear();
        if (_shown == null) return;
        foreach (var a in _shown.Arrivals)
        {
            bool chosen = ResultNewArrival == null && a.Index == ResultArrival;
            var r = new Rectangle
            {
                Width = 14, Height = 14, Stroke = chosen ? Brushes.White : Brushes.Cyan, StrokeThickness = chosen ? 3 : 2,
                Fill = new SolidColorBrush(Color.FromArgb(chosen ? (byte)140 : (byte)60, 0, 255, 255)),
            };
            Canvas.SetLeft(r, a.X * 16 + 1); Canvas.SetTop(r, a.Y * 16 + 1);
            Marks.Children.Add(r);
            var t = new TextBlock { Text = $"A{a.Index}", Foreground = Brushes.White, FontSize = 10, FontWeight = FontWeights.Bold,
                                    Background = new SolidColorBrush(Color.FromArgb(160, 0, 0, 0)) };
            Canvas.SetLeft(t, a.X * 16 + 16); Canvas.SetTop(t, a.Y * 16);
            Marks.Children.Add(t);
        }
        if (ResultNewArrival is { } p)
        {
            var r = new Rectangle { Width = 16, Height = 16, Stroke = Brushes.HotPink, StrokeThickness = 3,
                                    Fill = new SolidColorBrush(Color.FromArgb(120, 255, 105, 180)) };
            Canvas.SetLeft(r, p.x * 16); Canvas.SetTop(r, p.y * 16);
            Marks.Children.Add(r);
        }
    }

    private void Surface_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (_shown == null) return;
        var pt = e.GetPosition(MapImage);
        int x = (int)(pt.X / 16), y = (int)(pt.Y / 16);
        if (x < 0 || y < 0 || x >= _shown.Width || y >= _shown.Height) return;
        if (NewArrivalCheck.IsChecked == true)
        {
            ResultNewArrival = (x, y);
        }
        else
        {
            // nearest arrival point within a couple of blocks
            var best = _shown.Arrivals.OrderBy(a => Math.Abs(a.X - x) + Math.Abs(a.Y - y)).FirstOrDefault();
            if (best == null || Math.Abs(best.X - x) + Math.Abs(best.Y - y) > 2) { ChoiceText.Text = "Click on an arrival point (cyan square), or tick \"Place a new arrival point\"."; return; }
            ResultArrival = best.Index;
            ResultNewArrival = null;
        }
        DrawMarks();
        UpdateChoice();
    }

    private void UpdateChoice()
    {
        if (_shown == null) return;
        string map = _shown.IsWorld ? "the world map" : $"map {_shown.MapId:X2}";
        if (ResultNewArrival is { } p)
            ChoiceText.Text = $"Leads to a NEW arrival point at ({p.x}, {p.y}) on {map}." +
                              (_shown.MapId != _currentMapId ? $" Map {_shown.MapId:X2} will be written to the ROM with the new point." : "");
        else if (ResultArrival >= 0 && ResultArrival < _shown.Arrivals.Count)
        {
            var a = _shown.Arrivals[ResultArrival];
            ChoiceText.Text = $"Leads to {map}, arrival point A{a.Index} at ({a.X}, {a.Y}).";
        }
        else ChoiceText.Text = $"Showing {map}. Click an arrival point.";
        OkButton.IsEnabled = ResultNewArrival != null || (ResultArrival >= 0 && ResultArrival < _shown.Arrivals.Count);
    }

    private void FocusChoice()
    {
        if (_shown == null) return;
        (int x, int y)? at = ResultNewArrival ?? (ResultArrival >= 0 && ResultArrival < _shown.Arrivals.Count
            ? (_shown.Arrivals[ResultArrival].X, _shown.Arrivals[ResultArrival].Y) : null);
        if (at is not { } p) return;
        Scroll.UpdateLayout();
        Scroll.ScrollToHorizontalOffset(Math.Max(0, p.x * 16 * Zoom.ScaleX - Scroll.ViewportWidth / 2));
        Scroll.ScrollToVerticalOffset(Math.Max(0, p.y * 16 * Zoom.ScaleY - Scroll.ViewportHeight / 2));
    }

    private void NewArrival_Changed(object sender, RoutedEventArgs e)
    {
        if (NewArrivalCheck.IsChecked != true) ResultNewArrival = null;
        DrawMarks();
        UpdateChoice();
    }

    private void ZoomIn_Click(object sender, RoutedEventArgs e) => Zoom.ScaleX = Zoom.ScaleY = Math.Min(4, Zoom.ScaleX * 1.5);
    private void ZoomOut_Click(object sender, RoutedEventArgs e) => Zoom.ScaleX = Zoom.ScaleY = Math.Max(0.125, Zoom.ScaleX / 1.5);

    private void Ok_Click(object sender, RoutedEventArgs e) { DialogResult = true; Close(); }
    private void Cancel_Click(object sender, RoutedEventArgs e) { DialogResult = false; Close(); }
}
