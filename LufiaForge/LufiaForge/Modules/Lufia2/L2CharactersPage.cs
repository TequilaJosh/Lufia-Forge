using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using LufiaForge.Core.Lufia2;
using LufiaForge.Modules.Common;
using Color = System.Windows.Media.Color;

namespace LufiaForge.Modules.Lufia2;

/// <summary>A row of numbers (a growth row: 7 stats and the EXP growth).</summary>
public sealed class L2Row
{
    public string Label { get; init; } = "";
    public List<L2Word> Cells { get; init; } = new();
}

/// <summary>A band of levels and how much a stat grows per level in it (shown as a decimal or a percentage).</summary>
public partial class L2Band : ObservableObject
{
    public string Label { get; init; } = "";
    public string Unit { get; init; } = "";
    [ObservableProperty] private double _value;
    public Action? Changed { get; set; }
    partial void OnValueChanged(double value) => Changed?.Invoke();
}

/// <summary>The heroes: starting stats, growth per level (as a chart and as gains per level), the EXP curve, what each
/// starts the game with (level, EXP, magic, equipment), the Ancient Cave's bonuses, and the party, gold and items of a
/// new game.</summary>
public partial class L2CharactersPage : L2Page
{
    private L2Characters.Start? _start;
    private List<L2Characters.HeroRecord> _cave = new();
    private int[][] _rows = Array.Empty<int[]>();
    private List<int> _spellOrder = new();

    [ObservableProperty] private int _hp, _mp, _str, _agl, _int, _gut, _mgr;
    [ObservableProperty] private string _name = "";
    [ObservableProperty] private int _level, _exp, _ip;
    [ObservableProperty] private double _expBase;
    [ObservableProperty] private List<L2Slot> _equipment = new();
    [ObservableProperty] private List<L2Flag> _startSpells = new();
    [ObservableProperty] private string _spellsHint = "";
    [ObservableProperty] private List<L2Word> _caveBonuses = new();
    [ObservableProperty] private string _expHint = "";

    // growth: the stat shown, its bands, the chart and the table of levels
    public List<L2Option> GrowthStats { get; } = L2Characters.Stats.Select((s, i) => new L2Option(i, s)).Append(new L2Option(7, "EXP")).ToList();
    [ObservableProperty] private int _growthStat;
    [ObservableProperty] private List<L2Band> _bands = new();
    [ObservableProperty] private string _bandsHint = "";
    [ObservableProperty] private IReadOnlyList<ChartSeries>? _chart;
    [ObservableProperty] private string _levelTable = "";

    // the new game as a whole
    [ObservableProperty] private int _gold;
    [ObservableProperty] private List<L2Slot> _party = new();
    [ObservableProperty] private List<L2Slot> _inventory = new();
    [ObservableProperty] private List<L2Word> _inventoryCounts = new();

    private static readonly Color[] StatColors =
    {
        Color.FromRgb(0xE8, 0x60, 0x60), Color.FromRgb(0x60, 0x90, 0xF0), Color.FromRgb(0xF0, 0xB0, 0x40), Color.FromRgb(0x60, 0xD0, 0x80),
        Color.FromRgb(0xB0, 0x80, 0xF0), Color.FromRgb(0xD0, 0xD0, 0x60), Color.FromRgb(0x60, 0xD0, 0xD0), Color.FromRgb(0xF0, 0xF0, 0xF0),
    };

    protected override IEnumerable<string> ReadNames() => L2Characters.Names;

