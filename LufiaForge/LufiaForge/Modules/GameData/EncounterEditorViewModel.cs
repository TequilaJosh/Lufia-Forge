using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using LufiaForge.Core.Battle;
using LufiaForge.Core.Maps;
using System.Collections.ObjectModel;
using System.Globalization;

namespace LufiaForge.Modules.GameData;

// a subgroup of monsters (4 slots) as edited, with how often the selected group lists it
public partial class SubgroupRow : ObservableObject
{
    public int Number { get; init; }
    public string Title => Number >= Encounters.FirstBoss ? $"Formation {Number:X2}" : $"Subgroup {Number:X2}";
    public string UsedBy { get; init; } = "";
    // how many times the selected group lists this subgroup (its chance = Times / all entries)
    public int Times { get; init; }
    public string Chance { get; init; } = "";
    public bool InGroup => Times > 0;
    [ObservableProperty] private int _m1;
    [ObservableProperty] private int _m2;
    [ObservableProperty] private int _m3;
    [ObservableProperty] private int _m4;
}

// a map that picks a random battle group in its setup script
public partial class MapGroupRow : ObservableObject
{
    public int Offset { get; init; }
    // the group the setup script had there when the row was read. only a changed row gets written, and only
    // while that byte is still a "0D group" of the same group
    public int LoadedGroup { get; init; }
    public string Map { get; init; } = "";
    public bool Conditional { get; init; }
    // the map has visible monsters (so no random battles)
    public bool Monsters { get; init; }
    public string Note => (Conditional ? "(only in some story states) " : "") + (Monsters ? "(visible monsters instead)" : "");
    [ObservableProperty] private string _group = "";
}

// random battle groups, the monster subgroups they draw from, scripted boss formations, which group each
// map uses, and the world map's zones (Encounters has how the game picks battles)
public partial class EncounterEditorViewModel : GameDataEditorBase
{
    // the editor footer shows a ROM offset, this editor doesn't have one of its own
    public string OffsetText => "";

    // "Group 01 ..." then "Boss E6 ...". _entryNumbers maps index -> group or formation number
    public ObservableCollection<string> Entries { get; } = new();
    private readonly List<int> _entryNumbers = new();
    public ObservableCollection<string> MonsterChoices { get; } = new();
    public ObservableCollection<SubgroupRow> Rows { get; } = new();
    public ObservableCollection<MapGroupRow> MapGroups { get; } = new();
    // every subgroup, for "add an existing subgroup"
    public ObservableCollection<string> SubgroupChoices { get; } = new();

    [ObservableProperty] private int _selectedIndex = -1;
    [ObservableProperty] private string _subgroupsText = "";
    [ObservableProperty] private bool _isGroup;
    [ObservableProperty] private string _roomText = "";
    [ObservableProperty] private string _whereText = "";
    [ObservableProperty] private int _addChoice = -1;
    [ObservableProperty] private string _limitText = "";
    [ObservableProperty] private bool _canPatch;

    private List<List<int>> _groups = new();
    private Dictionary<int, string> _mapLabels = new();
    // monster edits not applied yet, by subgroup. kept while the list of subgroups changes
    private readonly Dictionary<int, int[]> _pending = new();

    private int Count => _groups.Count;
    public int SelectedGroup => SelectedIndex >= 0 && SelectedIndex < _entryNumbers.Count && _entryNumbers[SelectedIndex] < Encounters.FirstBoss
        ? _entryNumbers[SelectedIndex] : 0;

    private void FillMonsterChoices()
    {
        if (Ctx == null) return;
        MonsterChoices.Clear();
        for (int i = 0, n = Ctx.MonsterCount; i < n; i++)
            MonsterChoices.Add($"{i:X2} {Ctx.ReadName(Ctx.MonsterOffset(i), GameDataOffsets.MonsterNameLen).Replace('@', ' ')}");
        MonsterChoices.Add("FF (none)");
    }

    // monster names or the number of monsters changed in the Monsters tab
    protected override void OnNamesChanged()
    {
        FillMonsterChoices();
        LoadSelected();
    }

    protected override void OnRomLoaded()
    {
        if (Ctx == null) return;
        var rom = Ctx.Rom;
        FillMonsterChoices();
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
        MonsterSpriteOptions.Clear();
        StartMonsterMaps();
    }

