using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using LufiaForge.Core.Battle;
using LufiaForge.Core.Maps;
using System.Collections.ObjectModel;
using System.Globalization;

namespace LufiaForge.Modules.GameData;

/// <summary>A subgroup of monsters (4 slots) as edited, with how often the selected group lists it.</summary>
public partial class SubgroupRow : ObservableObject
{
    public int Number { get; init; }
    public string Title => Number >= Encounters.FirstBoss ? $"Formation {Number:X2}" : $"Subgroup {Number:X2}";
    public string UsedBy { get; init; } = "";
    /// <summary>Times the selected group lists this subgroup (its chance is Times / all entries).</summary>
    public int Times { get; init; }
    public string Chance { get; init; } = "";
    public bool InGroup => Times > 0;
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
/// Random battle groups, the monster subgroups they draw from, scripted boss formations, which group each
/// map uses, and the world map's zones (see <see cref="Encounters"/> for how the game picks battles).
/// </summary>
public partial class EncounterEditorViewModel : GameDataEditorBase
{
    /// <summary>"Group 01 ..." then "Boss E6 ..."; index → group or formation number.</summary>
    public ObservableCollection<string> Entries { get; } = new();
    private readonly List<int> _entryNumbers = new();
    public ObservableCollection<string> MonsterChoices { get; } = new();
    public ObservableCollection<SubgroupRow> Rows { get; } = new();
    public ObservableCollection<MapGroupRow> MapGroups { get; } = new();
    /// <summary>Every subgroup, for "add an existing subgroup".</summary>
    public ObservableCollection<string> SubgroupChoices { get; } = new();

    [ObservableProperty] private int _selectedIndex = -1;
    [ObservableProperty] private string _subgroupsText = "";
    [ObservableProperty] private bool _isGroup;
    [ObservableProperty] private string _roomText = "";
    [ObservableProperty] private string _whereText = "";
    [ObservableProperty] private int _addChoice = -1;

    private List<List<int>> _groups = new();
    private Dictionary<int, string> _mapLabels = new();
    /// <summary>Monster edits not yet applied, by subgroup (kept while the list of subgroups changes).</summary>
    private readonly Dictionary<int, int[]> _pending = new();

    private int Count => _groups.Count;
    public int SelectedGroup => SelectedIndex >= 0 && SelectedIndex < _entryNumbers.Count && _entryNumbers[SelectedIndex] < Encounters.FirstBoss
        ? _entryNumbers[SelectedIndex] : 0;

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
        ZonesDirty = false;
        _groups = Enumerable.Range(1, Encounters.GroupCount(rom)).Select(g => Encounters.GroupSubgroups(rom, g)).ToList();
        _pending.Clear();
        BuildEntries();
        BuildMapGroups();
        SelectedIndex = -1;
        SelectedIndex = 0;
        StartWorldMap();
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
        BuildSubgroupChoices();
    }

    private void BuildSubgroupChoices()
    {
        SubgroupChoices.Clear();
        for (int s = 1; s <= Encounters.SubgroupCount; s++)
        {
            var names = Monsters(s).Where(m => m != 0xFF).Select(MonsterName).ToList();
            int users = _groups.Count(g => g.Contains(s));
            SubgroupChoices.Add($"{s:X2}  {(names.Count == 0 ? "(empty)" : string.Join(", ", names))}{(users == 0 ? "  - free" : "")}");
        }
    }

    private string MonsterName(int m) => m < GameDataOffsets.MonsterCount ? MonsterChoices[m][3..].Trim() : "";

    /// <summary>A subgroup's monsters: unapplied edits first, else the ROM.</summary>
    private int[] Monsters(int s) => _pending.TryGetValue(s, out var p) ? p : Encounters.SubgroupMonsters(Ctx!.Rom, s);

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

    partial void OnSelectedIndexChanged(int value)
    {
        _pending.Clear();
        LoadSelected();
        OnPropertyChanged(nameof(SelectedGroup));
        DrawZones();
    }

    protected override void LoadSelected()
    {
        Rows.Clear();
        if (Ctx == null || SelectedIndex < 0 || SelectedIndex >= _entryNumbers.Count) return;
        int n = _entryNumbers[SelectedIndex];
        IsGroup = n < Encounters.FirstBoss;
        if (IsGroup)
        {
            _subgroupsTextFromCode = true;
            SubgroupsText = string.Join(" ", _groups[n - 1].Select(s => s.ToString("X2")));
            _subgroupsTextFromCode = false;
            ShowSubgroups(_groups[n - 1]);
            var maps = _mapLabels.Where(kv => Encounters.MapGroups(Ctx.Rom, kv.Key).Any(x => x.Group == n)).Select(kv => kv.Value).ToList();
            int cells = _zones.Count(z => z == n);
            WhereText = (cells > 0 ? $"World map: {cells} zones of 4x4 tiles (see the World map page). " : "") +
                        (maps.Count > 0 ? "Maps: " + string.Join(", ", maps) : cells > 0 ? "" : "No map uses this group yet: paint it on the world map, or give it to a map on the right.");
        }
        else
        {
            SubgroupsText = "";
            var m = Encounters.BossMonsters(Ctx.Rom, n);
            Rows.Add(Row(n, m, "Scripted battle (event command 1F)", 0, ""));
            WhereText = "Used by event command 1F (Start a battle) with this formation.";
        }
        UpdateRoom();
    }

