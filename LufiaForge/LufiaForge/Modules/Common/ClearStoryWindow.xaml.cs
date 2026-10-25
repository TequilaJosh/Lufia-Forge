using LufiaForge.Core;
using LufiaForge.Core.Maps;
using System.Windows;
using System.Windows.Controls;

namespace LufiaForge.Modules.Common;

/// <summary>Picks where a new game starts after the story is cleared (<see cref="StoryClear"/>).</summary>
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
        var maps = MapCatalog.ScanNamed(rom).Where(m => m.MapId != StoryClear.OpeningMap && m.MapId != LufiaMap.WorldMap).ToList();
        MapCombo.ItemsSource = maps;
        MapCombo.SelectedItem = maps.FirstOrDefault(m => m.MapId == 0x04) ?? maps.FirstOrDefault();
    }

    private void MapCombo_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        var list = new List<Choice>();
        if (MapCombo.SelectedItem is MapInfo info)
        {
            try
            {
                // arrival points are numbered from 1 in the warp commands
                foreach (var a in LufiaMap.Load(_rom, info.MapId).Arrivals)
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
        if (MapCombo.SelectedItem is not MapInfo m || ArrivalCombo.SelectedItem is not Choice a) return;
        StartMap = m.MapId;
        StartArrival = a.Id;
        DialogResult = true;
    }
}
