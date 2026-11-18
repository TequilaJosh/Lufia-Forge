using CommunityToolkit.Mvvm.ComponentModel;
using LufiaForge.Core.Lufia2;

namespace LufiaForge.Modules.Lufia2;

// one exit: where it is on this map, and the map and place it leads to
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

// map exits (the map info's exit list), you can change where each one leads (map and place)
public partial class L2ExitsPage : L2Page
{
    private List<int> _maps = new();
    private List<L2MapInfo.Entry> _exits = new();
    [ObservableProperty] private List<L2ExitRow> _rows = new();

    protected override IEnumerable<string> ReadNames()
    {
        _maps = Enumerable.Range(2, L2Maps.Count(Rom!) - 2).Where(m => L2MapInfo.Read(Rom!, m).Any(e => e.Kind == "Exit")).ToList();
        return _maps.Select(m => $"{m:X2}  {L2Maps.Name(Rom!, m)}  ({L2MapInfo.Read(Rom!, m).Count(e => e.Kind == "Exit")})");
    }

    protected override void Load(int index)
    {
        var mapOptions = Enumerable.Range(0, L2Maps.Count(Rom!)).Select(m => new L2Option(m, $"{m:X2} {L2Maps.Name(Rom!, m)}")).ToList();
        _exits = L2MapInfo.Read(Rom!, _maps[index]).Where(e => e.Kind == "Exit").ToList();
        Rows = _exits.Select(e => new L2ExitRow
        {
            Label = $"Exit {e.Id:X2} at ({e.X1},{e.Y1}){(e.X2 != e.X1 || e.Y2 != e.Y1 ? $"-({e.X2},{e.Y2})" : "")}",
            MapOptions = mapOptions, Map = e.DestMap, X = e.DestX, Y = e.DestY, Changed = Edited,
        }).ToList();
    }

    protected override bool IsField(string property) => false;

    // only writes the rows changed here, each one to its exit as the ROM has it now (the Maps tab edits the same
    // list and may have moved it), so an exit changed over there keeps its change
    protected override string? Write()
    {
        int map = _maps[SelectedIndex];
        var exitsNow = L2MapInfo.Read(Rom!, map).Where(e => e.Kind == "Exit").ToList();
        bool any = false;
        for (int i = 0; i < _exits.Count && i < Rows.Count; i++)
        {
            var row = Rows[i];
            var (destMap, x, y) = (Math.Clamp(row.Map, 0, 255), Math.Clamp(row.X, 0, 255), Math.Clamp(row.Y, 0, 255));
            var was = _exits[i];
            if (destMap == was.DestMap && x == was.DestX && y == was.DestY) continue;
            var exit = exitsNow.FirstOrDefault(e => e.Id == was.Id);
            if (exit == null) continue;   // taken out on the Maps tab in the meantime
            L2MapInfo.WriteExit(Rom!, exit, destMap, x, y);
            _exits[i] = exit with { DestMap = destMap, DestX = x, DestY = y };
            any = true;
        }
        return any ? $"exits of map {map:X2}" : null;
    }
}
