using LufiaForge.Core;
using LufiaForge.Core.Lufia2;
using LufiaForge.Core.Maps;
using System.Windows;
using System.Windows.Controls;

namespace LufiaForge.Modules.Common;

// picks where a new game starts once the story is cleared. Lufia 1 uses an arrival point (StoryClear), Lufia II a
// preset point (L2StoryClear)
public partial class ClearStoryWindow : Window
{
    private sealed record Choice(int Id, string Label);

    private readonly RomBuffer _rom;
    public int StartMap { get; private set; }
    public int StartArrival { get; private set; }

    public ClearStoryWindow(RomBuffer rom)
    {
        InitializeComponent();
        _rom = rom;
        if (rom.IsLufia2)
        {
            StartLabel.Text = "A new game starts here, with Maxim";
            PointLabel.Text = "Preset point";
            var maps = Enumerable.Range(2, L2Maps.Count(rom) - 2)
                .Select(id => new Choice(id, $"{id:X2}  {L2Maps.Name(rom, id)}"))
                .Where(c => L2StoryClear.PresetPoints(rom, c.Id).Count > 0 && L2Events.Block(rom, c.Id) >= 0).ToList();
            MapCombo.ItemsSource = maps;
            MapCombo.SelectedItem = maps.FirstOrDefault(m => m.Id == 0x03) ?? maps.FirstOrDefault();
            return;
        }
        var lufia1Maps = MapCatalog.ScanNamed(rom).Where(m => m.MapId != StoryClear.OpeningMap && m.MapId != LufiaMap.WorldMap).Select(m => new Choice(m.MapId, m.Label)).ToList();
        MapCombo.ItemsSource = lufia1Maps;
        MapCombo.SelectedItem = lufia1Maps.FirstOrDefault(m => m.Id == 0x04) ?? lufia1Maps.FirstOrDefault();
    }

    private void MapCombo_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        var points = new List<Choice>();
        if (MapCombo.SelectedItem is Choice map)
        {
            try
            {
                if (_rom.IsLufia2)
                    foreach (var preset in L2StoryClear.PresetPoints(_rom, map.Id))
                        points.Add(new Choice(preset.Id, $"{preset.Id:X2}  at ({preset.X}, {preset.Y})"));
                else
                    // the warp commands number arrival points from 1
                    foreach (var arrival in LufiaMap.Load(_rom, map.Id).Arrivals)
                        points.Add(new Choice(arrival.Index + 1, $"{arrival.Index + 1}  at ({arrival.X}, {arrival.Y})"));
            }
            catch { }
        }
        ArrivalCombo.ItemsSource = points;
        ArrivalCombo.SelectedIndex = points.Count > 0 ? 0 : -1;
        OkButton.IsEnabled = points.Count > 0;
    }

    private void Ok_Click(object sender, RoutedEventArgs e)
    {
        if (MapCombo.SelectedItem is not Choice map || ArrivalCombo.SelectedItem is not Choice point) return;
        StartMap = map.Id;
        StartArrival = point.Id;
        DialogResult = true;
    }
}
