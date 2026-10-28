using System.Collections.ObjectModel;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using LufiaForge.Core;
using LufiaForge.Core.Lufia2;
using LufiaForge.ViewModels;
using Color = System.Windows.Media.Color;

namespace LufiaForge.Modules.Lufia2;

/// <summary>A monster to pick: its number, name and battle picture (drawn when first shown).</summary>
public sealed class L2MonsterChoice
{
    private readonly Func<BitmapSource?>? _draw;
    private BitmapSource? _picture;
    private bool _drawn;
    public L2MonsterChoice(int id, string label, Func<BitmapSource?>? draw) { Id = id; Label = label; _draw = draw; }
    public int Id { get; }
    public string Label { get; }
    public BitmapSource? Picture { get { if (!_drawn) { _drawn = true; _picture = _draw?.Invoke(); } return _picture; } }
    public override string ToString() => Label;
}

/// <summary>One monster of the group being edited.</summary>
public partial class L2GroupMonster : ObservableObject
{
    public int Slot { get; init; }
    public string Place => Slot < 2 ? $"Place {Slot + 1} (1 or 2 always comes)" : $"Place {Slot + 1}";
    public List<L2MonsterChoice> Choices { get; init; } = new();
    [ObservableProperty] private L2MonsterChoice? _monster;
    public string Detail { get; init; } = "";
    public Action<L2GroupMonster>? Changed { get; set; }
    partial void OnMonsterChanged(L2MonsterChoice? value) => Changed?.Invoke(this);
}

/// <summary>One of a zone's 5 group slots.</summary>
public partial class L2ZoneSlotVm : ObservableObject
{
    public int Index { get; init; }
    public List<L2Option> Groups { get; init; } = new();
    [ObservableProperty] private int _group;
    [ObservableProperty] private int _weight;
    [ObservableProperty] private string _shareText = "";
    [ObservableProperty] private List<L2MonsterChoice> _monsters = new();
    public Action? Changed { get; set; }
    partial void OnGroupChanged(int value) => Changed?.Invoke();
    partial void OnWeightChanged(int value) => Changed?.Invoke();
}

/// <summary>A dungeon map's field monster and the group it fights.</summary>
public partial class L2FieldMonsterVm : ObservableObject
{
    public int Index { get; init; }
    public string Label { get; init; } = "";
    public List<L2Option> Groups { get; init; } = new();
    [ObservableProperty] private int _group;
    [ObservableProperty] private List<L2MonsterChoice> _monsters = new();
    public Action<L2FieldMonsterVm>? Changed { get; set; }
    partial void OnGroupChanged(int value) => Changed?.Invoke(this);
}

/// <summary>
/// Lufia II encounters in plain terms: the world map's battle zones (where they are, which monster groups come up there
/// and how often, <see cref="L2Encounters.Zone"/>), the dungeons' field monsters (which group each one fights), and the
/// monster groups themselves (up to 8 monsters that fight together), each monster shown with its picture and picked
/// from a list. Changes go straight into the ROM (Edit &gt; Undo takes them back).
/// </summary>
public partial class L2EncountersPage : ObservableObject
{
    private RomBuffer? _rom;
    private MainViewModel? _main;
    private bool _loading;
    private List<L2Battle.Monster> _monsterList = new();
    private List<L2Option> _groupOptions = new();

    public List<L2MonsterChoice> MonsterChoices { get; private set; } = new();
    public ObservableCollection<string> ZoneNames { get; } = new();
    public ObservableCollection<string> MapNames { get; } = new();
    public ObservableCollection<string> GroupNames { get; } = new();
    private readonly List<int> _mapIds = new();

    [ObservableProperty] private int _selectedZone = -1;
    [ObservableProperty] private int _selectedMapRow = -1;
    [ObservableProperty] private int _selectedGroup = -1;
    /// <summary>0 = a zone is shown in the middle, 1 = a dungeon map.</summary>
    [ObservableProperty] private int _middleMode;
    [ObservableProperty] private int _listTab;