    protected override void Load(int index)
    {
        var rom = Rom!;
        _start = L2Characters.ReadStart(rom);
        _cave = L2Characters.ReadCave(rom);
        var items = L2Battle.Items(rom).Select(i => new L2Option(i.Index, $"{i.Index:X3} {i.Name}")).ToList();

        var v = L2Characters.Read(rom, index);
        Hp = v[0]; Mp = v[1]; Str = v[2]; Agl = v[3]; Int = v[4]; Gut = v[5]; Mgr = v[6];
        ExpBase = L2Characters.ReadExpBase(rom, index);
        _rows = Enumerable.Range(0, L2Characters.GrowthRows).Select(r => L2Characters.ReadGrowth(rom, index, r)).ToArray();

        var h = _start.Heroes[index];
        Name = h.Name; Level = h.Level; Exp = h.Exp; Ip = h.Ip;
        Equipment = h.Words.Select((w, i) => new L2Slot { Label = L2Characters.EquipSlots[i], Options = items, Value = w, Changed = Edited }).ToList();

        // starting magic: every spell, ticked when known at the start (in the order the game lists them)
        _spellOrder = L2StartSpells.Of(rom, index);
        StartSpells = L2Battle.Spells(rom).Select(s => new L2Flag { Label = $"{s.Index:X2} {s.Name}", Bit = s.Index, IsChecked = _spellOrder.Contains(s.Index), Changed = Edited }).ToList();
        SpellsHint = L2StartSpells.CanChange(rom)
            ? $"Ticked spells are known from the start ({_spellOrder.Count}). Any hero can start with any spell; they still need the MP."
            : "This ROM's new-game code was changed by another patch, so the starting magic can't be changed here.";

        CaveBonuses = _cave[index].Words.Select((w, i) => new L2Word { Label = L2Characters.CaveBonuses[i], Bit = i, Value = w, Changed = Edited }).ToList();

        Gold = _start.Gold;
        var heroes = new[] { new L2Option(0xFF, "(empty)") }.Concat(L2Characters.Names.Select((n, i) => new L2Option(i, n))).ToList();
        Party = _start.Party.Select((p, i) => new L2Slot { Label = $"{i + 1}.", Options = heroes, Value = p, Changed = PartyEdited }).ToList();
        Inventory = _start.Inventory.Select((it, i) => new L2Slot { Label = $"{i + 1}.", Options = items, Value = it.Item, Changed = PartyEdited }).ToList();
        InventoryCounts = _start.Inventory.Select((it, i) => new L2Word { Label = "x", Bit = i, Value = it.Count, Changed = PartyEdited }).ToList();
        ShowBands();
        Refresh();
    }

    /// <summary>The levels a growth row is used for: row r takes a hero from level 8r (at least 1) to the next, so it
    /// makes levels 8r+1 to 8r+8.</summary>
    private static string BandLabel(int r) => $"Levels {Math.Max(2, r * 8 + 1)}-{Math.Min(r * 8 + 8, L2Characters.MaxLevel)}";

    partial void OnGrowthStatChanged(int value) { if (Rom != null && SelectedIndex >= 0) { bool l = Loading; Loading = true; try { ShowBands(); } finally { Loading = l; } Refresh(); } }

    private void ShowBands()
    {
        int s = Math.Clamp(GrowthStat, 0, 7);
        bool exp = s == 7;
        Bands = Enumerable.Range(0, _rows.Length).Select(r => new L2Band
        {
            Label = BandLabel(r),
            Unit = exp ? "% more EXP each level" : $"{L2Characters.Stats[s]} per level",
            Value = exp ? Math.Round((_rows[r][7] + 1) * 100.0 / 256, 1) : Math.Round(_rows[r][s] / 16.0, 2),
            Changed = Edited,
        }).ToList();
        BandsHint = exp
            ? "How much more EXP each level needs than the one before, in each band of levels (the game keeps it the same for every hero). The curve starts at 'EXP curve start'."
            : s == 5
                ? "GUT grows by exactly this much each level."
                : "Each level adds this much, plus 0 to 4 at random (the chart and the table leave the random part out).";
    }

