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

// a monster to pick: its number, name and battle picture (drawn the first time it's shown)
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

// one monster of the group being edited
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

// one of a zone's 5 group slots
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

// a dungeon map's field monster and the group it fights
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

// Lufia II encounters in plain terms: the world map's battle zones (where they are, which monster groups come up
// there and how often, L2Encounters.Zone), the dungeons' field monsters (which group each one fights) and the monster
// groups themselves (up to 8 monsters that fight together). each monster is shown with its picture and picked from a
// list. changes go straight into the ROM (Edit > Undo takes them back)
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
    // 0 = a zone is shown in the middle, 1 = a dungeon map
    [ObservableProperty] private int _middleMode;
    [ObservableProperty] private int _listTab;

    // the zone
    [ObservableProperty] private string _zoneTitle = "";
    [ObservableProperty] private string _zoneText = "";
    [ObservableProperty] private BitmapSource? _zoneMapImage;
    // the zone's groups in use (chance above 0), _allSlots holds all 5
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
    // the squares of the battle sprite buffer the group's battles need (L2BattleRoom)
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
            try { var (width, height, pixels) = L2WorldMap.Render(rom, 8); _worldSmall = Bitmap(width, height, pixels); } catch { _worldSmall = null; }
        RefreshLists();
        _mapIds.Clear(); MapNames.Clear();
        for (int map = 0; map < L2Maps.Count(rom); map++)
        {
            int monsterCount = L2Encounters.MapList(rom, map).Length;
            if (monsterCount == 0) continue;
            _mapIds.Add(map);
            string name = L2Maps.Name(rom, map);
            MapNames.Add($"{map:X2}  {(name.Length > 0 && !name.Contains('~') ? name : "Map " + map.ToString("X2"))}  ({monsterCount} monsters)");
        }
        SelectedGroup = -1; SelectedGroup = 0;
        SelectedZone = -1; SelectedZone = 1;
        InitStory();
        _readVersion = rom.Version;
    }

    private bool _keepWorld;
    // the ROM's version when the page read it (or last wrote it itself)
    private int _readVersion = -1;

    // the page is shown: if the ROM changed since we read it (monsters renamed or added, zones painted on the
    // Maps tab, Undo), everything gets read again, keeping the picks
    public void Shown()
    {
        if (_rom == null || _main == null || _rom.Version == _readVersion) return;
        int group = SelectedGroup, zone = SelectedZone, story = SelectedStory, row = SelectedMapRow, mode = MiddleMode;
        SetRom(_rom, _main);
        // (a zone picks its first group, so the zone or map goes first, then the group that was shown)
        SelectedZone = -1; SelectedZone = zone;
        if (mode == 1 && row >= 0) { SelectedMapRow = -1; SelectedMapRow = row; }
        SelectedGroup = -1; SelectedGroup = group;
        SelectedStory = -1; SelectedStory = story;
    }

    // after monsters were added or renamed on the Monsters page, or their sprites changed (Monsters or Graphics
    // page): the lists offer them with their pictures and sprite room (the picks are kept)
    public void RefreshMonsters()
    {
        if (_rom == null || _main == null) return;
        int group = SelectedGroup, zone = SelectedZone, story = SelectedStory;
        _keepWorld = _worldSmall != null;
        try { SetRom(_rom, _main); } finally { _keepWorld = false; }
        SelectedGroup = -1; SelectedGroup = group;
        SelectedZone = -1; SelectedZone = zone;
        SelectedStory = -1; SelectedStory = story;
    }

    private static BitmapSource Bitmap(int width, int height, uint[] pixels)
    {
        var bitmap = BitmapSource.Create(width, height, 96, 96, PixelFormats.Bgra32, null, pixels.Select(c => (int)c).ToArray(), width * 4);
        bitmap.Freeze();
        return bitmap;
    }

    private static BitmapSource? Picture(RomBuffer rom, L2Battle.Monster monster)
    {
        try { return L2Graphics.MonsterSprite(rom, monster.Sprite, monster.Palette) is var (width, height, pixels) && width > 0 ? Bitmap(width, height, pixels) : null; }
        catch { return null; }
    }

    private string MonsterName(int id) => id < _monsterList.Count ? _monsterList[id].Name.Trim() : $"monster {id:X2}";

    private L2BattleRoom.Sizes? _sizes;
    private L2BattleRoom.Sizes Sizes => _sizes ??= new L2BattleRoom.Sizes(_rom!);

    private string GroupLabel(int group)
    {
        var all = L2Encounters.Group(_rom!, group);
        var monsters = all.Where(m => m != 0xFF).ToList();
        var room = monsters.Count == 0 ? null : L2BattleRoom.Group(all, Sizes);
        return $"{group:X2}  " + (monsters.Count == 0 ? "(empty)" : string.Join(", ", monsters.GroupBy(m => m).Select(x => MonsterName(x.Key) + (x.Count() > 1 ? $" x{x.Count()}" : "")))) +
               (room is { TooBig: true, Fixed: false } ? $"  (! {room.Squares} squares)" : "");
    }

    // the squares of the battle sprite buffer a group's battles need, in words
    private void ShowGroupRoom(int[] monsters)
    {
        var room = L2BattleRoom.Group(monsters, Sizes);
        GroupRoomWarn = room.TooBig && !room.Fixed;
        GroupRoom = room.Fixed ? "" : room.Monsters.Count == 0 ? "" :
            GroupRoomWarn
                ? $"Too big: a battle of this group can need {L2BattleRoom.Describe(room, _monsterList)}. All the kinds of monster in one battle share {L2BattleRoom.Squares} squares of 16 x 16 for their sprites; past that their pictures come out wrong. Use smaller sprites or fewer big kinds together."
                : $"Sprite room: a battle of this group needs at most {room.Squares} of the {L2BattleRoom.Squares} squares of 16 x 16 the kinds of monster share (with {string.Join(", ", room.Monsters.Distinct().Select(MonsterName))}).";
    }

    private List<L2MonsterChoice> GroupPictures(int group) =>
        L2Encounters.Group(_rom!, group).Where(m => m != 0xFF && m < MonsterChoices.Count).Select(m => MonsterChoices[m]).ToList();

    private void RefreshLists()
    {
        if (_rom == null) return;
        _loading = true;
        int keptGroup = SelectedGroup, keptZone = SelectedZone;
        GroupNames.Clear();
        for (int group = 0; group < L2Encounters.GroupCount; group++) GroupNames.Add(GroupLabel(group));
        _groupOptions = Enumerable.Range(0, L2Encounters.GroupCount).Select(g => new L2Option(g, GroupLabel(g))).ToList();
        ZoneNames.Clear();
        var sizes = ZoneSizes();
        for (int zone = 0; zone < L2Encounters.ZoneCountIn(_rom); zone++)
        {
            var groups = L2Encounters.ZoneGroups(_rom, zone);
            ZoneNames.Add($"Zone {zone:X2}  " + (groups.Count == 0 ? "no battles" : $"{groups.Count} group{(groups.Count > 1 ? "s" : "")}") + (sizes[zone] == 0 ? ", not on the map" : ""));
        }
        SelectedGroup = keptGroup; SelectedZone = keptZone;
        _loading = false;
    }

    private byte[] ZoneMap() { try { return L2Rom.ReadFile(_rom!, L2Encounters.ZoneFile); } catch { return new byte[4096]; } }
    private int[] ZoneSizes() => L2Encounters.ZoneSizes(ZoneMap());

    // shows a zone (from the Maps tab's "Monsters..." button)
    public void ShowZone(int zone)
    {
        RefreshLists();   // (zones may have been added on the Maps tab)
        ListTab = 0;
        SelectedZone = -1; SelectedZone = zone;
    }

    private bool Locked()
    {
        if (_rom != null && TableGuard.LockReason(_rom, "Encounters") is { } why) { Status = why; return true; }
        return false;
    }

    // zones

    partial void OnSelectedZoneChanged(int value)
    {
        if (_rom == null || value < 0 || _loading) return;
        MiddleMode = 0;
        ShowZoneDetails(value);
        var first = L2Encounters.ZoneGroups(_rom, value).FirstOrDefault();
        if (first.Weight > 0) SelectedGroup = first.Group;
    }

    private void ShowZoneDetails(int zone)
    {
        var slots = L2Encounters.Zone(_rom!, zone);
        var zoneMap = ZoneMap();
        int cells = zoneMap.Count(b => b == zone);
        ZoneTitle = $"World map zone {zone:X2}";
        var near = new SortedSet<string>();
        foreach (var exit in L2MapInfo.Read(_rom!, 0).Where(e => e.Kind == "Exit" && e.DestMap >= 2))
        {
            int zoneX = exit.X1 / 4, zoneY = exit.Y1 / 4;
            bool hit = false;
            for (int dy = -3; dy <= 3 && !hit; dy++)
                for (int dx = -3; dx <= 3 && !hit; dx++)
                {
                    int x = zoneX + dx, y = zoneY + dy;
                    if (x >= 0 && y >= 0 && x < 64 && y < 64 && zoneMap[y * 64 + x] == zone) hit = true;
                }
            if (hit) { string name = L2Maps.Name(_rom!, exit.DestMap); if (name.Length > 0 && !name.Contains('~')) near.Add(name); }
        }
        ZoneText = zone == 0 ? "Zone 00 has no battles: paint it where the party should walk in peace." :
            (cells == 0 ? "No part of the world map is painted with this zone (paint it in Maps > World map > Battle zones). " : $"Painted on {cells} squares of the world map (the light area below)" + (near.Count > 0 ? $", near {string.Join(", ", near.Take(8))}. " : ". ")) +
            "Each battle here is one of the groups below, picked by its chance; how often battles come is the same everywhere (Settings).";
        ZoneMapImage = ZonePicture(zoneMap, zone);
        double totalWeight = slots.Sum(s => s.Weight);
        _allSlots = slots.Select((s, i) => new L2ZoneSlotVm
        {
            Index = i, Groups = _groupOptions, Group = s.Group, Weight = s.Weight,
            ShareText = s.Weight == 0 ? "not used (chance 0)" : $"{s.Weight / totalWeight * 100:0}% of the battles here",
            Monsters = s.Weight == 0 ? new() : GroupPictures(s.Group),
            Changed = ZoneEdited,
        }).ToList();
        ZoneSlots = _allSlots.Where(s => s.Weight > 0).ToList();
    }

    private BitmapSource? ZonePicture(byte[] zoneMap, int zone)
    {
        if (_worldSmall == null) return null;
        // the world map at 1/8 size, darkened outside the zone
        var visual = new DrawingVisual();
        using (var dc = visual.RenderOpen())
        {
            dc.DrawImage(_worldSmall, new System.Windows.Rect(0, 0, 512, 512));
            var shade = new SolidColorBrush(Color.FromArgb(0x80, 0, 0, 0)); shade.Freeze();
            uint colour = L2MapsPage.ZoneColour(zone);
            var mark = new SolidColorBrush(Color.FromArgb(0x60, (byte)(colour >> 16), (byte)(colour >> 8), (byte)colour)); mark.Freeze();
            var rim = new System.Windows.Media.Pen(System.Windows.Media.Brushes.White, 1); rim.Freeze();
            for (int y = 0; y < 64; y++)
                for (int x = 0; x < 64; x++)
                {
                    bool inZone = zoneMap[y * 64 + x] == zone;
                    dc.DrawRectangle(inZone ? mark : shade, null, new System.Windows.Rect(x * 8, y * 8, 8, 8));
                    // a white edge round the zone
                    if (inZone && (x == 0 || zoneMap[y * 64 + x - 1] != zone)) dc.DrawLine(rim, new System.Windows.Point(x * 8, y * 8), new System.Windows.Point(x * 8, y * 8 + 8));
                    if (inZone && (x == 63 || zoneMap[y * 64 + x + 1] != zone)) dc.DrawLine(rim, new System.Windows.Point(x * 8 + 8, y * 8), new System.Windows.Point(x * 8 + 8, y * 8 + 8));
                    if (inZone && (y == 0 || zoneMap[(y - 1) * 64 + x] != zone)) dc.DrawLine(rim, new System.Windows.Point(x * 8, y * 8), new System.Windows.Point(x * 8 + 8, y * 8));
                    if (inZone && (y == 63 || zoneMap[(y + 1) * 64 + x] != zone)) dc.DrawLine(rim, new System.Windows.Point(x * 8, y * 8 + 8), new System.Windows.Point(x * 8 + 8, y * 8 + 8));
                }
        }
        var bitmap = new RenderTargetBitmap(512, 512, 96, 96, PixelFormats.Pbgra32);
        bitmap.Render(visual); bitmap.Freeze();
        return bitmap;
    }

    private void ZoneEdited()
    {
        if (_rom == null || _main == null || SelectedZone < 0 || _loading) return;
        if (Locked()) return;
        var slots = _allSlots.Select(s => new L2Encounters.ZoneSlot(s.Group, s.Weight)).ToList();
        L2Encounters.WriteZone(_rom, SelectedZone, slots);
        _main.NotifyRomModified($"Lufia II: battle zone {SelectedZone:X2}");
        _readVersion = _rom.Version;
        int zone = SelectedZone;
        Status = $"Zone {zone:X2} written.";
        System.Windows.Application.Current?.Dispatcher.BeginInvoke(() => { RefreshLists(); ShowZoneDetails(zone); });
    }

    // adds a world map battle zone (the game has 17, the zone table moves to the added space the first time)
    [RelayCommand]
    private void AddZone()
    {
        if (_rom == null || _main == null || Locked()) return;
        try
        {
            int zone = L2Encounters.AddZone(_rom);
            _main.NotifyRomModified($"Lufia II: battle zone {zone:X2} added");
            _readVersion = _rom.Version;
            RefreshLists();
            ListTab = 0; SelectedZone = -1; SelectedZone = zone;
            Status = $"Zone {zone:X2} added: give it groups here (Add group), then paint it on the world map (Maps > World map > Battle zones).";
        }
        catch (Exception ex) { Status = "No zone added: " + ex.Message; }
    }

    [RelayCommand]
    private void EditSlotGroup(L2ZoneSlotVm? slot) { if (slot != null) SelectedGroup = slot.Group; }

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
    private void RemoveZoneGroup(L2ZoneSlotVm? slot) { if (slot != null) slot.Weight = 0; }

    // dungeon maps

    partial void OnSelectedMapRowChanged(int value)
    {
        if (_rom == null || value < 0 || value >= _mapIds.Count) return;
        MiddleMode = 1;
        ShowMapDetails(_mapIds[value]);
    }

    private void ShowMapDetails(int map)
    {
        var (listOffset, length) = L2Encounters.MapList(_rom!, map);
        var shared = L2Encounters.SharingWith(_rom!, map);
        string name = L2Maps.Name(_rom!, map);
        MapTitle = $"{map:X2}  {(name.Length > 0 && !name.Contains('~') ? name : "Map " + map.ToString("X2"))}";
        MapText = "In towns and dungeons the monsters walk on the map; touching one starts a battle with its group. These are the map's monsters in the order the map places them." +
                  (shared.Count > 0 ? $" Maps {string.Join(", ", shared.Select(m => m.ToString("X2")))} use the same list: a change here changes them too." : "");
        FieldMonsters = Enumerable.Range(0, length).Select(i =>
        {
            int group = _rom!.ReadByte(listOffset + i);
            return new L2FieldMonsterVm { Index = i, Label = $"Monster {i + 1}", Groups = _groupOptions, Group = group, Monsters = GroupPictures(group), Changed = FieldEdited };
        }).ToList();
    }

    private void FieldEdited(L2FieldMonsterVm fieldMonster)
    {
        if (_rom == null || _main == null || SelectedMapRow < 0 || _loading) return;
        if (Locked()) return;
        int map = _mapIds[SelectedMapRow];
        var (listOffset, _) = L2Encounters.MapList(_rom, map);
        _rom.WriteByte(listOffset + fieldMonster.Index, (byte)fieldMonster.Group);
        _main.NotifyRomModified($"Lufia II: monsters of map {map:X2}");
        _readVersion = _rom.Version;
        fieldMonster.Monsters = GroupPictures(fieldMonster.Group);
        SelectedGroup = fieldMonster.Group;
    }

    [RelayCommand]
    private void EditFieldGroup(L2FieldMonsterVm? fieldMonster) { if (fieldMonster != null) SelectedGroup = fieldMonster.Group; }

    // the group

    partial void OnSelectedGroupChanged(int value)
    {
        if (_rom == null || value < 0) return;
        ShowGroup(value);
    }

    private void ShowGroup(int group)
    {
        var monsters = L2Encounters.Group(_rom!, group);
        GroupTitle = $"Monster group {group:X2}";
        var (zones, maps) = L2Encounters.UsersOf(_rom!, group);
        var parts = new List<string>();
        if (zones.Count > 0) parts.Add("world map " + string.Join(", ", zones.Select(z => $"zone {z.Zone:X2} ({z.Share * 100:0}% of its battles)")));
        if (maps.Count > 0) parts.Add("the monsters of " + string.Join(", ", maps.Take(10).Select(m => { string name = L2Maps.Name(_rom!, m); return $"{m:X2} {(name.Length > 0 && !name.Contains('~') ? name : "")}".Trim(); })) + (maps.Count > 10 ? $" and {maps.Count - 10} more maps" : ""));
        GroupUsers = parts.Count == 0 ? "No zone or map uses this group (scripted battles may)." : "Fought in " + string.Join("; ", parts) + ".";
        ShowGroupRoom(monsters);
        GroupNote = L2Encounters.IsFixed(monsters)
            ? "A fixed battle: with place 1 empty the game takes the monster in place 2 and reads the other places as its own layout data, not monsters. Change only place 2 here."
            : "";
        var list = new ObservableCollection<L2GroupMonster>();
        for (int i = 0; i < monsters.Length; i++)
        {
            if (monsters[i] == 0xFF || L2Encounters.IsFixed(monsters) && i != 1) continue;
            var monster = monsters[i] < _monsterList.Count ? _monsterList[monsters[i]] : null;
            list.Add(new L2GroupMonster
            {
                Slot = i, Choices = MonsterChoices, Monster = monsters[i] < MonsterChoices.Count ? MonsterChoices[monsters[i]] : null,
                Detail = monster == null ? "" : $"level {monster.Level}, HP {monster.Hp}, {monster.Exp} EXP, {monster.Gold} gold", Changed = GroupMonsterEdited,
            });
        }
        GroupMonsters = list;
    }

    private void WriteGroup(int[] monsters, string what)
    {
        if (_rom == null || _main == null || SelectedGroup < 0) return;
        if (Locked()) return;
        L2Encounters.WriteGroup(_rom, SelectedGroup, monsters);
        _main.NotifyRomModified($"Lufia II: monster group {SelectedGroup:X2}");
        _readVersion = _rom.Version;
        int group = SelectedGroup;
        Status = what;
        System.Windows.Application.Current?.Dispatcher.BeginInvoke(() =>
        {
            RefreshLists(); ShowGroup(group);
            if (MiddleMode == 0 && SelectedZone >= 0) ShowZoneDetails(SelectedZone);
            else if (SelectedMapRow >= 0 && SelectedMapRow < _mapIds.Count) ShowMapDetails(_mapIds[SelectedMapRow]);
        });
    }

    private void GroupMonsterEdited(L2GroupMonster groupMonster)
    {
        if (_rom == null || groupMonster.Monster == null) return;
        var monsters = L2Encounters.Group(_rom, SelectedGroup);
        if (monsters[groupMonster.Slot] == groupMonster.Monster.Id) return;
        if (L2Encounters.IsFixed(monsters) && groupMonster.Slot != 1) { Status = "This is a fixed battle: only the monster in place 2 can change."; ShowGroup(SelectedGroup); return; }
        monsters[groupMonster.Slot] = groupMonster.Monster.Id;
        WriteGroup(monsters, $"Group {SelectedGroup:X2}: {MonsterName(groupMonster.Monster.Id)} in place {groupMonster.Slot + 1}.");
    }

    [RelayCommand]
    private void RemoveMonster(L2GroupMonster? groupMonster)
    {
        if (_rom == null || groupMonster == null) return;
        var monsters = L2Encounters.Group(_rom, SelectedGroup);
        if (L2Encounters.IsFixed(monsters)) { Status = "This is a fixed battle: only the monster in place 2 can change."; return; }
        if (monsters.Count(m => m != 0xFF) <= 1) { Status = "A group needs at least one monster."; return; }
        monsters[groupMonster.Slot] = 0xFF;
        // place 1 empty with place 2 filled would make it a fixed battle, so place 2's monster moves up
        if (monsters[0] == 0xFF && monsters[1] != 0xFF) { monsters[0] = monsters[1]; monsters[1] = 0xFF; }
        WriteGroup(monsters, $"Took {MonsterName(groupMonster.Monster?.Id ?? 0)} out of group {SelectedGroup:X2}.");
    }

    [RelayCommand]
    private void AddMonster()
    {
        if (_rom == null || SelectedGroup < 0) return;
        if (AddChoice == null) { Status = "Pick the monster to add in the list first."; return; }
        var monsters = L2Encounters.Group(_rom, SelectedGroup);
        if (L2Encounters.IsFixed(monsters)) { Status = "This is a fixed battle: only the monster in place 2 can change."; return; }
        int freePlace = Array.IndexOf(monsters, 0xFF);
        if (freePlace < 0) { Status = "A group holds at most 8 monsters."; return; }
        monsters[freePlace] = AddChoice.Id;
        WriteGroup(monsters, $"Added {MonsterName(AddChoice.Id)} to group {SelectedGroup:X2}.");
    }
}