    private void BuildEntries()
    {
        Entries.Clear(); _entryNumbers.Clear();
        for (int group = 1; group <= Count; group++)
        {
            Entries.Add($"Group {group:X2}  {Summary(group)}");
            _entryNumbers.Add(group);
        }
        for (int formation = Encounters.FirstBoss; formation <= Encounters.LastBoss; formation++)
        {
            var monsterNames = Encounters.BossMonsters(Ctx!.Rom, formation).Where(m => m != 0xFF).Select(MonsterName).Distinct();
            Entries.Add($"Boss {formation:X2}  {string.Join(", ", monsterNames)}");
            _entryNumbers.Add(formation);
        }
        BuildSubgroupChoices();
        var rom = Ctx!.Rom;
        bool patched = SubgroupPatch.IsApplied(rom);
        CanPatch = !patched && SubgroupPatch.IsOriginal(rom);
        int used = _groups.SelectMany(g => g).Distinct().Count();
        LimitText = patched
            ? $"Subgroups: {used} in use of {Encounters.SubgroupCount(rom)} (more-subgroups patch applied; the group lists have {Encounters.GroupRoom(rom)} bytes)."
            : $"Subgroups: {used} in use of the game's {Encounters.SubgroupCount(rom)}. The patch raises this to {SubgroupPatch.MaxSubgroups} and gives the group lists {Encounters.Bosses - Encounters.GroupPointers - Encounters.GroupRoom(rom)} more bytes.";
    }

    [RelayCommand]
    private void PatchSubgroups()
    {
        if (Ctx == null || !CanPatch) return;
        var rom = Ctx.Rom;
        string expand = rom.Length < MapWriter.ExpandedSize ? "\n\nThe ROM will be expanded to 2 MB (the patch lives in the new space)." : "";
        if (System.Windows.MessageBox.Show(
                $"Allow up to {SubgroupPatch.MaxSubgroups} subgroups?\n\nThis changes a small piece of the game's battle code so it reads the subgroups from a bigger table, " +
                "and frees the old table's space for longer group lists. Battles work as before." + expand,
                "More subgroups", System.Windows.MessageBoxButton.YesNo) != System.Windows.MessageBoxResult.Yes) return;
        StashRows();
        var pending = new Dictionary<int, int[]>(_pending);
        bool ok = false;
        Commit("More-subgroups patch", () => { SubgroupPatch.Apply(rom); ok = true; });
        if (!ok) return;
        foreach (var (subgroup, monsters) in pending) _pending[subgroup] = monsters;
        int keepIndex = SelectedIndex;
        BuildEntries();
        SelectedIndex = -1; SelectedIndex = keepIndex;
        foreach (var (subgroup, monsters) in pending) _pending[subgroup] = monsters;
        Status = $"Patched: up to {SubgroupPatch.MaxSubgroups} subgroups and {Encounters.GroupRoom(rom)} bytes for group lists (Save Project keeps it).";
    }

    private void BuildSubgroupChoices()
    {
        SubgroupChoices.Clear();
        for (int subgroup = 1; subgroup <= Encounters.SubgroupCount(Ctx!.Rom); subgroup++)
        {
            var names = Monsters(subgroup).Where(m => m != 0xFF).Select(MonsterName).ToList();
            int users = _groups.Count(g => g.Contains(subgroup));
            SubgroupChoices.Add($"{subgroup:X2}  {(names.Count == 0 ? "(empty)" : string.Join(", ", names))}{(users == 0 ? "  - free" : "")}");
        }
    }

    private string MonsterName(int monster) => monster < MonsterChoices.Count - 1 ? MonsterChoices[monster][3..].Trim() : "";

    // a subgroup's monsters: the edits not applied yet if there are any, else the ROM's
    private int[] Monsters(int subgroup) => _pending.TryGetValue(subgroup, out var edited) ? edited : Encounters.SubgroupMonsters(Ctx!.Rom, subgroup);

    private string Summary(int group)
    {
        var maps = _mapLabels.Keys.Where(id => Encounters.MapGroups(Ctx!.Rom, id).Any(x => x.Group == group))
                             .Select(id => _mapLabels[id].Length > 4 ? _mapLabels[id][4..].Trim() : _mapLabels[id]).ToList();
        int cells = _zones.Count(z => z == group);
        if (cells > 0) maps.Insert(0, "world map");
        return maps.Count == 0 ? "(not used by any map)" : string.Join(", ", maps.Take(3)) + (maps.Count > 3 ? $" +{maps.Count - 3}" : "");
    }