    // the zone
    [ObservableProperty] private string _zoneTitle = "";
    [ObservableProperty] private string _zoneText = "";
    [ObservableProperty] private BitmapSource? _zoneMapImage;
    /// <summary>The zone's groups in use (chance above 0); <see cref="_allSlots"/> holds all 5.</summary>
    [ObservableProperty] private List<L2ZoneSlotVm> _zoneSlots = new();
    private List<L2ZoneSlotVm> _allSlots = new();
    // the dungeon map
    [ObservableProperty] private string _mapTitle = "";
    [ObservableProperty] private string _mapText = "";
    [ObservableProperty] private List<L2FieldMonsterVm> _fieldMonsters = new();
    // the group
    [ObservableProperty] private string _groupTitle = "";
    [ObservableProperty] private string _groupUsers = "";
    [ObservableProperty] private string _groupNote = "";
    /// <summary>The squares of the battle sprite buffer the group's battles need (<see cref="L2BattleRoom"/>).</summary>
    [ObservableProperty] private string _groupRoom = "";
    [ObservableProperty] private bool _groupRoomWarn;
    [ObservableProperty] private ObservableCollection<L2GroupMonster> _groupMonsters = new();
    [ObservableProperty] private L2MonsterChoice? _addChoice;
    [ObservableProperty] private string _status = "";

    public bool IsZoneShown => MiddleMode == 0;
    public bool IsMapShown => MiddleMode == 1;
    partial void OnMiddleModeChanged(int value) { OnPropertyChanged(nameof(IsZoneShown)); OnPropertyChanged(nameof(IsMapShown)); }

    private BitmapSource? _worldSmall;

    public void SetRom(RomBuffer rom, MainViewModel main)
    {
        _rom = rom; _main = main;
        _monsterList = L2Battle.Monsters(rom);
        MonsterChoices = _monsterList.Select(m => new L2MonsterChoice(m.Index, $"{m.Index:X2}  {(m.Unused ? "(unused slot)" : m.Name.Trim())}  (level {m.Level})",
            () => Picture(rom, m))).ToList();
        OnPropertyChanged(nameof(MonsterChoices));
        _sizes = null;
        if (!_keepWorld)
            try { var (w, h, px) = L2WorldMap.Render(rom, 8); _worldSmall = Bitmap(w, h, px); } catch { _worldSmall = null; }
        RefreshLists();
        _mapIds.Clear(); MapNames.Clear();
        for (int m = 0; m < L2Maps.Count; m++)
        {
            int n = L2Encounters.MapList(rom, m).Length;
            if (n == 0) continue;
            _mapIds.Add(m);
            string name = L2Maps.Name(rom, m);
            MapNames.Add($"{m:X2}  {(name.Length > 0 && !name.Contains('~') ? name : "Map " + m.ToString("X2"))}  ({n} monsters)");
        }
        SelectedGroup = -1; SelectedGroup = 0;
        SelectedZone = -1; SelectedZone = 1;
        InitStory();
    }

    private bool _keepWorld;

    /// <summary>After monsters were added or renamed on the Monsters page, or their sprites changed (Monsters or
    /// Graphics page): the lists offer them, with their pictures and sprite room (the picks are kept).</summary>
    public void RefreshMonsters()
    {
        if (_rom == null || _main == null) return;
        int g = SelectedGroup, z = SelectedZone, st = SelectedStory;
        _keepWorld = _worldSmall != null;
        try { SetRom(_rom, _main); } finally { _keepWorld = false; }
        SelectedGroup = -1; SelectedGroup = g;
        SelectedZone = -1; SelectedZone = z;
        SelectedStory = -1; SelectedStory = st;
    }

    private static BitmapSource Bitmap(int w, int h, uint[] px)
    {
        var b = BitmapSource.Create(w, h, 96, 96, PixelFormats.Bgra32, null, px.Select(c => (int)c).ToArray(), w * 4);
        b.Freeze();
        return b;
    }

    private static BitmapSource? Picture(RomBuffer rom, L2Battle.Monster m)
    {
        try { return L2Graphics.MonsterSprite(rom, m.Sprite, m.Palette) is var (w, h, px) && w > 0 ? Bitmap(w, h, px) : null; }
        catch { return null; }
    }

    private string MonsterName(int id) => id < _monsterList.Count ? _monsterList[id].Name.Trim() : $"monster {id:X2}";

    private L2BattleRoom.Sizes? _sizes;
    private L2BattleRoom.Sizes Sizes => _sizes ??= new L2BattleRoom.Sizes(_rom!);