    private void Refresh()
    {
        if (Rom == null || SelectedIndex < 0) return;
        int c = SelectedIndex;
        var levels = Enumerable.Range(1, L2Characters.MaxLevel).ToArray();
        var stats = levels.Select(lv => L2Characters.StatsAt(Rom, c, lv)).ToArray();
        if (GrowthStat == 7)
            Chart = new[] { new ChartSeries("EXP for the next level", levels.Take(97).Select(lv => (double)L2Characters.ExpNeeded(Rom, c, lv)).ToArray(), StatColors[7], true) };
        else
            Chart = Enumerable.Range(0, 7).Select(i => new ChartSeries(L2Characters.Stats[i], stats.Select(x => (double)x[i]).ToArray(), StatColors[i], i == GrowthStat)).ToList();
        string Row(int lv) => $"{lv,5}  " + string.Join("", stats[lv - 1].Select(x => $"{x,6}")) + $"  {L2Characters.ExpNeeded(Rom, c, lv),12:N0}";
        LevelTable = "Level" + string.Join("", L2Characters.Stats.Select(x => $"{x,6}")) + "  EXP for next\n" +
                     string.Join("\n", new[] { 1, 5, 10, 20, 30, 40, 50, 60, 70, 80, 90, 99 }.Select(Row));
        int lo = Level <= 1 ? 0 : L2Characters.ExpNeeded(Rom, c, Level - 1), hi = L2Characters.ExpNeeded(Rom, c, Level);
        ExpHint = Exp >= lo && Exp < hi
            ? $"Level {Level} covers {lo:N0} to {hi - 1:N0} EXP."
            : $"Level {Level} needs {lo:N0} to {hi - 1:N0} EXP: with {Exp:N0} the game would put the hero at another level.";
    }

    protected override bool IsField(string p) => p is nameof(Hp) or nameof(Mp) or nameof(Str) or nameof(Agl) or nameof(Int) or nameof(Gut) or nameof(Mgr)
        or nameof(Name) or nameof(Level) or nameof(Exp) or nameof(Ip) or nameof(ExpBase);

    protected override string? Write()
    {
        if (_start == null) return null;
        var rom = Rom!;
        int c = SelectedIndex;
        L2Characters.Write(rom, c, new[] { Hp, Mp, Str, Agl, Int, Gut, Mgr });
        L2Characters.WriteExpBase(rom, c, ExpBase);
        int s = Math.Clamp(GrowthStat, 0, 7);
        for (int r = 0; r < Bands.Count && r < _rows.Length; r++)
            _rows[r][s] = s == 7 ? Math.Clamp((int)Math.Round(Bands[r].Value * 256 / 100) - 1, 0, 255) : Math.Clamp((int)Math.Round(Bands[r].Value * 16), 0, 255);
        for (int r = 0; r < _rows.Length; r++) L2Characters.WriteGrowth(rom, c, r, _rows[r]);
        var h = _start.Heroes[c];
        h.Name = Name; h.Level = Level; h.Exp = Exp; h.Ip = Ip;
        for (int i = 0; i < Equipment.Count; i++) h.Words[i] = Equipment[i].Value;
        L2Characters.WriteRecord(rom, h);
        // magic: the ticked spells, the ones already known keeping their place, new ones added at the end
        var ticked = StartSpells.Where(f => f.IsChecked).Select(f => f.Bit).ToList();
        _spellOrder = _spellOrder.Where(ticked.Contains).Concat(ticked.Where(x => !_spellOrder.Contains(x))).Take(L2StartSpells.Slots).ToList();
        if (L2StartSpells.CanChange(rom)) L2StartSpells.Set(rom, c, _spellOrder);
        var cave = _cave[c];
        for (int i = 0; i < CaveBonuses.Count; i++) cave.Words[i] = CaveBonuses[i].Value;
        L2Characters.WriteRecord(rom, cave);
        Refresh();
        return L2Characters.Names[c];
    }

    partial void OnGoldChanged(int value) => PartyEdited();

    private void PartyEdited()
    {
        if (Loading || Rom == null || Main == null || _start == null) return;
        _start.Gold = Gold;
        for (int i = 0; i < 4 && i < Party.Count; i++) _start.Party[i] = Party[i].Value;
        // the party is packed to the front; its size is how many are in it
        var ids = _start.Party.Where(p => p != 0xFF).ToList();
        if (ids.Count == 0) ids.Add(0);
        for (int i = 0; i < 4; i++) _start.Party[i] = i < ids.Count ? ids[i] : 0xFF;
        _start.PartySize = ids.Count;
        for (int i = 0; i < _start.Inventory.Count; i++) _start.Inventory[i] = (Inventory[i].Value, InventoryCounts[i].Value);
        L2Characters.WriteStart(Rom, _start);
        Main.NotifyRomModified("Lufia II: the new game's party, gold and items");
    }
}