    private void BuildMapGroups()
    {
        MapGroups.Clear();
        foreach (var (id, label) in _mapLabels.OrderBy(kv => kv.Key))
            foreach (var mapGroup in Encounters.MapGroups(Ctx!.Rom, id))
                MapGroups.Add(new MapGroupRow { Offset = mapGroup.Offset, LoadedGroup = mapGroup.Group, Map = label, Conditional = mapGroup.Conditional, Group = mapGroup.Group.ToString("X2"), Monsters = VisibleMonsters.Monsters(Ctx.Rom, id).Count > 0 });
    }

    partial void OnSelectedIndexChanged(int value)
    {
        if (Holding) return;   // the group's edits couldn't be written, so it stays picked with them
        _pending.Clear();
        LoadSelected();
        OnPropertyChanged(nameof(SelectedGroup));
        DrawZones();
    }

    protected override void LoadRecord()
    {
        Rows.Clear();
        if (Ctx == null || SelectedIndex < 0 || SelectedIndex >= _entryNumbers.Count) return;
        int number = _entryNumbers[SelectedIndex];
        IsGroup = number < Encounters.FirstBoss;
        if (IsGroup)
        {
            _subgroupsTextFromCode = true;
            SubgroupsText = string.Join(" ", _groups[number - 1].Select(s => s.ToString("X2")));
            _subgroupsTextFromCode = false;
            ShowSubgroups(_groups[number - 1]);
            var maps = _mapLabels.Where(kv => Encounters.MapGroups(Ctx.Rom, kv.Key).Any(x => x.Group == number)).Select(kv => kv.Value).ToList();
            int cells = _zones.Count(z => z == number);
            WhereText = (cells > 0 ? $"World map: {cells} zones of 4x4 tiles (see the World map page). " : "") +
                        (maps.Count > 0 ? "Maps: " + string.Join(", ", maps) : cells > 0 ? "" : "No map uses this group yet: paint it on the world map, or give it to a map on the right.");
        }
        else
        {
            SubgroupsText = "";
            var monsters = Encounters.BossMonsters(Ctx.Rom, number);
            Rows.Add(Row(number, monsters, "Scripted battle (event command 1F)", 0, ""));
            WhereText = "Used by event command 1F (Start a battle) with this formation.";
        }
        UpdateRoom();
    }

    private SubgroupRow Row(int number, int[] monsters, string usedBy, int times, string chance) => new()
    {
        Number = number, UsedBy = usedBy, Times = times, Chance = chance,
        M1 = Index(monsters[0]), M2 = Index(monsters[1]), M3 = Index(monsters[2]), M4 = Index(monsters[3]),
    };

    private int Index(int monster) => monster < MonsterChoices.Count - 1 ? monster : MonsterChoices.Count - 1;
    private int Monster(int index) => index >= 0 && index < MonsterChoices.Count - 1 ? index : 0xFF;
    private int[] RowMonsters(SubgroupRow row) => new[] { Monster(row.M1), Monster(row.M2), Monster(row.M3), Monster(row.M4) };

    // keeps the monsters picked in the rows on screen before the rows get rebuilt
    private void StashRows()
    {
        if (!IsGroup) return;
        foreach (var row in Rows)
        {
            var monsters = RowMonsters(row);
            if (!monsters.SequenceEqual(Encounters.SubgroupMonsters(Ctx!.Rom, row.Number))) _pending[row.Number] = monsters;
            else _pending.Remove(row.Number);
        }
    }

    private void ShowSubgroups(List<int> subgroups)
    {
        Rows.Clear();
        int total = subgroups.Count;
        foreach (int subgroup in subgroups.Distinct())
        {
            if (subgroup < 1 || subgroup > Encounters.SubgroupCount(Ctx!.Rom)) continue;
            int times = subgroups.Count(x => x == subgroup);
            var users = _groups.Select((g, i) => (g, i)).Where(x => x.g.Contains(subgroup) && x.i + 1 != SelectedGroup).Select(x => $"{x.i + 1:X2}").ToList();
            string shared = users.Count == 0 ? "only in this group" : "also in group" + (users.Count > 1 ? "s " : " ") + string.Join(" ", users) + " (changing its monsters changes them too)";
            string chance = $"Listed {times} time{(times == 1 ? "" : "s")} of {total}: {times * 100.0 / total:0.#}% of this group's battles";
            Rows.Add(Row(subgroup, Monsters(subgroup), shared, times, chance));
        }
    }

    private bool _subgroupsTextFromCode;

    partial void OnSubgroupsTextChanged(string value)
    {
        if (_subgroupsTextFromCode || !IsGroup || ParseSubgroups(value) is not { } subgroups) return;
        StashRows();
        ShowSubgroups(subgroups);
        UpdateRoom();
    }

