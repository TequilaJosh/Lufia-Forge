using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using LufiaForge.Core.Battle;
using LufiaForge.Core.Maps;
using System.Collections.ObjectModel;
using System.Globalization;

namespace LufiaForge.Modules.GameData;

/// <summary>A subgroup of monsters (4 slots) as edited.</summary>
public partial class SubgroupRow : ObservableObject
{
    public int Number { get; init; }
    public string Title => Number >= Encounters.FirstBoss ? $"Formation {Number:X2}" : $"Subgroup {Number:X2}";
    public string UsedBy { get; init; } = "";
    [ObservableProperty] private int _m1;
    [ObservableProperty] private int _m2;
    [ObservableProperty] private int _m3;
    [ObservableProperty] private int _m4;
}

/// <summary>A map that picks a random-battle group in its setup script.</summary>
public partial class MapGroupRow : ObservableObject
{
    public int Offset { get; init; }
    public string Map { get; init; } = "";
    public bool Conditional { get; init; }
    public string Note => Conditional ? "(only in some story states)" : "";
    [ObservableProperty] private string _group = "";
}

/// <summary>
/// Random battle groups, the monster subgroups they draw from, scripted boss formations, and which group each
/// map uses (see <see cref="Encounters"/> for how the game picks battles).
/// </summary>
public partial class EncounterEditorViewModel : GameDataEditorBase
{
    /// <summary>"Group 01 ..." then "Boss E6 ..."; index → group or formation number.</summary>
    public ObservableCollection<string> Entries { get; } = new();
    private readonly List<int> _entryNumbers = new();
    public ObservableCollection<string> MonsterChoices { get; } = new();
    public ObservableCollection<SubgroupRow> Rows { get; } = new();
    public ObservableCollection<MapGroupRow> MapGroups { get; } = new();

    [ObservableProperty] private int _selectedIndex = -1;
    [ObservableProperty] private string _subgroupsText = "";
    [ObservableProperty] private bool _isGroup;
    [ObservableProperty] private string _roomText = "";
    [ObservableProperty] private string _whereText = "";

    private List<List<int>> _groups = new();
    private Dictionary<int, string> _mapLabels = new();
    private byte[] _zones = Array.Empty<byte>();

    private int Count => Ctx == null ? 0 : Encounters.GroupCount(Ctx.Rom);

    protected override void OnRomLoaded()
    {
        if (Ctx == null) return;
        var rom = Ctx.Rom;
        MonsterChoices.Clear();
        for (int i = 0; i < GameDataOffsets.MonsterCount; i++)
            MonsterChoices.Add($"{i:X2} {Ctx.ReadName(Ctx.MonsterOffset(i), GameDataOffsets.MonsterNameLen).Replace('@', ' ')}");
        MonsterChoices.Add("FF (none)");
        _mapLabels = MapCatalog.ScanNamed(rom).ToDictionary(m => m.MapId, m => m.Label);
        try { _zones = Encounters.ReadZones(rom); } catch { _zones = Array.Empty<byte>(); }
        _groups = Enumerable.Range(1, Count).Select(g => Encounters.GroupSubgroups(rom, g)).ToList();
        BuildEntries();
        BuildMapGroups();
        SelectedIndex = -1;
        SelectedIndex = 0;
    }

    private void BuildEntries()
    {
        Entries.Clear(); _entryNumbers.Clear();
        for (int g = 1; g <= Count; g++)
        {
            Entries.Add($"Group {g:X2}  {Summary(g)}");
            _entryNumbers.Add(g);
        }
        for (int f = Encounters.FirstBoss; f <= Encounters.LastBoss; f++)
        {
            var ms = Encounters.BossMonsters(Ctx!.Rom, f).Where(m => m != 0xFF).Select(MonsterName).Distinct();
            Entries.Add($"Boss {f:X2}  {string.Join(", ", ms)}");
            _entryNumbers.Add(f);
        }
    }

    private string MonsterName(int m) => m < GameDataOffsets.MonsterCount ? MonsterChoices[m][3..].Trim() : "";

    private string Summary(int g)
    {
        var maps = _mapLabels.Keys.Where(id => Encounters.MapGroups(Ctx!.Rom, id).Any(x => x.Group == g))
                             .Select(id => _mapLabels[id].Length > 4 ? _mapLabels[id][4..].Trim() : _mapLabels[id]).ToList();
        int cells = _zones.Count(z => z == g);
        if (cells > 0) maps.Insert(0, "world map");
        return maps.Count == 0 ? "(not used by any map)" : string.Join(", ", maps.Take(3)) + (maps.Count > 3 ? $" +{maps.Count - 3}" : "");
    }

    private void BuildMapGroups()
    {
        MapGroups.Clear();
        foreach (var (id, label) in _mapLabels.OrderBy(kv => kv.Key))
            foreach (var g in Encounters.MapGroups(Ctx!.Rom, id))
                MapGroups.Add(new MapGroupRow { Offset = g.Offset, Map = label, Conditional = g.Conditional, Group = g.Group.ToString("X2") });
    }

    partial void OnSelectedIndexChanged(int value) => LoadSelected();