    private string GroupLabel(int g)
    {
        var all = L2Encounters.Group(_rom!, g);
        var ms = all.Where(m => m != 0xFF).ToList();
        var need = ms.Count == 0 ? null : L2BattleRoom.Group(all, Sizes);
        return $"{g:X2}  " + (ms.Count == 0 ? "(empty)" : string.Join(", ", ms.GroupBy(m => m).Select(x => MonsterName(x.Key) + (x.Count() > 1 ? $" x{x.Count()}" : "")))) +
               (need is { TooBig: true, Fixed: false } ? $"  (! {need.Squares} squares)" : "");
    }

    /// <summary>The squares of the battle sprite buffer a group's battles need, in words.</summary>
    private void ShowGroupRoom(int[] ms)
    {
        var n = L2BattleRoom.Group(ms, Sizes);
        GroupRoomWarn = n.TooBig && !n.Fixed;
        GroupRoom = n.Fixed ? "" : n.Monsters.Count == 0 ? "" :
            GroupRoomWarn
                ? $"Too big: a battle of this group can need {L2BattleRoom.Describe(n, _monsterList)}. All the kinds of monster in one battle share {L2BattleRoom.Squares} squares of 16 x 16 for their sprites; past that their pictures come out wrong. Use smaller sprites or fewer big kinds together."
                : $"Sprite room: a battle of this group needs at most {n.Squares} of the {L2BattleRoom.Squares} squares of 16 x 16 the kinds of monster share (with {string.Join(", ", n.Monsters.Distinct().Select(MonsterName))}).";
    }

    private List<L2MonsterChoice> GroupPictures(int g) =>
        L2Encounters.Group(_rom!, g).Where(m => m != 0xFF && m < MonsterChoices.Count).Select(m => MonsterChoices[m]).ToList();

    private void RefreshLists()
    {
        if (_rom == null) return;
        _loading = true;
        int keepG = SelectedGroup, keepZ = SelectedZone;
        GroupNames.Clear();
        for (int g = 0; g < L2Encounters.GroupCount; g++) GroupNames.Add(GroupLabel(g));
        _groupOptions = Enumerable.Range(0, L2Encounters.GroupCount).Select(g => new L2Option(g, GroupLabel(g))).ToList();
        ZoneNames.Clear();
        var sizes = ZoneSizes();
        for (int z = 0; z < L2Encounters.ZoneCountIn(_rom); z++)
        {
            var groups = L2Encounters.ZoneGroups(_rom, z);
            ZoneNames.Add($"Zone {z:X2}  " + (groups.Count == 0 ? "no battles" : $"{groups.Count} group{(groups.Count > 1 ? "s" : "")}") + (sizes[z] == 0 ? ", not on the map" : ""));
        }
        SelectedGroup = keepG; SelectedZone = keepZ;
        _loading = false;
    }

    private byte[] ZoneMap() { try { return L2Rom.ReadFile(_rom!, L2Encounters.ZoneFile); } catch { return new byte[4096]; } }
    private int[] ZoneSizes() => L2Encounters.ZoneSizes(ZoneMap());

    /// <summary>Shows zone <paramref name="z"/> (from the Maps tab's "Monsters..." button).</summary>
    public void ShowZone(int z)
    {
        RefreshLists();   // (zones may have been added on the Maps tab)
        ListTab = 0;
        SelectedZone = -1; SelectedZone = z;
    }

    private bool Locked()
    {
        if (_rom != null && TableGuard.LockReason(_rom, "Encounters") is { } why) { Status = why; return true; }
        return false;
    }

    // ---------------------------------------------------------------- zones

    partial void OnSelectedZoneChanged(int value)
    {
        if (_rom == null || value < 0 || _loading) return;
        MiddleMode = 0;
        ShowZoneDetails(value);
        var first = L2Encounters.ZoneGroups(_rom, value).FirstOrDefault();
        if (first.Weight > 0) SelectedGroup = first.Group;
    }

