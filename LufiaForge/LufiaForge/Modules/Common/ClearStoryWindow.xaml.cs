using LufiaForge.Core;
using LufiaForge.Core.Lufia2;
using LufiaForge.Core.Maps;
using System.Windows;
using System.Windows.Controls;

namespace LufiaForge.Modules.Common;

/// <summary>Picks where a new game starts after the story is cleared (<see cref="StoryClear"/> for Lufia 1, an arrival
/// point; <see cref="L2StoryClear"/> for Lufia II, a preset point).</summary>
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
            var maps = Enumerable.Range(2, L2Maps.Count - 2)
                .Select(id => new Choice(id, $"{id:X2}  {L2Maps.Name(rom, id)}"))
                .Where(c => L2StoryClear.PresetPoints(rom, c.Id).Count > 0 && L2Events.Block(rom, c.Id) >= 0).ToList();
            MapCombo.ItemsSource = maps;
            MapCombo.SelectedItem = maps.FirstOrDefault(m => m.Id == 0x03) ?? maps.FirstOrDefault();
            return;
        }
        var l1 = MapCatalog.ScanNamed(rom).Where(m => m.MapId != StoryClear.OpeningMap && m.MapId != LufiaMap.WorldMap).Select(m => new Choice(m.MapId, m.Label)).ToList();
        MapCombo.ItemsSource = l1;
        MapCombo.SelectedItem = l1.FirstOrDefault(m => m.Id == 0x04) ?? l1.FirstOrDefault();
    }

    private void MapCombo_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        var list = new List<Choice>();
        if (MapCombo.SelectedItem is Choice map)
        {
            try
            {
                if (_rom.IsLufia2)
                    foreach (var p in L2StoryClear.PresetPoints(_rom, map.Id))
                        list.Add(new Choice(p.Id, $"{p.Id:X2}  at ({p.X}, {p.Y})"));
                else
                    // arrival points are numbered from 1 in the warp commands
                    foreach (var a in LufiaMap.Load(_rom, map.Id).Arrivals)
                        list.Add(new Choice(a.Index + 1, $"{a.Index + 1}  at ({a.X}, {a.Y})"));
            }
            catch { }
        }
        ArrivalCombo.ItemsSource = list;
        ArrivalCombo.SelectedIndex = list.Count > 0 ? 0 : -1;
        OkButton.IsEnabled = list.Count > 0;
    }

    private void Ok_Click(object sender, RoutedEventArgs e)
    {
        if (MapCombo.SelectedItem is not Choice m || ArrivalCombo.SelectedItem is not Choice a) return;
        StartMap = m.Id;
        StartArrival = a.Id;
        DialogResult = true;
    }
}
