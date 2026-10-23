using CommunityToolkit.Mvvm.ComponentModel;
using LufiaForge.Core.Lufia2;

namespace LufiaForge.Modules.Lufia2;

/// <summary>One exit: where on this map it is, and the map and place it leads to.</summary>
public partial class L2ExitRow : ObservableObject
{
    public string Label { get; init; } = "";
    public List<L2Option> MapOptions { get; init; } = new();
    [ObservableProperty] private int _map, _x, _y;
    public Action? Changed { get; set; }
    partial void OnMapChanged(int value) => Changed?.Invoke();
    partial void OnXChanged(int value) => Changed?.Invoke();
    partial void OnYChanged(int value) => Changed?.Invoke();
}

/// <summary>Map exits (the map info's exit list): each one's destination map and place can be changed.</summary>
public partial class L2ExitsPage : L2Page
{
    private List<int> _maps = new();
    private List<L2MapInfo.Entry> _exits = new();
    [ObservableProperty] private List<L2ExitRow> _rows = new();

    protected override IEnumerable<string> ReadNames()
    {
        _maps = Enumerable.Range(2, L2Maps.Count - 2).Where(m => L2MapInfo.Read(Rom!, m).Any(e => e.Kind == "Exit")).ToList();
        return _maps.Select(m => $"{m:X2}  {L2Maps.Name(Rom!, m)}  ({L2MapInfo.Read(Rom!, m).Count(e => e.Kind == "Exit")})");
    }

    protected override void Load(int index)
    {
        var maps = Enumerable.Range(0, L2Maps.Count).Select(m => new L2Option(m, $"{m:X2} {L2Maps.Name(Rom!, m)}")).ToList();
        _exits = L2MapInfo.Read(Rom!, _maps[index]).Where(e => e.Kind == "Exit").ToList();
        Rows = _exits.Select(e => new L2ExitRow
        {
            Label = $"Exit {e.Id:X2} at ({e.X1},{e.Y1}){(e.X2 != e.X1 || e.Y2 != e.Y1 ? $"-({e.X2},{e.Y2})" : "")}",
            MapOptions = maps, Map = e.DestMap, X = e.DestX, Y = e.DestY, Changed = Edited,
        }).ToList();
    }

    protected override bool IsField(string p) => false;

    protected override string? Write()
    {
        for (int i = 0; i < _exits.Count && i < Rows.Count; i++)
        {
            var r = Rows[i];
            L2MapInfo.WriteExit(Rom!, _exits[i], Math.Clamp(r.Map, 0, 255), Math.Clamp(r.X, 0, 255), Math.Clamp(r.Y, 0, 255));
        }
        return $"exits of map {_maps[SelectedIndex]:X2}";
    }
}