    private void ShowZoneDetails(int z)
    {
        var slots = L2Encounters.Zone(_rom!, z);
        var zm = ZoneMap();
        int cells = zm.Count(b => b == z);
        ZoneTitle = $"World map zone {z:X2}";
        var near = new SortedSet<string>();
        foreach (var e in L2MapInfo.Read(_rom!, 0).Where(e => e.Kind == "Exit" && e.DestMap >= 2))
        {
            int zx = e.X1 / 4, zy = e.Y1 / 4;
            bool hit = false;
            for (int dy = -3; dy <= 3 && !hit; dy++)
                for (int dx = -3; dx <= 3 && !hit; dx++)
                {
                    int x = zx + dx, y = zy + dy;
                    if (x >= 0 && y >= 0 && x < 64 && y < 64 && zm[y * 64 + x] == z) hit = true;
                }
            if (hit) { string n = L2Maps.Name(_rom!, e.DestMap); if (n.Length > 0 && !n.Contains('~')) near.Add(n); }
        }
        ZoneText = z == 0 ? "Zone 00 has no battles: paint it where the party should walk in peace." :
            (cells == 0 ? "No part of the world map is painted with this zone (paint it in Maps > World map > Battle zones). " : $"Painted on {cells} squares of the world map (the light area below)" + (near.Count > 0 ? $", near {string.Join(", ", near.Take(8))}. " : ". ")) +
            "Each battle here is one of the groups below, picked by its chance; how often battles come is the same everywhere (Settings).";
        ZoneMapImage = ZonePicture(zm, z);
        double sum = slots.Sum(s => s.Weight);
        _allSlots = slots.Select((s, i) => new L2ZoneSlotVm
        {
            Index = i, Groups = _groupOptions, Group = s.Group, Weight = s.Weight,
            ShareText = s.Weight == 0 ? "not used (chance 0)" : $"{s.Weight / sum * 100:0}% of the battles here",
            Monsters = s.Weight == 0 ? new() : GroupPictures(s.Group),
            Changed = ZoneEdited,
        }).ToList();
        ZoneSlots = _allSlots.Where(s => s.Weight > 0).ToList();
    }

    private BitmapSource? ZonePicture(byte[] zm, int z)
    {
        if (_worldSmall == null) return null;
        // the world map at 1/8 size, darkened outside the zone
        var dv = new DrawingVisual();
        using (var dc = dv.RenderOpen())
        {
            dc.DrawImage(_worldSmall, new System.Windows.Rect(0, 0, 512, 512));
            var shade = new SolidColorBrush(Color.FromArgb(0x80, 0, 0, 0)); shade.Freeze();
            uint c = L2MapsPage.ZoneColour(z);
            var mark = new SolidColorBrush(Color.FromArgb(0x60, (byte)(c >> 16), (byte)(c >> 8), (byte)c)); mark.Freeze();
            var rim = new System.Windows.Media.Pen(System.Windows.Media.Brushes.White, 1); rim.Freeze();
            for (int y = 0; y < 64; y++)
                for (int x = 0; x < 64; x++)
                {
                    bool inZone = zm[y * 64 + x] == z;
                    dc.DrawRectangle(inZone ? mark : shade, null, new System.Windows.Rect(x * 8, y * 8, 8, 8));
                    // a white edge round the zone
                    if (inZone && (x == 0 || zm[y * 64 + x - 1] != z)) dc.DrawLine(rim, new System.Windows.Point(x * 8, y * 8), new System.Windows.Point(x * 8, y * 8 + 8));
                    if (inZone && (x == 63 || zm[y * 64 + x + 1] != z)) dc.DrawLine(rim, new System.Windows.Point(x * 8 + 8, y * 8), new System.Windows.Point(x * 8 + 8, y * 8 + 8));
                    if (inZone && (y == 0 || zm[(y - 1) * 64 + x] != z)) dc.DrawLine(rim, new System.Windows.Point(x * 8, y * 8), new System.Windows.Point(x * 8 + 8, y * 8));
                    if (inZone && (y == 63 || zm[(y + 1) * 64 + x] != z)) dc.DrawLine(rim, new System.Windows.Point(x * 8, y * 8 + 8), new System.Windows.Point(x * 8 + 8, y * 8 + 8));
                }
        }
        var rtb = new RenderTargetBitmap(512, 512, 96, 96, PixelFormats.Pbgra32);
        rtb.Render(dv); rtb.Freeze();
        return rtb;
    }

    private void ZoneEdited()
    {
        if (_rom == null || _main == null || SelectedZone < 0 || _loading) return;
        if (Locked()) return;
        var slots = _allSlots.Select(s => new L2Encounters.ZoneSlot(s.Group, s.Weight)).ToList();
        L2Encounters.WriteZone(_rom, SelectedZone, slots);
        _main.NotifyRomModified($"Lufia II: battle zone {SelectedZone:X2}");
        int z = SelectedZone;
        Status = $"Zone {z:X2} written.";
        System.Windows.Application.Current?.Dispatcher.BeginInvoke(() => { RefreshLists(); ShowZoneDetails(z); });
    }

