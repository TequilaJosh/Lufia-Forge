using CommunityToolkit.Mvvm.ComponentModel;
using LufiaForge.Core.Lufia2;

namespace LufiaForge.Modules.Lufia2;

/// <summary>A row of numbers (a growth row: 7 stats and the EXP growth).</summary>
public sealed class L2Row
{
    public string Label { get; init; } = "";
    public List<L2Word> Cells { get; init; } = new();
}

/// <summary>The heroes: starting stats, growth per level, the EXP curve, what each starts the game with (level, EXP,
/// spells, equipment), the Ancient Cave's bonuses, and the party, gold and items of a new game.</summary>
public partial class L2CharactersPage : L2Page
{
    private L2Characters.Start? _start;
    private List<L2Characters.HeroRecord> _cave = new();

    [ObservableProperty] private int _hp, _mp, _str, _agl, _int, _gut, _mgr;
    [ObservableProperty] private string _name = "";
    [ObservableProperty] private int _level, _exp, _ip;
    [ObservableProperty] private double _expBase;
    [ObservableProperty] private List<L2Slot> _equipment = new();
    [ObservableProperty] private List<L2Slot> _spells = new();
    [ObservableProperty] private List<L2Row> _growth = new();
    [ObservableProperty] private List<L2Word> _caveBonuses = new();
    [ObservableProperty] private string _preview = "";
    [ObservableProperty] private string _expHint = "";
    [ObservableProperty] private string _spellsHint = "";

    // the new game as a whole
    [ObservableProperty] private int _gold;
    [ObservableProperty] private List<L2Slot> _party = new();
    [ObservableProperty] private List<L2Slot> _inventory = new();
    [ObservableProperty] private List<L2Word> _inventoryCounts = new();

    private static readonly string[] Columns = L2Characters.Stats.Append("EXP").ToArray();
    public string[] GrowthColumns => Columns;

    protected override IEnumerable<string> ReadNames() => L2Characters.Names;

    protected override void Load(int index)
    {
        var rom = Rom!;
        _start = L2Characters.ReadStart(rom);
        _cave = L2Characters.ReadCave(rom);
        var items = L2Battle.Items(rom).Select(i => new L2Option(i.Index, $"{i.Index:X3} {i.Name}")).ToList();
        var spells = L2Battle.Spells(rom).Select(s => new L2Option(s.Index, $"{s.Index:X2} {s.Name}")).ToList();

        var v = L2Characters.Read(rom, index);
        Hp = v[0]; Mp = v[1]; Str = v[2]; Agl = v[3]; Int = v[4]; Gut = v[5]; Mgr = v[6];
        ExpBase = L2Characters.ReadExpBase(rom, index);
        Growth = Enumerable.Range(0, L2Characters.GrowthRows).Select(r =>
        {
            var g = L2Characters.ReadGrowth(rom, index, r);
            int from = Math.Max(1, r * 8), to = Math.Min(r * 8 + 7, 98);
            return new L2Row { Label = $"Lv {from}-{to + 1}", Cells = g.Select((x, i) => new L2Word { Label = Columns[i], Bit = i, Value = x, Changed = Edited }).ToList() };
        }).ToList();

        var h = _start.Heroes[index];
        Name = h.Name; Level = h.Level; Exp = h.Exp; Ip = h.Ip;
        Equipment = h.Words.Select((w, i) => new L2Slot { Label = L2Characters.EquipSlots[i], Options = items, Value = w, Changed = Edited }).ToList();
        Spells = h.Spells.Select((s, i) => new L2Slot { Label = $"{i + 1}.", Options = spells, Value = s, Changed = Edited }).ToList();
        SpellsHint = h.Spells.Count == 0
            ? "Starts with no spells (a list can't be added: the record would grow). Spells are bought in shops."
            : "The spells known at the start (the list keeps its length). Others are bought in shops.";
        CaveBonuses = _cave[index].Words.Select((w, i) => new L2Word { Label = L2Characters.CaveBonuses[i], Bit = i, Value = w, Changed = Edited }).ToList();

        Gold = _start.Gold;
        var heroes = new[] { new L2Option(0xFF, "(empty)") }.Concat(L2Characters.Names.Select((n, i) => new L2Option(i, n))).ToList();
        Party = _start.Party.Select((p, i) => new L2Slot { Label = $"{i + 1}.", Options = heroes, Value = p, Changed = PartyEdited }).ToList();
        Inventory = _start.Inventory.Select((it, i) => new L2Slot { Label = $"{i + 1}.", Options = items, Value = it.Item, Changed = PartyEdited }).ToList();
        InventoryCounts = _start.Inventory.Select((it, i) => new L2Word { Label = "x", Bit = i, Value = it.Count, Changed = PartyEdited }).ToList();
        Refresh();
    }

    private void Refresh()
    {
        if (Rom == null || SelectedIndex < 0) return;
        int c = SelectedIndex;
        string Row(int lv) => $"Lv {lv,2}: " + string.Join("  ", L2Characters.StatsAt(Rom, c, lv).Select((s, i) => $"{L2Characters.Stats[i]} {s}")) +
                              $"   next level at {L2Characters.ExpNeeded(Rom, c, lv):N0} EXP";
        Preview = string.Join("\n", new[] { 1, 10, 20, 30, 50, 75, 99 }.Select(Row));
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
        for (int r = 0; r < Growth.Count; r++) L2Characters.WriteGrowth(rom, c, r, Growth[r].Cells.Select(x => x.Value).ToArray());
        var h = _start.Heroes[c];
        h.Name = Name; h.Level = Level; h.Exp = Exp; h.Ip = Ip;
        for (int i = 0; i < Equipment.Count; i++) h.Words[i] = Equipment[i].Value;
        for (int i = 0; i < Spells.Count; i++) h.Spells[i] = Spells[i].Value;
        L2Characters.WriteRecord(rom, h);
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