    private void SetList(List<int> subgroups)
    {
        StashRows();
        _subgroupsTextFromCode = true;
        SubgroupsText = string.Join(" ", subgroups.Select(s => s.ToString("X2")));
        _subgroupsTextFromCode = false;
        ShowSubgroups(subgroups);
        UpdateRoom();
    }

    private List<int> CurrentList() => ParseSubgroups(SubgroupsText) ?? (SelectedGroup > 0 ? _groups[SelectedGroup - 1].ToList() : new List<int>());

    private List<int>? ParseSubgroups(string text)
    {
        var list = new List<int>();
        foreach (var part in text.Split(new[] { ' ', ',' }, StringSplitOptions.RemoveEmptyEntries))
        {
            if (!int.TryParse(part, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out int subgroup) || subgroup < 1 || subgroup > Encounters.SubgroupCount(Ctx!.Rom)) return null;
            list.Add(subgroup);
        }
        return list.Count is >= 1 and <= 255 ? list : null;
    }

    // the groups the way they'd be written, with the edited list for the selected group
    private List<List<int>> GroupsWithEdit()
    {
        var groups = _groups.Select(g => g.ToList()).ToList();
        if (IsGroup && SelectedGroup > 0 && ParseSubgroups(SubgroupsText) is { } edited) groups[SelectedGroup - 1] = edited;
        return groups;
    }

    private void UpdateRoom()
    {
        if (Ctx == null) return;
        int used = Encounters.GroupBytes(GroupsWithEdit());
        int free = Encounters.GroupRoom(Ctx!.Rom) - used;
        RoomText = $"Each battle picks one entry of this list at random. Group lists use {used} of {Encounters.GroupRoom(Ctx!.Rom)} bytes" +
                   (free >= 0 ? $" ({free} free: each extra entry takes 1, a new group 4)." : $" - {-free} too many: remove some entries.");
    }

