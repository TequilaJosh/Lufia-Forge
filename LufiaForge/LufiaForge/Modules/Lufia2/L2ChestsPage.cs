using CommunityToolkit.Mvvm.ComponentModel;
using LufiaForge.Core.Lufia2;

namespace LufiaForge.Modules.Lufia2;

// one treasure on the Treasure page: an item, or gold with its amount
public partial class L2ChestRow : ObservableObject
{
    public const int GoldChoice = -2;
    public int Spot { get; init; }
    public string Label { get; init; } = "";
    public List<L2Option> Options { get; init; } = new();
    // the item, or GoldChoice
    [ObservableProperty] private int _value;
    [ObservableProperty] private int _goldAmount = 100;
    public bool IsGold => Value == GoldChoice;
    public Action? Changed { get; set; }
    // the treasure the way the editors keep it (gold = L2Gold.EditorBase + amount)
    public int EditorItem => IsGold ? L2Gold.Item(GoldAmount) : Value;
    partial void OnValueChanged(int value) { OnPropertyChanged(nameof(IsGold)); Changed?.Invoke(); }
    partial void OnGoldAmountChanged(int value) { if (IsGold) Changed?.Invoke(); }
}

// treasure chests: the maps that have some, and for the picked map each chest's place and item (or gold).
// it shares the ROM with the Maps tab. the Maps tab's edits get written before this page reads a map (BeforeRead),
// and a change here is written by spot over whatever the ROM holds at that point, so neither page writes over the
// other's changes
public partial class L2ChestsPage : L2Page
{
    private List<int> _maps = new();
    private int _map = -1;
    // what each shown treasure held when we read it (spot -> editor item)
    private Dictionary<int, int> _shown = new();
    [ObservableProperty] private List<L2ChestRow> _rows = new();
    [ObservableProperty] private string _info = "";
    [ObservableProperty] private string _goldNote = "";

    // called before the page reads the ROM (the Maps tab writes whatever it hasn't written yet)
    public Func<string?>? BeforeRead { get; set; }

    // page shown: the Maps tab's edits go in first, then if the ROM changed since we read it, the list and the
    // picked map get read again
    public override void Shown()
    {
        if (Rom == null) return;
        BeforeRead?.Invoke();
        if (!ChangedSinceRead) return;
        int map = _map;
        Loading = true;
        try { Names.Clear(); foreach (var name in ReadNames()) Names.Add(name); }
        finally { Loading = false; }
        int listIndex = _maps.IndexOf(map);
        SelectedIndex = -1;
        SelectedIndex = listIndex >= 0 ? listIndex : Names.Count > 0 ? 0 : -1;
    }

    protected override IEnumerable<string> ReadNames()
    {
        _maps = Enumerable.Range(2, L2Maps.Count(Rom!) - 2).Where(m => L2Chests.Records(Rom!, m).Count > 0).ToList();
        return _maps.Select(m => $"{m:X2}  {L2Maps.Name(Rom!, m)}  ({L2Chests.Records(Rom!, m).Count})");
    }

    protected override void Load(int index)
    {
        var options = new List<L2Option> { new(L2ChestRow.GoldChoice, "Gold") };
        options.AddRange(L2Battle.Items(Rom!).Select(i => new L2Option(i.Index, $"{i.Index:X3} {i.Name}")));
        _map = _maps[index];
        var chests = L2Chests.Read(Rom!, _map);
        _shown = new();
        var rows = new List<L2ChestRow>();
        foreach (var chest in chests)
        {
            int item = L2Gold.ToEditor(Rom!, chest.Item);
            _shown[chest.Spot] = item;
            rows.Add(new L2ChestRow
            {
                Spot = chest.Spot,
                Label = $"Chest {chest.Spot:X2}{(chest.X >= 0 ? $" at ({chest.X},{chest.Y})" : "")}{(chest.Special ? " *" : "")}",
                Options = options, Value = L2Gold.IsGold(item) ? L2ChestRow.GoldChoice : item,
                GoldAmount = L2Gold.IsGold(item) ? L2Gold.AmountOf(item) : 100, Changed = Edited,
            });
        }
        Rows = rows;
        int total = _maps.Sum(m => L2Chests.Records(Rom!, m).Count);
        Info = $"{chests.Count} chests on this map ({total} in the game). Places are in 16x16 blocks, as on the Maps page. " +
               "Each treasure has its own opened flag, so it is found once and the item can be anything. * = left out of the game's count of opened chests. To add chests or hidden items, use + Chest and + Hidden item on the Maps page.";
        GoldNote = L2Gold.CantInstall(Rom!) ?? $"Gold: pick Gold and type the amount (1 to {L2Gold.Max:N0}). The game says \"Found 500 GOLD\" and adds it to the party's gold, once. Up to {L2Gold.Slots} different amounts in the whole game.";
    }

    protected override bool IsField(string p) => false;

    protected override string? Write()
    {
        if (_map < 0) return null;
        var changed = Rows.Where(r => !_shown.TryGetValue(r.Spot, out var before) || before != r.EditorItem).ToDictionary(r => r.Spot, r => r.EditorItem);
        if (changed.Count == 0) return null;
        try
        {
            // read the list again (the Maps tab may have moved or changed it) and only change these spots
            var records = L2MapEdit.EditorRecords(Rom!, _map).Select(r => changed.TryGetValue(r.Spot, out int newItem) ? r with { Item = newItem } : r).ToList();
            L2Chests.WriteList(Rom!, _map, L2Gold.ToRom(Rom!, _map, records));
        }
        catch (Exception ex) { GoldNote = "Not written: " + ex.Message; return null; }
        foreach (var spotItem in changed) _shown[spotItem.Key] = spotItem.Value;
        return $"chests of map {_map:X2}";
    }
}