    /// <summary>Adds a world map battle zone (the game has 17; the zone table moves to the added space the first time).</summary>
    [RelayCommand]
    private void AddZone()
    {
        if (_rom == null || _main == null || Locked()) return;
        try
        {
            int z = L2Encounters.AddZone(_rom);
            _main.NotifyRomModified($"Lufia II: battle zone {z:X2} added");
            RefreshLists();
            ListTab = 0; SelectedZone = -1; SelectedZone = z;
            Status = $"Zone {z:X2} added: give it groups here (Add group), then paint it on the world map (Maps > World map > Battle zones).";
        }
        catch (Exception ex) { Status = "No zone added: " + ex.Message; }
    }

    [RelayCommand]
    private void EditSlotGroup(L2ZoneSlotVm? s) { if (s != null) SelectedGroup = s.Group; }

    [RelayCommand]
    private void AddZoneGroup()
    {
        if (_rom == null || SelectedZone <= 0) { Status = "Pick a zone (1 or up) first."; return; }
        var free = _allSlots.FirstOrDefault(s => s.Weight == 0);
        if (free == null) { Status = "A zone has at most 5 groups."; return; }
        _loading = true; free.Group = SelectedGroup < 0 ? 0 : SelectedGroup; _loading = false;
        free.Weight = 50;
    }

    [RelayCommand]
    private void RemoveZoneGroup(L2ZoneSlotVm? s) { if (s != null) s.Weight = 0; }

    // ---------------------------------------------------------------- dungeon maps

    partial void OnSelectedMapRowChanged(int value)
    {
        if (_rom == null || value < 0 || value >= _mapIds.Count) return;
        MiddleMode = 1;
        ShowMapDetails(_mapIds[value]);
    }

    private void ShowMapDetails(int map)
    {
        var (at, len) = L2Encounters.MapList(_rom!, map);
        var shared = L2Encounters.SharingWith(_rom!, map);
        string name = L2Maps.Name(_rom!, map);
        MapTitle = $"{map:X2}  {(name.Length > 0 && !name.Contains('~') ? name : "Map " + map.ToString("X2"))}";
        MapText = "In towns and dungeons the monsters walk on the map; touching one starts a battle with its group. These are the map's monsters in the order the map places them." +
                  (shared.Count > 0 ? $" Maps {string.Join(", ", shared.Select(m => m.ToString("X2")))} use the same list: a change here changes them too." : "");
        FieldMonsters = Enumerable.Range(0, len).Select(i =>
        {
            int g = _rom!.ReadByte(at + i);
            return new L2FieldMonsterVm { Index = i, Label = $"Monster {i + 1}", Groups = _groupOptions, Group = g, Monsters = GroupPictures(g), Changed = FieldEdited };
        }).ToList();
    }

    private void FieldEdited(L2FieldMonsterVm f)
    {
        if (_rom == null || _main == null || SelectedMapRow < 0 || _loading) return;
        if (Locked()) return;
        int map = _mapIds[SelectedMapRow];
        var (at, _) = L2Encounters.MapList(_rom, map);
        _rom.WriteByte(at + f.Index, (byte)f.Group);
        _main.NotifyRomModified($"Lufia II: monsters of map {map:X2}");
        f.Monsters = GroupPictures(f.Group);
        SelectedGroup = f.Group;
    }

    [RelayCommand]
    private void EditFieldGroup(L2FieldMonsterVm? f) { if (f != null) SelectedGroup = f.Group; }

    // ---------------------------------------------------------------- the group

    partial void OnSelectedGroupChanged(int value)
    {
        if (_rom == null || value < 0) return;
        ShowGroup(value);
    }