    private SubgroupRow Row(int number, int[] m, string usedBy, int times, string chance) => new()
    {
        Number = number, UsedBy = usedBy, Times = times, Chance = chance,
        M1 = Index(m[0]), M2 = Index(m[1]), M3 = Index(m[2]), M4 = Index(m[3]),
    };

    private int Index(int monster) => monster < GameDataOffsets.MonsterCount ? monster : MonsterChoices.Count - 1;
    private int Monster(int index) => index >= 0 && index < GameDataOffsets.MonsterCount ? index : 0xFF;
    private int[] RowMonsters(SubgroupRow r) => new[] { Monster(r.M1), Monster(r.M2), Monster(r.M3), Monster(r.M4) };

    /// <summary>Keeps the monster choices of the rows on screen before the rows are rebuilt.</summary>
    private void StashRows()
    {
        if (!IsGroup) return;
        foreach (var r in Rows)
        {
            var m = RowMonsters(r);
            if (!m.SequenceEqual(Encounters.SubgroupMonsters(Ctx!.Rom, r.Number))) _pending[r.Number] = m;
            else _pending.Remove(r.Number);
        }
    }

    private void ShowSubgroups(List<int> subs)
    {
        Rows.Clear();
        int total = subs.Count;
        foreach (int s in subs.Distinct())
        {
            if (s < 1 || s > Encounters.SubgroupCount) continue;
            int times = subs.Count(x => x == s);
            var users = _groups.Select((g, i) => (g, i)).Where(x => x.g.Contains(s) && x.i + 1 != SelectedGroup).Select(x => $"{x.i + 1:X2}").ToList();
            string shared = users.Count == 0 ? "only in this group" : "also in group" + (users.Count > 1 ? "s " : " ") + string.Join(" ", users) + " (changing its monsters changes them too)";
            string chance = $"Listed {times} time{(times == 1 ? "" : "s")} of {total}: {times * 100.0 / total:0.#}% of this group's battles";
            Rows.Add(Row(s, Monsters(s), shared, times, chance));
        }
    }

    private bool _subgroupsTextFromCode;

    partial void OnSubgroupsTextChanged(string value)
    {
        if (_subgroupsTextFromCode || !IsGroup || ParseSubgroups(value) is not { } subs) return;
        StashRows();
        ShowSubgroups(subs);
        UpdateRoom();
    }

    private void SetList(List<int> subs)
    {
        StashRows();
        _subgroupsTextFromCode = true;
        SubgroupsText = string.Join(" ", subs.Select(s => s.ToString("X2")));
        _subgroupsTextFromCode = false;
        ShowSubgroups(subs);
        UpdateRoom();
    }

    private List<int> CurrentList() => ParseSubgroups(SubgroupsText) ?? (SelectedGroup > 0 ? _groups[SelectedGroup - 1].ToList() : new List<int>());

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

    /// <summary>The groups as they would be written: the edited list for the selected group.</summary>
    private List<List<int>> GroupsWithEdit()
    {
        var groups = _groups.Select(g => g.ToList()).ToList();
        if (IsGroup && SelectedGroup > 0 && ParseSubgroups(SubgroupsText) is { } p) groups[SelectedGroup - 1] = p;
        return groups;
    }

    private void UpdateRoom()
    {
        if (Ctx == null) return;
        int used = Encounters.GroupBytes(GroupsWithEdit());
        int free = Encounters.GroupRoom - used;
        RoomText = $"Each battle picks one entry of this list at random. Group lists use {used} of {Encounters.GroupRoom} bytes" +
                   (free >= 0 ? $" ({free} free: each extra entry takes 1, a new group 4)." : $" - {-free} too many: remove some entries.");
    }

    // ── add / remove subgroups ──

    [RelayCommand]
    private void MoreLikely(SubgroupRow? row)
    {
        if (row == null || !IsGroup) return;
        var list = CurrentList(); list.Add(row.Number); SetList(list);
    }

    [RelayCommand]
    private void LessLikely(SubgroupRow? row)
    {
        if (row == null || !IsGroup) return;
        var list = CurrentList();
        if (list.Count(s => s == row.Number) == 1 && list.Count == 1) { Status = "A group needs at least one subgroup."; return; }
        list.RemoveAt(list.LastIndexOf(row.Number)); SetList(list);
    }

    [RelayCommand]
    private void RemoveSubgroup(SubgroupRow? row)
    {
        if (row == null || !IsGroup) return;
        var list = CurrentList();
        if (list.All(s => s == row.Number)) { Status = "A group needs at least one subgroup."; return; }
        list.RemoveAll(s => s == row.Number); SetList(list);
        Status = $"Subgroup {row.Number:X2} removed from this group (Apply to keep).";
    }

