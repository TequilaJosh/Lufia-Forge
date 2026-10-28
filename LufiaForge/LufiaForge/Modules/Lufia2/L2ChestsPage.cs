using CommunityToolkit.Mvvm.ComponentModel;
using LufiaForge.Core.Lufia2;

namespace LufiaForge.Modules.Lufia2;

/// <summary>One treasure on the Treasure page: an item, or gold with its amount.</summary>
public partial class L2ChestRow : ObservableObject
{
    public const int GoldChoice = -2;
    public int Spot { get; init; }
    public string Label { get; init; } = "";
    public List<L2Option> Options { get; init; } = new();
    /// <summary>The item, or <see cref="GoldChoice"/>.</summary>
    [ObservableProperty] private int _value;
    [ObservableProperty] private int _goldAmount = 100;
    public bool IsGold => Value == GoldChoice;
    public Action? Changed { get; set; }
    /// <summary>The treasure as the editors keep it (gold: <see cref="L2Gold.EditorBase"/> + amount).</summary>
    public int EditorItem => IsGold ? L2Gold.Item(GoldAmount) : Value;
    partial void OnValueChanged(int value) { OnPropertyChanged(nameof(IsGold)); Changed?.Invoke(); }
    partial void OnGoldAmountChanged(int value) { if (IsGold) Changed?.Invoke(); }
}

/// <summary>Treasure chests: the maps that have some, and for the picked map each chest's place and item (or gold).
/// It shares the ROM with the Maps tab: the Maps tab's edits are written before this page reads a map
/// (<see cref="BeforeRead"/>), and a change here is written by spot over what the ROM holds then, so neither page
/// writes over the other's changes.</summary>
public partial class L2ChestsPage : L2Page
{
    private List<int> _maps = new();
    private int _map = -1;
    /// <summary>What each shown treasure held when the page read it (spot -> editor item).</summary>
    private Dictionary<int, int> _shown = new();
    [ObservableProperty] private List<L2ChestRow> _rows = new();
    [ObservableProperty] private string _info = "";
    [ObservableProperty] private string _goldNote = "";

    /// <summary>Called before the page reads the ROM (the Maps tab writes what it hasn't written yet).</summary>
    public Func<string?>? BeforeRead { get; set; }

    /// <summary>The page is shown: the Maps tab's edits go in first, then the list and the picked map are read again.</summary>
    public void Shown()
    {
        if (Rom == null) return;
        BeforeRead?.Invoke();
        int map = _map;
        Loading = true;
        try { Names.Clear(); foreach (var n in ReadNames()) Names.Add(n); }
        finally { Loading = false; }
        int i = _maps.IndexOf(map);
        SelectedIndex = -1;
        SelectedIndex = i >= 0 ? i : Names.Count > 0 ? 0 : -1;
    }

    protected override IEnumerable<string> ReadNames()
    {
        _maps = Enumerable.Range(2, L2Maps.Count - 2).Where(m => L2Chests.Records(Rom!, m).Count > 0).ToList();
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
        foreach (var c in chests)
        {
            int item = L2Gold.ToEditor(Rom!, c.Item);
            _shown[c.Spot] = item;
            rows.Add(new L2ChestRow
            {
                Spot = c.Spot,
                Label = $"Chest {c.Spot:X2}{(c.X >= 0 ? $" at ({c.X},{c.Y})" : "")}{(c.Special ? " *" : "")}",
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
        var changed = Rows.Where(r => !_shown.TryGetValue(r.Spot, out var was) || was != r.EditorItem).ToDictionary(r => r.Spot, r => r.EditorItem);
        if (changed.Count == 0) return null;
        try
        {
            // read the list again (the Maps tab may have moved or changed it) and change only these spots
            var recs = L2MapEdit.EditorRecords(Rom!, _map).Select(r => changed.TryGetValue(r.Spot, out int it) ? r with { Item = it } : r).ToList();
            L2Chests.WriteList(Rom!, _map, L2Gold.ToRom(Rom!, _map, recs));
        }
        catch (Exception ex) { GoldNote = "Not written: " + ex.Message; return null; }
        foreach (var kv in changed) _shown[kv.Key] = kv.Value;
        return $"chests of map {_map:X2}";
    }
}