    protected override void LoadSelected()
    {
        Rows.Clear();
        if (Ctx == null || SelectedIndex < 0 || SelectedIndex >= _entryNumbers.Count) return;
        int n = _entryNumbers[SelectedIndex];
        IsGroup = n < Encounters.FirstBoss;
        if (IsGroup)
        {
            SubgroupsText = string.Join(" ", _groups[n - 1].Select(s => s.ToString("X2")));
            ShowSubgroups(_groups[n - 1]);
            var maps = _mapLabels.Where(kv => Encounters.MapGroups(Ctx.Rom, kv.Key).Any(x => x.Group == n)).Select(kv => kv.Value).ToList();
            int cells = _zones.Count(z => z == n);
            WhereText = (cells > 0 ? $"World map: {cells} zones of 4x4 tiles. " : "") +
                        (maps.Count > 0 ? "Maps: " + string.Join(", ", maps) : cells > 0 ? "" : "No map uses this group.");
        }
        else
        {
            SubgroupsText = "";
            var m = Encounters.BossMonsters(Ctx.Rom, n);
            Rows.Add(Row(n, m, "Scripted battle (event command 1F)"));
            WhereText = "Used by event command 1F (Start a battle) with this formation.";
        }
        UpdateRoom();
    }

    private SubgroupRow Row(int number, int[] m, string usedBy) => new()
    {
        Number = number, UsedBy = usedBy,
        M1 = Index(m[0]), M2 = Index(m[1]), M3 = Index(m[2]), M4 = Index(m[3]),
    };

    private int Index(int monster) => monster < GameDataOffsets.MonsterCount ? monster : MonsterChoices.Count - 1;
    private int Monster(int index) => index >= 0 && index < GameDataOffsets.MonsterCount ? index : 0xFF;

    private void ShowSubgroups(IEnumerable<int> subs)
    {
        Rows.Clear();
        foreach (int s in subs.Distinct())
        {
            if (s < 1 || s > Encounters.SubgroupCount) continue;
            var users = _groups.Select((g, i) => (g, i)).Where(x => x.g.Contains(s)).Select(x => $"{x.i + 1:X2}");
            Rows.Add(Row(s, Encounters.SubgroupMonsters(Ctx!.Rom, s), "in groups " + string.Join(" ", users)));
        }
    }

    partial void OnSubgroupsTextChanged(string value)
    {
        if (!IsGroup || ParseSubgroups(value) is not { } subs) return;
        ShowSubgroups(subs);
        UpdateRoom();
    }

    private static List<int>? ParseSubgroups(string text)
    {
        var list = new List<int>();
        foreach (var part in text.Split(new[] { ' ', ',' }, StringSplitOptions.RemoveEmptyEntries))
        {
            if (!int.TryParse(part, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out int v) || v < 1 || v > Encounters.SubgroupCount) return null;
            list.Add(v);
        }
        return list.Count is >= 1 and <= 255 ? list : null;
    }

    private void UpdateRoom()
    {
        if (Ctx == null) return;
        int used = 0;
        for (int g = 0; g < _groups.Count; g++)
            used += 1 + (IsGroup && SelectedIndex == g && ParseSubgroups(SubgroupsText) is { } p ? p.Count : _groups[g].Count);
        RoomText = $"Group lists use {used} of {Encounters.RoomForGroups(Ctx.Rom)} bytes. A battle picks one subgroup of the list at random (list one twice to make it more likely).";
    }

    [RelayCommand]
    private void Apply()
    {
        if (Ctx == null || SelectedIndex < 0) return;
        var rom = Ctx.Rom;
        int n = _entryNumbers[SelectedIndex];
        List<int>? subs = null;
        if (IsGroup && (subs = ParseSubgroups(SubgroupsText)) == null)
        {
            Status = $"Subgroups: hex numbers 01-{Encounters.SubgroupCount:X2} separated by spaces.";
            return;
        }
        var mapEdits = new List<(int Offset, int Group)>();
        foreach (var r in MapGroups)
        {
            if (!int.TryParse(r.Group, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out int g) || g < 1 || g > Count)
            { Status = $"{r.Map}: the group must be 01-{Count:X2}."; return; }
            mapEdits.Add((r.Offset, g));
        }
        try
        {
            Commit(IsGroup ? $"Encounter group {n:X2}" : $"Boss formation {n:X2}", () =>
            {
                if (IsGroup)
                {
                    var groups = _groups.Select(g => g.ToList()).ToList();
                    groups[n - 1] = subs!;
                    Encounters.WriteGroups(rom, groups);
                    _groups = groups;
                    foreach (var r in Rows) Encounters.SetSubgroupMonsters(rom, r.Number, new[] { Monster(r.M1), Monster(r.M2), Monster(r.M3), Monster(r.M4) });
                }
                else
                {
                    var r = Rows[0];
                    Encounters.SetBossMonsters(rom, n, new[] { Monster(r.M1), Monster(r.M2), Monster(r.M3), Monster(r.M4) });
                }
                foreach (var (o, g) in mapEdits) rom.WriteByte(o, (byte)g);
            });
        }
        catch (InvalidOperationException ex) { Status = ex.Message; return; }
        int keep = SelectedIndex;
        BuildEntries();
        SelectedIndex = -1; SelectedIndex = keep;
    }
}