    [RelayCommand]
    private void AddExisting()
    {
        if (!IsGroup || AddChoice < 0) { Status = "Pick a subgroup to add first."; return; }
        var list = CurrentList(); list.Add(AddChoice + 1); SetList(list);
        Status = $"Subgroup {AddChoice + 1:X2} added (Apply to keep).";
    }

    /// <summary>A subgroup slot no group lists (counting the edit on screen), or 0.</summary>
    private int FreeSubgroup()
    {
        var used = GroupsWithEdit().SelectMany(g => g).ToHashSet();
        for (int s = 1; s <= Encounters.SubgroupCount; s++) if (!used.Contains(s)) return s;
        return 0;
    }

    [RelayCommand]
    private void NewSubgroup()
    {
        if (!IsGroup || Ctx == null) return;
        int s = FreeSubgroup();
        if (s == 0)
        {
            Status = $"All {Encounters.SubgroupCount} subgroups are listed by some group. Remove a subgroup from the groups that don't need it (it becomes free), or add an existing one instead.";
            return;
        }
        // start from the first monsters of this group so it isn't empty
        var list = CurrentList();
        _pending[s] = Rows.Count > 0 ? RowMonsters(Rows[0]) : new[] { 0, 0xFF, 0xFF, 0xFF };
        list.Add(s); SetList(list);
        Status = $"New subgroup {s:X2} (a free slot) added with a copy of the first monsters: choose its monsters, then Apply.";
    }

    // ── new / delete group ──

    [RelayCommand]
    private void NewGroup()
    {
        if (Ctx == null) return;
        var groups = GroupsWithEdit();
        int first = IsGroup && SelectedGroup > 0 ? groups[SelectedGroup - 1][0] : 1;
        groups.Add(new List<int> { first });
        if (groups.Count > Encounters.MaxGroups) { Status = $"The game allows at most {Encounters.MaxGroups:X2} groups."; return; }
        if (Encounters.GroupBytes(groups) > Encounters.GroupRoom)
        {
            Status = $"No room for a new group: it needs 4 bytes and {Encounters.GroupRoom - Encounters.GroupBytes(GroupsWithEdit())} are free. Remove a duplicate entry from some group first.";
            return;
        }
        int n = groups.Count;
        bool ok = false;
        Commit($"New encounter group {n:X2}", () =>
        {
            Encounters.WriteGroups(Ctx.Rom, groups);
            foreach (var (s, m) in _pending) Encounters.SetSubgroupMonsters(Ctx.Rom, s, m);
            _groups = groups;
            ok = true;
        });
        if (!ok) return;
        _pending.Clear();
        BuildEntries();
        SelectedIndex = n - 1;
        Status = $"Group {n:X2} added (with subgroup {first:X2}); written to ROM (unsaved). Add its subgroups, then paint it on the world map or give it to a map.";
    }

    [RelayCommand]
    private void DeleteGroup()
    {
        if (Ctx == null || !IsGroup) return;
        int g = SelectedGroup;
        if (g != Count) { Status = $"Only the last group ({Count:X2}) can be deleted: deleting another would renumber the groups after it, which maps and zones refer to by number."; return; }
        if (g == 1) { Status = "There must be at least one group."; return; }
        if (_zones.Any(z => z == g) || _mapLabels.Keys.Any(id => Encounters.MapGroups(Ctx.Rom, id).Any(x => x.Group == g)) || MapGroups.Any(r => r.Group.Trim().ToUpperInvariant() == g.ToString("X2")))
        { Status = $"Group {g:X2} is still used by the world map or a map; give those another group first."; return; }
        var groups = _groups.Take(g - 1).Select(x => x.ToList()).ToList();
        bool ok = false;
        Commit($"Delete encounter group {g:X2}", () => { Encounters.WriteGroups(Ctx.Rom, groups); _groups = groups; ok = true; });
        if (!ok) return;
        _pending.Clear();
        BuildEntries();
        SelectedIndex = g - 2;
    }

    [RelayCommand]
    private void Apply()
    {
        if (Ctx == null || SelectedIndex < 0) return;
        var rom = Ctx.Rom;
        int n = _entryNumbers[SelectedIndex];
        if (IsGroup && ParseSubgroups(SubgroupsText) == null)
        {
            Status = $"Subgroups: hex numbers 01-{Encounters.SubgroupCount:X2} separated by spaces.";
            return;
        }
        StashRows();
        foreach (var r in Rows)
            if (!Encounters.HasMonster(RowMonsters(r)))
            { Status = $"{r.Title} has no monsters: the game would hang looking for one. Pick at least one."; return; }
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
                    var groups = GroupsWithEdit();
                    Encounters.WriteGroups(rom, groups);
                    _groups = groups;
                    foreach (var (s, m) in _pending) Encounters.SetSubgroupMonsters(rom, s, m);
                }
                else
                {
                    Encounters.SetBossMonsters(rom, n, RowMonsters(Rows[0]));
                }
                foreach (var (o, g) in mapEdits) rom.WriteByte(o, (byte)g);
            });
        }
        catch (InvalidOperationException ex) { Status = ex.Message; return; }
        int keep = SelectedIndex;
        _pending.Clear();
        BuildEntries();
        SelectedIndex = -1; SelectedIndex = keep;
    }
}