    private void ShowGroup(int g)
    {
        var ms = L2Encounters.Group(_rom!, g);
        GroupTitle = $"Monster group {g:X2}";
        var (zones, maps) = L2Encounters.UsersOf(_rom!, g);
        var parts = new List<string>();
        if (zones.Count > 0) parts.Add("world map " + string.Join(", ", zones.Select(z => $"zone {z.Zone:X2} ({z.Share * 100:0}% of its battles)")));
        if (maps.Count > 0) parts.Add("the monsters of " + string.Join(", ", maps.Take(10).Select(m => { string n = L2Maps.Name(_rom!, m); return $"{m:X2} {(n.Length > 0 && !n.Contains('~') ? n : "")}".Trim(); })) + (maps.Count > 10 ? $" and {maps.Count - 10} more maps" : ""));
        GroupUsers = parts.Count == 0 ? "No zone or map uses this group (scripted battles may)." : "Fought in " + string.Join("; ", parts) + ".";
        ShowGroupRoom(ms);
        GroupNote = L2Encounters.IsFixed(ms)
            ? "A fixed battle: with place 1 empty the game takes the monster in place 2 and reads the other places as its own layout data, not monsters. Change only place 2 here."
            : "";
        var list = new ObservableCollection<L2GroupMonster>();
        for (int i = 0; i < ms.Length; i++)
        {
            if (ms[i] == 0xFF || L2Encounters.IsFixed(ms) && i != 1) continue;
            var m = ms[i] < _monsterList.Count ? _monsterList[ms[i]] : null;
            list.Add(new L2GroupMonster
            {
                Slot = i, Choices = MonsterChoices, Monster = ms[i] < MonsterChoices.Count ? MonsterChoices[ms[i]] : null,
                Detail = m == null ? "" : $"level {m.Level}, HP {m.Hp}, {m.Exp} EXP, {m.Gold} gold", Changed = GroupMonsterEdited,
            });
        }
        GroupMonsters = list;
    }

    private void WriteGroup(int[] ms, string what)
    {
        if (_rom == null || _main == null || SelectedGroup < 0) return;
        if (Locked()) return;
        L2Encounters.WriteGroup(_rom, SelectedGroup, ms);
        _main.NotifyRomModified($"Lufia II: monster group {SelectedGroup:X2}");
        int g = SelectedGroup;
        Status = what;
        System.Windows.Application.Current?.Dispatcher.BeginInvoke(() =>
        {
            RefreshLists(); ShowGroup(g);
            if (MiddleMode == 0 && SelectedZone >= 0) ShowZoneDetails(SelectedZone);
            else if (SelectedMapRow >= 0 && SelectedMapRow < _mapIds.Count) ShowMapDetails(_mapIds[SelectedMapRow]);
        });
    }

    private void GroupMonsterEdited(L2GroupMonster gm)
    {
        if (_rom == null || gm.Monster == null) return;
        var ms = L2Encounters.Group(_rom, SelectedGroup);
        if (ms[gm.Slot] == gm.Monster.Id) return;
        if (L2Encounters.IsFixed(ms) && gm.Slot != 1) { Status = "This is a fixed battle: only the monster in place 2 can change."; ShowGroup(SelectedGroup); return; }
        ms[gm.Slot] = gm.Monster.Id;
        WriteGroup(ms, $"Group {SelectedGroup:X2}: {MonsterName(gm.Monster.Id)} in place {gm.Slot + 1}.");
    }

    [RelayCommand]
    private void RemoveMonster(L2GroupMonster? gm)
    {
        if (_rom == null || gm == null) return;
        var ms = L2Encounters.Group(_rom, SelectedGroup);
        if (L2Encounters.IsFixed(ms)) { Status = "This is a fixed battle: only the monster in place 2 can change."; return; }
        if (ms.Count(m => m != 0xFF) <= 1) { Status = "A group needs at least one monster."; return; }
        ms[gm.Slot] = 0xFF;
        // place 1 empty with place 2 filled would make it a fixed battle: place 2's monster moves up
        if (ms[0] == 0xFF && ms[1] != 0xFF) { ms[0] = ms[1]; ms[1] = 0xFF; }
        WriteGroup(ms, $"Took {MonsterName(gm.Monster?.Id ?? 0)} out of group {SelectedGroup:X2}.");
    }

    [RelayCommand]
    private void AddMonster()
    {
        if (_rom == null || SelectedGroup < 0) return;
        if (AddChoice == null) { Status = "Pick the monster to add in the list first."; return; }
        var ms = L2Encounters.Group(_rom, SelectedGroup);
        if (L2Encounters.IsFixed(ms)) { Status = "This is a fixed battle: only the monster in place 2 can change."; return; }
        int free = Array.IndexOf(ms, 0xFF);
        if (free < 0) { Status = "A group holds at most 8 monsters."; return; }
        ms[free] = AddChoice.Id;
        WriteGroup(ms, $"Added {MonsterName(AddChoice.Id)} to group {SelectedGroup:X2}.");
    }
}