    // add / remove subgroups

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
        Status = $"Subgroup {row.Number:X2} removed from this group (written by itself).";
    }

    [RelayCommand]
    private void AddExisting()
    {
        if (!IsGroup || AddChoice < 0) { Status = "Pick a subgroup to add first."; return; }
        var list = CurrentList(); list.Add(AddChoice + 1); SetList(list);
        Status = $"Subgroup {AddChoice + 1:X2} added (written by itself).";
    }

    // a subgroup slot that no group lists (counting the edit on screen), or 0
    private int FreeSubgroup()
    {
        var used = GroupsWithEdit().SelectMany(g => g).ToHashSet();
        for (int subgroup = 1; subgroup <= Encounters.SubgroupCount(Ctx!.Rom); subgroup++) if (!used.Contains(subgroup)) return subgroup;
        return 0;
    }

    [RelayCommand]
    private void NewSubgroup()
    {
        if (!IsGroup || Ctx == null) return;
        int subgroup = FreeSubgroup();
        if (subgroup == 0)
        {
            Status = $"All {Encounters.SubgroupCount(Ctx!.Rom)} subgroups are listed by some group. Remove a subgroup from the groups that don't need it (it becomes free), or add an existing one instead.";
            return;
        }
        // start from the first monsters of this group so it isn't empty
        var list = CurrentList();
        _pending[subgroup] = Rows.Count > 0 ? RowMonsters(Rows[0]) : new[] { 0, 0xFF, 0xFF, 0xFF };
        list.Add(subgroup); SetList(list);
        Status = $"New subgroup {subgroup:X2} (a free slot) added with a copy of the first monsters: choose its monsters; it's written by itself (or click Apply in the top bar).";
    }

    // new / delete group

    [RelayCommand]
    private void NewGroup()
    {
        if (Ctx == null) return;
        var groups = GroupsWithEdit();
        int first = IsGroup && SelectedGroup > 0 ? groups[SelectedGroup - 1][0] : 1;
        groups.Add(new List<int> { first });
        if (groups.Count > Encounters.MaxGroups) { Status = $"The game allows at most {Encounters.MaxGroups:X2} groups."; return; }
        if (Encounters.GroupBytes(groups) > Encounters.GroupRoom(Ctx!.Rom))
        {
            Status = $"No room for a new group: it needs 4 bytes and {Encounters.GroupRoom(Ctx!.Rom) - Encounters.GroupBytes(GroupsWithEdit())} are free. Remove a duplicate entry from some group first.";
            return;
        }
        int number = groups.Count;
        // every edited subgroup (also the other groups' ones kept from before) needs a monster, the game hangs on one without
        foreach (var (subgroup, monsters) in _pending)
            if (!Encounters.HasMonster(monsters)) { Refuse($"Subgroup {subgroup:X2} has no monsters: the game would hang looking for one. Pick at least one."); return; }
        bool ok = false;
        Commit($"New encounter group {number:X2}", () =>
        {
            Encounters.WriteGroups(Ctx.Rom, groups);
            foreach (var (subgroup, monsters) in _pending) Encounters.SetSubgroupMonsters(Ctx.Rom, subgroup, monsters);
            _groups = groups;
            ok = true;
        });
        if (!ok) return;
        _pending.Clear();
        BuildEntries();
        SelectedIndex = number - 1;
        Status = $"Group {number:X2} added (with subgroup {first:X2}); saved in the project. Add its subgroups, then paint it on the world map or give it to a map.";
    }

    [RelayCommand]
    private void DeleteGroup()
    {
        if (Ctx == null || !IsGroup) return;
        int group = SelectedGroup;
        if (group != Count) { Status = $"Only the last group ({Count:X2}) can be deleted: deleting another would renumber the groups after it, which maps and zones refer to by number."; return; }
        if (group == 1) { Status = "There must be at least one group."; return; }
        if (_zones.Any(z => z == group) || _mapLabels.Keys.Any(id => Encounters.MapGroups(Ctx.Rom, id).Any(x => x.Group == group)) || MapGroups.Any(r => r.Group.Trim().ToUpperInvariant() == group.ToString("X2")))
        { Status = $"Group {group:X2} is still used by the world map or a map; give those another group first."; return; }
        var groups = _groups.Take(group - 1).Select(x => x.ToList()).ToList();
        bool ok = false;
        Commit($"Delete encounter group {group:X2}", () => { Encounters.WriteGroups(Ctx.Rom, groups); _groups = groups; ok = true; });
        if (!ok) return;
        _pending.Clear();
        BuildEntries();
        SelectedIndex = group - 2;
    }

    [RelayCommand]
    private void Apply()
    {
        if (Ctx == null || SelectedIndex < 0) return;
        var rom = Ctx.Rom;
        int number = _entryNumbers[SelectedIndex];
        if (IsGroup && ParseSubgroups(SubgroupsText) == null)
        {
            Refuse($"Subgroups: hex numbers 01-{Encounters.SubgroupCount(Ctx!.Rom):X2} separated by spaces.");
            return;
        }
        StashRows();
        foreach (var row in Rows)
            if (!Encounters.HasMonster(RowMonsters(row)))
            { Refuse($"{row.Title} has no monsters: the game would hang looking for one. Pick at least one."); return; }
        // the edits kept from subgroups that aren't showing any more get written too, so same check for them
        if (IsGroup)
            foreach (var (subgroup, monsters) in _pending)
                if (!Encounters.HasMonster(monsters)) { Refuse($"Subgroup {subgroup:X2} has no monsters: the game would hang looking for one. Pick at least one."); return; }
        var mapEdits = new List<(int Offset, int Group)>();
        foreach (var row in MapGroups)
        {
            if (!int.TryParse(row.Group, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out int group) || group < 1 || group > Count)
            { Refuse($"{row.Map}: the group must be 01-{Count:X2}."); return; }
            if (group == row.LoadedGroup) continue;   // only the changed ones
            // the setup script may have moved since the rows were read (another page changed it), never write there then
            if (rom.ReadByte(row.Offset - 1) != 0x0D || rom.ReadByte(row.Offset) != row.LoadedGroup)
            { Refuse($"{row.Map}'s setup script changed since this list was read: open the tab again, then change its group."); return; }
            mapEdits.Add((row.Offset, group));
        }
        bool written;
        try
        {
            written = Commit(IsGroup ? $"Encounter group {number:X2}" : $"Boss formation {number:X2}", () =>
            {
                if (IsGroup)
                {
                    var groups = GroupsWithEdit();
                    Encounters.WriteGroups(rom, groups);
                    _groups = groups;
                    foreach (var (subgroup, monsters) in _pending) Encounters.SetSubgroupMonsters(rom, subgroup, monsters);
                }
                else
                {
                    Encounters.SetBossMonsters(rom, number, RowMonsters(Rows[0]));
                }
                foreach (var (offset, group) in mapEdits) rom.WriteByte(offset, (byte)group);
            });
        }
        catch (InvalidOperationException ex) { Refuse(ex.Message); return; }
        if (!written) return;   // the edits stay, with the error
        int keepIndex = SelectedIndex;
        _pending.Clear();
        BuildEntries();
        SelectedIndex = -1; SelectedIndex = keepIndex;
    }
}
