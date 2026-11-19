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

// pick where an exit leads by clicking an arrival point (or a new spot) on the destination map
public partial class DestinationPickerWindow : Window
{
    private readonly RomBuffer _rom;
    private readonly MapEditorViewModel _vm;
    private readonly MapExit _exit;
    private readonly int _currentMapId;
    private LufiaMap? _shown;

    // the chosen map, as its real id (the current map's own id when the exit leads to the same map)
    public int ResultMap { get; private set; } = -1;
    public int ResultArrival { get; private set; } = -1;
    public (int x, int y)? ResultNewArrival { get; private set; }

    public DestinationPickerWindow(RomBuffer rom, MapEditorViewModel vm, MapExit exit, int currentMapId)
    {
        InitializeComponent();
        _rom = rom; _vm = vm; _exit = exit; _currentMapId = currentMapId;
        HeaderText.Text = $"Exit E{exit.Index} of map {currentMapId:X2}: choose where it leads";
        MapList.ItemsSource = vm.Maps;
        int destMap = exit.DestMap == 0 ? currentMapId : exit.DestMap;
        if (exit.Arrival is >= 0 and < 255) ResultArrival = exit.Arrival;   // before the map is shown, so it's highlighted
        MapList.SelectedItem = vm.Maps.FirstOrDefault(m => m.MapId == destMap) ?? vm.Maps.FirstOrDefault();
        Loaded += (_, _) => { MapList.ScrollIntoView(MapList.SelectedItem); FocusChoice(); };
    }

    private void MapList_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (MapList.SelectedItem is not MapInfo info) return;
        _shown = _vm.MapForPicker(info.MapId);
        if (_shown == null) { ChoiceText.Text = $"Map {info.MapId:X2} can't be shown."; return; }
        var tileset = MapTileset.ForMap(_rom, _shown);
        var px = tileset.RenderMap(_shown);
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
        foreach (var arrival in _shown.Arrivals)
        {
            bool chosen = ResultNewArrival == null && arrival.Index == ResultArrival;
            var square = new Rectangle
            {
                Width = 14, Height = 14, Stroke = chosen ? Brushes.White : Brushes.Cyan, StrokeThickness = chosen ? 3 : 2,
                Fill = new SolidColorBrush(Color.FromArgb(chosen ? (byte)140 : (byte)60, 0, 255, 255)),
            };
            Canvas.SetLeft(square, arrival.X * 16 + 1); Canvas.SetTop(square, arrival.Y * 16 + 1);
            Marks.Children.Add(square);
            var label = new TextBlock { Text = $"A{arrival.Index}", Foreground = Brushes.White, FontSize = 10, FontWeight = FontWeights.Bold,
                                    Background = new SolidColorBrush(Color.FromArgb(160, 0, 0, 0)) };
            Canvas.SetLeft(label, arrival.X * 16 + 16); Canvas.SetTop(label, arrival.Y * 16);
            Marks.Children.Add(label);
        }
        if (ResultNewArrival is { } spot)
        {
            var square = new Rectangle { Width = 16, Height = 16, Stroke = Brushes.HotPink, StrokeThickness = 3,
                                    Fill = new SolidColorBrush(Color.FromArgb(120, 255, 105, 180)) };
            Canvas.SetLeft(square, spot.x * 16); Canvas.SetTop(square, spot.y * 16);
            Marks.Children.Add(square);
        }
    }

    private void Surface_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (_shown == null) return;
        var pos = e.GetPosition(MapImage);
        int x = (int)(pos.X / 16), y = (int)(pos.Y / 16);
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
        if (ResultNewArrival is { } spot)
            ChoiceText.Text = $"Leads to a NEW arrival point at ({spot.x}, {spot.y}) on {map}." +
                              (_shown.MapId != _currentMapId ? $" Map {_shown.MapId:X2} will be saved in the project with the new point." : "");
        else if (ResultArrival >= 0 && ResultArrival < _shown.Arrivals.Count)
        {
            var arrival = _shown.Arrivals[ResultArrival];
            ChoiceText.Text = $"Leads to {map}, arrival point A{arrival.Index} at ({arrival.X}, {arrival.Y}).";
        }
        else ChoiceText.Text = $"Showing {map}. Click an arrival point.";
        OkButton.IsEnabled = ResultNewArrival != null || (ResultArrival >= 0 && ResultArrival < _shown.Arrivals.Count);
    }

    private void FocusChoice()
    {
        if (_shown == null) return;
        (int x, int y)? target = ResultNewArrival ?? (ResultArrival >= 0 && ResultArrival < _shown.Arrivals.Count
            ? (_shown.Arrivals[ResultArrival].X, _shown.Arrivals[ResultArrival].Y) : null);
        if (target is not { } spot) return;
        Scroll.UpdateLayout();
        Scroll.ScrollToHorizontalOffset(Math.Max(0, spot.x * 16 * Zoom.ScaleX - Scroll.ViewportWidth / 2));
        Scroll.ScrollToVerticalOffset(Math.Max(0, spot.y * 16 * Zoom.ScaleY - Scroll.ViewportHeight / 2));
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
