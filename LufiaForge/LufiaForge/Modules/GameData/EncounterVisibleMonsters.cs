using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using LufiaForge.Core.Battle;
using LufiaForge.Core.Maps;
using System.Collections.ObjectModel;
using System.Globalization;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace LufiaForge.Modules.GameData;

// a character sprite you can pick for a map's monsters
public sealed class MonsterSpriteChoice
{
    // the sprite's number in the ROM. -1 = a Lufia 2 monster that isn't in the ROM yet (gets added when placed)
    public int Number { get; init; }
    public ImageSource? Image { get; init; }
    // which Lufia 2 monster it is, if it's one
    public string? Lufia2Name { get; init; }
    // a Lufia 2 monster's sheet (8 frames side by side), for adding it to the ROM
    public (uint[] Px, int FrameWidth, int Height)? Sheet { get; init; }
    public string Tip => Lufia2Name == null ? $"Sprite {Number:X2}"
        : Number >= 0 ? $"Lufia 2: {Lufia2Name} (sprite {Number:X2})" : $"Lufia 2: {Lufia2Name} (added to the ROM when you place it)";
}

// visible monsters (VisibleMonsters): on a town, cave or tower map you can see the monsters walk around,
// and a fight starts when one reaches the party. replaces the random battles
public partial class EncounterEditorViewModel
{
    // screen pixels per map tile in MonsterMapImage
    public const int MonsterTilePixels = 32;
    public const int MaxMonsters = 12;

    // maps that can have monsters (all but the world map): the labels, and _monsterMapIds has the map ids in the same order
    public ObservableCollection<string> MonsterMapOptions { get; } = new();
    private readonly List<int> _monsterMapIds = new();
    public ObservableCollection<MonsterSpriteChoice> MonsterSpriteOptions { get; } = new();
    public int[] MonsterCounts { get; } = Enumerable.Range(1, MaxMonsters).ToArray();
    public string[] MonsterRanges { get; } = { "Anywhere on the map", "Up to 4 tiles from its start", "Up to 8 tiles from its start", "Up to 12 tiles from its start" };
    private static readonly int[] RangeTiles = { 0, 4, 8, 12 };

    [ObservableProperty] private int _selectedMonsterMap = -1;
    [ObservableProperty] private int _monsterCount = 5;
    [ObservableProperty] private int _selectedMonsterSprite = -1;
    [ObservableProperty] private int _monsterRangeIndex;
    [ObservableProperty] private string _monsterGroupText = "";
    [ObservableProperty] private ImageSource? _monsterMapImage;
    [ObservableProperty] private double _monsterMapPixelWidth;
    [ObservableProperty] private double _monsterMapPixelHeight;
    [ObservableProperty] private string _monsterMapInfo = "";
    [ObservableProperty] private string _monsterHoverInfo = "";
    [ObservableProperty] private bool _isMonsterMapShown;

    private LufiaMap? _monsterMap;
    private uint[]? _monsterMapPixels;
    private HashSet<(int, int)> _monsterOpen = new();
    private int _monsterSeed = Environment.TickCount;
    private const int DefaultMonsterSprite = 0x33;

    private void StartMonsterMaps()
    {
        if (Ctx == null) return;
        var rom = Ctx.Rom;
        int keepMap = SelectedMonsterMap >= 0 && SelectedMonsterMap < _monsterMapIds.Count ? _monsterMapIds[SelectedMonsterMap] : -1;
        MonsterMapOptions.Clear();
        _monsterMapIds.Clear();
        foreach (var (id, label) in _mapLabels.OrderBy(kv => kv.Key))
        {
            if (id == LufiaMap.WorldMap) continue;
            int monsters = VisibleMonsters.Monsters(rom, id).Count, group = -1;
            try { group = VisibleMonsters.Group(rom, id); } catch { }
            string what = monsters > 0 ? $"{monsters} monster{(monsters == 1 ? "" : "s")}"
                        : group > 0 ? $"random battles (group {group:X2})" : "no battles";
            MonsterMapOptions.Add($"{label}: {what}");
            _monsterMapIds.Add(id);
        }
        if (MonsterSpriteOptions.Count == 0) FillMonsterSprites();
        int index = keepMap >= 0 ? _monsterMapIds.IndexOf(keepMap) : _monsterMapIds.FindIndex(id => VisibleMonsters.Monsters(rom, id).Count > 0);
        SelectedMonsterMap = -1;
        SelectedMonsterMap = index >= 0 ? index : _monsterMapIds.FindIndex(id => { try { return VisibleMonsters.Group(rom, id) > 0; } catch { return false; } });
    }

    // the ROM's sprites, then the Lufia 2 monsters built into Lufia Forge (Sprites\L2 *.png) that the ROM
    // doesn't have yet. one the ROM already has shows as its sprite, with the name
    private void FillMonsterSprites()
    {
        if (Ctx == null) return;
        var rom = Ctx.Rom;
        MonsterSpriteOptions.Clear();
        var lufia2Sheets = Lufia2Sheets();
        var named = new Dictionary<int, string>();
        int count = NpcSprites.Count(rom);
        var have = Enumerable.Range(0, count).Where(n => NpcSprites.IsNew(rom, n))
                             .Select(n => (n, sheet: NpcSprites.RenderSheet(rom, n))).Where(x => x.sheet != null).ToList();
        var missing = new List<(string Name, uint[] Px, int Fw, int H)>();
        foreach (var monster in lufia2Sheets)
        {
            int found = have.FirstOrDefault(x => SameSheet(x.sheet!.Value.Px, x.sheet.Value.W, x.sheet.Value.H, monster.Px, monster.Fw * 8, monster.H), (-1, null)).n;
            if (found >= 0 && have.Any(x => x.n == found)) named[found] = monster.Name; else missing.Add(monster);
        }
        for (int sprite = 0; sprite < count; sprite++)
        {
            if (NpcSprites.Render(rom, sprite) is not { } rendered) continue;
            MonsterSpriteOptions.Add(new MonsterSpriteChoice { Number = sprite, Image = Picture(rendered.Px, rendered.W, rendered.H), Lufia2Name = named.GetValueOrDefault(sprite) });
        }
        foreach (var monster in missing)
        {
            // the front view (frame 4) is the picture
            var front = new uint[monster.Fw * monster.H];
            for (int y = 0; y < monster.H; y++) Array.Copy(monster.Px, y * monster.Fw * 8 + 4 * monster.Fw, front, y * monster.Fw, monster.Fw);
            MonsterSpriteOptions.Add(new MonsterSpriteChoice { Number = -1, Image = Picture(front, monster.Fw, monster.H), Lufia2Name = monster.Name, Sheet = (monster.Px, monster.Fw, monster.H) });
        }
    }

    private static ImageSource Picture(uint[] px, int w, int h)
    {
        var bmp = BitmapSource.Create(w, h, 96, 96, PixelFormats.Bgra32, null, px, w * 4);
        bmp.Freeze();
        return bmp;
    }

    private static List<(string Name, uint[] Px, int Fw, int H)>? _lufia2Sheets;

    // the Lufia 2 monster sheets built into Lufia Forge, in Lufia2Sprites.Monsters order
    private static List<(string Name, uint[] Px, int Fw, int H)> Lufia2Sheets()
    {
        if (_lufia2Sheets != null) return _lufia2Sheets;
        var list = new List<(string, uint[], int, int)>();
        var assembly = typeof(EncounterEditorViewModel).Assembly;
        foreach (var (_, name) in Lufia2Sprites.Monsters)
        {
            using var stream = assembly.GetManifestResourceStream($"Sprites.L2 {name}.png");
            if (stream == null) continue;
            var frame = BitmapDecoder.Create(stream, BitmapCreateOptions.PreservePixelFormat, BitmapCacheOption.OnLoad).Frames[0];
            var bmp = new FormatConvertedBitmap(frame, PixelFormats.Bgra32, null, 0);
            var pixels = new uint[bmp.PixelWidth * bmp.PixelHeight];
            bmp.CopyPixels(pixels, bmp.PixelWidth * 4, 0);
            list.Add((name, pixels, bmp.PixelWidth / 8, bmp.PixelHeight));
        }
        return _lufia2Sheets = list;
    }

    // true if two sheets have the same shapes in every frame. frames 0-1 are left out, the game draws 2-3.
    // colours don't count, a monster added with the closest palette has different ones
    private static bool SameSheet(uint[] a, int aw, int ah, uint[] b, int bw, int bh)
    {
        if (aw != bw || ah != bh) return false;
        int frameWidth = aw / 8;
        for (int y = 0; y < ah; y++)
            for (int x = 2 * frameWidth; x < aw; x++)
                if ((a[y * aw + x] >> 24 < 128) != (b[y * aw + x] >> 24 < 128)) return false;
        return true;
    }

    private int MonsterMapId => SelectedMonsterMap >= 0 && SelectedMonsterMap < _monsterMapIds.Count ? _monsterMapIds[SelectedMonsterMap] : -1;
    private string MonsterMapName => SelectedMonsterMap >= 0 && SelectedMonsterMap < MonsterMapOptions.Count ? MonsterMapOptions[SelectedMonsterMap].Split(':')[0] : "";

    partial void OnSelectedMonsterMapChanged(int value) => LoadMonsterMap();

    // the selected map's monsters, as placed in the ROM
    private List<VisibleMonsters.Monster> _placed = new();

    // the map's monsters, one row each. the picked one is the one the settings below are for
    public ObservableCollection<MapMonsterRow> MapMonsterOptions { get; } = new();
    [ObservableProperty] private int _selectedMapMonster = -1;
    [ObservableProperty] private string _monsterGroupInfo = "";
    // the picked monster comes after the party (inside its roaming area, or within 6 tiles when it roams the
    // whole map), else it just wanders
    [ObservableProperty] private bool _monsterChases = true;

    // what a click on the map picture does: put / pick monsters, add points to the picked monster's path,
    // or add switch tiles
    public string[] MonsterClickModeOptions { get; } = { "Puts monsters / picks them", "Draws the picked monster's path", "Adds switch tiles" };
    [ObservableProperty] private int _monsterClickModeIndex;
    public string[] MonsterPathModeOptions { get; } = { "No path: roams", "Walks its path round and round", "Walks its path there and back", "Walks its path once, then stays" };
    [ObservableProperty] private int _monsterPathModeIndex;
    // what a new switch tile does: an event of its own (you write it in the Events editor) or opening a door
    public ObservableCollection<string> SwitchActionOptions { get; } = new();
    [ObservableProperty] private int _switchActionIndex;
    private List<int> _doors = new();
    // the map's step-on areas that are switched on: number, rectangle
    private List<(int Index, int X1, int Y1, int X2, int Y2)> _areas = new();

    partial void OnMonsterClickModeIndexChanged(int value)
    {
        OnPropertyChanged(nameof(IsClickMonsters)); OnPropertyChanged(nameof(IsClickPath)); OnPropertyChanged(nameof(IsClickSwitch));
        DrawMonsterMap();
    }

    public bool IsClickMonsters { get => MonsterClickModeIndex == 0; set { if (value) MonsterClickModeIndex = 0; } }
    public bool IsClickPath { get => MonsterClickModeIndex == 1; set { if (value) MonsterClickModeIndex = 1; } }
    public bool IsClickSwitch { get => MonsterClickModeIndex == 2; set { if (value) MonsterClickModeIndex = 2; } }

    // the page showing (0 Monsters, 1 World map, 2 Visible monsters). the list of each map's group goes away on the
    // last one, which uses the room
    [ObservableProperty] private int _encounterTabIndex;
    public bool IsMapGroupsShown => EncounterTabIndex != 2;
    public int MiddleColumnSpan => EncounterTabIndex == 2 ? 3 : 1;
    partial void OnEncounterTabIndexChanged(int value) { OnPropertyChanged(nameof(IsMapGroupsShown)); OnPropertyChanged(nameof(MiddleColumnSpan)); }

    partial void OnMonsterGroupTextChanged(string value) => MonsterGroupInfo = GroupSummary(value);

    // "Spider, Kobold, Giant Bat": the monsters a group's battles can have (text = hex group number), or why not
    private string GroupSummary(string text)
    {
        if (!int.TryParse(text.Trim(), NumberStyles.HexNumber, CultureInfo.InvariantCulture, out int group) || group < 1 || group > Count)
            return text.Trim().Length == 0 ? "" : $"No group {text.Trim()}: 01-{Count:X2}.";
        if (Ctx == null) return "";
        var names = new List<string>();
        foreach (int subgroup in _groups[group - 1].Distinct())
            foreach (int monster in Encounters.SubgroupMonsters(Ctx.Rom, subgroup))
                if (monster != 0xFF && monster < MonsterChoices.Count - 1)
                {
                    string name = MonsterChoices[monster][3..].Trim();
                    if (!names.Contains(name)) names.Add(name);
                }
        return names.Count == 0 ? $"Group {group:X2} has no monsters yet: give it subgroups on the Monsters page." : $"Group {group:X2}: {string.Join(", ", names)}";
    }

    partial void OnSelectedMapMonsterChanged(int value)
    {
        // the picked monster's settings show below so you can change them
        if (value < 0 || value >= _placed.Count) { DrawMonsterMap(); return; }
        var monster = _placed[value];
        int spriteIndex = MonsterSpriteOptions.ToList().FindIndex(c => c.Number == monster.Sprite);
        if (spriteIndex >= 0) SelectedMonsterSprite = spriteIndex;
        if (monster.Group > 0) MonsterGroupText = monster.Group.ToString("X2");
        MonsterRangeIndex = RangeIndex(monster.Range);
        MonsterChases = monster.Chases;
        MonsterPathModeIndex = (int)monster.Mode;
        DrawMonsterMap();
    }

    private static int RangeIndex(int range)
    {
        int i = Array.FindIndex(RangeTiles, t => t == range);
        if (i >= 0) return i;
        i = Array.FindIndex(RangeTiles, t => t >= range);
        return range == 0 ? 0 : i >= 0 ? i : RangeTiles.Length - 1;
    }

    // loads the selected map: its picture, its monsters and their settings
    private void LoadMonsterMap(int pick = -1)
    {
        _monsterMap = null; _monsterMapPixels = null;
        MonsterMapImage = null;
        IsMonsterMapShown = false;
        MapMonsterOptions.Clear();
        _placed = new();
        int id = MonsterMapId;
        if (Ctx == null || id < 0) { MonsterMapInfo = ""; return; }
        var rom = Ctx.Rom;
        try
        {
            var map = LufiaMap.Load(rom, id);
            _monsterMap = map;
            _monsterOpen = VisibleMonsters.OpenTiles(rom, map).ToHashSet();
            _placed = VisibleMonsters.Placed(rom, map);
            var enabledAreas = MapEvents.EnabledAreas(rom, id);
            _areas = map.Triggers.Where(t => !t.IsUnused && enabledAreas.Contains(t.Index)).Select(t => (t.Index, t.X1, t.Y1, t.X2, t.Y2)).ToList();
            _doors = VisibleMonsters.Doors(rom, id);
            int keepAction = SwitchActionIndex;
            SwitchActionOptions.Clear();
            SwitchActionOptions.Add("An event of its own (write it in the Events editor)");
            foreach (int door in _doors) SwitchActionOptions.Add($"Opens door {door + 1} for good");
            SwitchActionIndex = keepAction >= 0 && keepAction < SwitchActionOptions.Count ? keepAction : 0;
            int group = -1;
            try { group = VisibleMonsters.Group(rom, id); } catch { }
            for (int i = 0; i < _placed.Count; i++) MapMonsterOptions.Add(Row(i, _placed[i]));
            if (_placed.Count == 0)
            {
                MonsterGroupText = group > 0 ? group.ToString("X2") : "";
                if (SelectedMonsterSprite < 0) SelectedMonsterSprite = Math.Max(0, MonsterSpriteOptions.ToList().FindIndex(c => c.Number == DefaultMonsterSprite));
            }
            _monsterMapPixels = MapTileset.ForMap(rom, map).RenderMap(map);
            MonsterMapPixelWidth = map.Width * MonsterTilePixels;
            MonsterMapPixelHeight = map.Height * MonsterTilePixels;
            IsMonsterMapShown = true;
            var (firstChar, _) = VisibleMonsters.Monsters(rom, id);
            var groups = _placed.Select(m => m.Group).Distinct().ToList();
            MonsterMapInfo = _placed.Count > 0
                ? $"{_placed.Count} monster{(_placed.Count == 1 ? "" : "s")}, fighting group{(groups.Count == 1 ? "" : "s")} {string.Join(", ", groups.Select(g => g.ToString("X2")))} when they reach the party; this map has no random battles while it has them. " +
                  $"They are its characters {firstChar}-{firstChar + _placed.Count - 1}: if a cutscene on this map brings in characters of its own, try it in the game."
                : group > 0 ? $"Random battles with group {group:X2}. Put monsters here to have visible ones instead."
                            : "This map has no random battles: type the group its monsters fight.";
            SelectedMapMonster = -1;
            SelectedMapMonster = pick >= 0 && pick < _placed.Count ? pick : _placed.Count > 0 ? 0 : -1;
            if (SelectedMapMonster < 0) DrawMonsterMap();
        }
        catch (Exception ex) { MonsterMapInfo = "This map can't be shown: " + ex.Message; }
    }

    private MapMonsterRow Row(int i, VisibleMonsters.Monster monster)
    {
        ImageSource? image = null;
        if (Ctx != null && NpcSprites.Render(Ctx.Rom, monster.Sprite) is { } rendered) image = Picture(rendered.Px, rendered.W, rendered.H);
        string name = MonsterSpriteOptions.FirstOrDefault(c => c.Number == monster.Sprite)?.Lufia2Name ?? $"Sprite {monster.Sprite:X2}";
        string roam = monster.Mode != VisibleMonsters.PathMode.None && monster.Path is { Count: > 0 }
            ? $"walks a path of {monster.Path.Count} points ({monster.Mode switch { VisibleMonsters.PathMode.Loop => "round and round", VisibleMonsters.PathMode.BackAndForth => "there and back", _ => "once" }})"
            : monster.Range == 0 ? "roams the whole map" : $"roams up to {monster.Range} tiles";
        roam += monster.Chases ? ", comes after the party" : ", keeps to itself";
        string path = monster.Mode != VisibleMonsters.PathMode.None && monster.Path is { Count: > 0 } ? $", path ({monster.Path.Count})" : "";
        return new MapMonsterRow
        {
            Image = image, Title = $"{i + 1}. {name} at {monster.X},{monster.Y}",
            Short = $"group {monster.Group:X2}{path}{(monster.Chases ? ", chases" : "")}",
            Detail = $"{GroupSummary(monster.Group.ToString("X2"))}; {roam}",
        };
    }

    // the map picture with the monsters drawn on it (the picked one framed in yellow), and the floor they
    // can go on faintly marked
    private void DrawMonsterMap()
    {
        if (Ctx == null || _monsterMap is not { } map || _monsterMapPixels == null) return;
        int w = map.Width * 16, h = map.Height * 16;
        var pixels = (uint[])_monsterMapPixels.Clone();
        foreach (var (x, y) in _monsterOpen)
            for (int dy = 7; dy < 9; dy++) for (int dx = 7; dx < 9; dx++) pixels[(y * 16 + dy) * w + x * 16 + dx] = 0xFFE8E0A0;
        // step-on areas (switch tiles too), the things monsters and the party set off
        foreach (var (_, x1, y1, x2, y2) in _areas)
        {
            Outline(pixels, w, h, x1 * 16, y1 * 16, x2 * 16 - 1, y2 * 16 - 1, 0xFF40E0F0);
            Outline(pixels, w, h, x1 * 16 + 1, y1 * 16 + 1, x2 * 16 - 2, y2 * 16 - 2, 0xFF40E0F0);
        }
        // paths: the picked monster's in yellow, the others in blue
        for (int i = 0; i < _placed.Count; i++)
        {
            if (_placed[i].Mode == VisibleMonsters.PathMode.None || _placed[i].Path is not { Count: > 0 } points) continue;
            uint color = i == SelectedMapMonster ? 0xFFFFE040 : 0xFF6090FF;
            var all = new List<(int X, int Y)> { (_placed[i].X, _placed[i].Y) };
            all.AddRange(points);
            if (_placed[i].Mode == VisibleMonsters.PathMode.Loop) all.Add(points[0]);
            for (int k = 1; k < all.Count; k++) Line(pixels, w, h, all[k - 1].X * 16 + 8, all[k - 1].Y * 16 + 8, all[k].X * 16 + 8, all[k].Y * 16 + 8, color);
            foreach (var (x, y) in points) for (int dy = 5; dy < 11; dy++) for (int dx = 5; dx < 11; dx++) pixels[(y * 16 + dy) * w + x * 16 + dx] = color;
        }
        for (int i = 0; i < _placed.Count; i++)
        {
            var (x, y, sprite, range, _, _, _, _) = _placed[i];
            if (range > 0 && i == SelectedMapMonster)
                Outline(pixels, w, h, Math.Max(0, x - range) * 16, Math.Max(0, y - range) * 16, Math.Min(map.Width, x + range + 1) * 16 - 1, Math.Min(map.Height, y + range + 1) * 16 - 1, 0xFFE05050);
            if (NpcSprites.Render(Ctx.Rom, sprite) is not { } rendered) { Outline(pixels, w, h, x * 16, y * 16, x * 16 + 15, y * 16 + 15, 0xFFFF4040); continue; }
            int left = x * 16 + 8 - rendered.W / 2, top = y * 16 + 16 - rendered.H;
            for (int sy = 0; sy < rendered.H; sy++)
                for (int sx = 0; sx < rendered.W; sx++)
                {
                    uint color = rendered.Px[sy * rendered.W + sx];
                    int tx = left + sx, ty = top + sy;
                    if ((color >> 24) != 0 && tx >= 0 && ty >= 0 && tx < w && ty < h) pixels[ty * w + tx] = color;
                }
            if (i == SelectedMapMonster) Outline(pixels, w, h, left - 1, top - 1, left + rendered.W, top + rendered.H, 0xFFFFE040);
        }
        var bmp = BitmapSource.Create(w, h, 96, 96, PixelFormats.Bgra32, null, pixels, w * 4);
        bmp.Freeze();
        MonsterMapImage = bmp;
    }

    private static void Line(uint[] px, int w, int h, int x1, int y1, int x2, int y2, uint color)
    {
        int steps = Math.Max(Math.Abs(x2 - x1), Math.Abs(y2 - y1));
        for (int i = 0; i <= steps; i++)
        {
            int x = x1 + (steps == 0 ? 0 : (x2 - x1) * i / steps), y = y1 + (steps == 0 ? 0 : (y2 - y1) * i / steps);
            if (x >= 0 && y >= 0 && x < w && y < h) px[y * w + x] = color;
        }
    }

    private static void Outline(uint[] px, int w, int h, int x1, int y1, int x2, int y2, uint color)
    {
        for (int x = Math.Max(0, x1); x <= Math.Min(w - 1, x2); x++) { if (y1 >= 0 && y1 < h) px[y1 * w + x] = color; if (y2 >= 0 && y2 < h) px[y2 * w + x] = color; }
        for (int y = Math.Max(0, y1); y <= Math.Min(h - 1, y2); y++) { if (x1 >= 0 && x1 < w) px[y * w + x1] = color; if (x2 >= 0 && x2 < w) px[y * w + x2] = color; }
    }

    private int? ParseMonsterGroup()
    {
        if (int.TryParse(MonsterGroupText.Trim(), NumberStyles.HexNumber, CultureInfo.InvariantCulture, out int group) && group >= 1 && group <= Count) return group;
        Status = $"The group the monster fights: 01-{Count:X2} (the list on the left).";
        return null;
    }

    private int RangeShown => RangeTiles[Math.Clamp(MonsterRangeIndex, 0, RangeTiles.Length - 1)];

    // stands in for the sprite picked below in a monster written by WriteMonsters (a Lufia 2
    // monster gets put in the ROM first)
    private const int PickedSprite = -2;

    // a monster at x, y with the settings showing (sprite, roaming, group). null when the group isn't valid
    private VisibleMonsters.Monster? Shown(int x, int y)
    {
        if (ParseMonsterGroup() is not int group) return null;
        var choice = SelectedMonsterSprite >= 0 && SelectedMonsterSprite < MonsterSpriteOptions.Count ? MonsterSpriteOptions[SelectedMonsterSprite] : null;
        int sprite = choice == null ? DefaultMonsterSprite : choice.Number >= 0 ? choice.Number : PickedSprite;
        return new VisibleMonsters.Monster(x, y, sprite, RangeShown, group, MonsterChases);
    }

    // writes the map's monsters (none = take them off), then shows the map again with monster pick picked
    private void WriteMonsters(List<VisibleMonsters.Monster> monsters, string what, int pick = -1)
    {
        if (Ctx == null || MonsterMapId < 0) return;
        int id = MonsterMapId;
        var rom = Ctx.Rom;
        // group edits go in first. the list of each map's group holds places in the setup scripts, and adding or
        // taking off characters moves those
        if (HasPendingEdits && WritePendingEdits() is string error) { Status = error; return; }
        var choice = SelectedMonsterSprite >= 0 && SelectedMonsterSprite < MonsterSpriteOptions.Count ? MonsterSpriteOptions[SelectedMonsterSprite] : null;
        string? note = null;
        int added = -1;
        Commit(what, () =>
        {
            if (monsters.Any(m => m.Sprite == PickedSprite) && choice is { Number: < 0, Sheet: { } sheet })
            {
                // a Lufia 2 monster goes in the ROM first, as a sprite of its own
                added = NpcSprites.AddFromSheet(rom, sheet.Px, sheet.FrameWidth, sheet.Height, out bool exact, true, Lufia2Sprites.PaletteOf(choice.Lufia2Name ?? ""));
                note = exact ? $"{choice.Lufia2Name} is now sprite {added:X2}."
                             : $"{choice.Lufia2Name} is now sprite {added:X2}; the 4 spare sprite palettes were taken, so it uses the closest one.";
                monsters = monsters.Select(m => m.Sprite == PickedSprite ? m with { Sprite = added } : m).ToList();
            }
            VisibleMonsters.AddToMap(rom, id, monsters);
        });
        BuildMapGroups();   // new places in the setup script, and the note on the map
        Edited = false;
        if (note != null) FillMonsterSprites();
        int keepMap = SelectedMonsterMap;
        StartMonsterMaps();
        if (SelectedMonsterMap != keepMap) SelectedMonsterMap = keepMap;
        LoadMonsterMap(pick);
        if (note != null)
        {
            SelectedMonsterSprite = MonsterSpriteOptions.ToList().FindIndex(c => c.Number == added);
            Status += " " + note;
        }
    }

    // a change to the map (switch tiles). gets written like the monsters, then the map shows again
    private void ChangeMap(string what, Action<Core.RomBuffer, int> write, int pick)
    {
        if (Ctx == null || MonsterMapId < 0) return;
        int id = MonsterMapId;
        var rom = Ctx.Rom;
        if (HasPendingEdits && WritePendingEdits() is string error) { Status = error; return; }
        Commit(what, () => write(rom, id));
        BuildMapGroups();
        Edited = false;
        int keepMap = SelectedMonsterMap;
        StartMonsterMaps();
        if (SelectedMonsterMap != keepMap) SelectedMonsterMap = keepMap;
        LoadMonsterMap(pick);
    }

    // gives the picked monster a path of points (none = it roams again)
    private void SetPath(List<(int X, int Y)> points, string what)
    {
        int i = SelectedMapMonster;
        if (i < 0 || i >= _placed.Count) { Status = "Pick a monster in the list (or on the map) first."; return; }
        var mode = (VisibleMonsters.PathMode)Math.Clamp(MonsterPathModeIndex, 0, 3);
        if (points.Count > 0 && mode == VisibleMonsters.PathMode.None) { mode = VisibleMonsters.PathMode.Loop; MonsterPathModeIndex = 1; }
        if (points.Count > VisibleMonsters.MaxPathPoints) { Status = $"A path can have {VisibleMonsters.MaxPathPoints} points."; return; }
        var list = _placed.ToList();
        list[i] = list[i] with { Mode = points.Count > 0 ? mode : VisibleMonsters.PathMode.None, Path = points.Count > 0 ? points : null };
        WriteMonsters(list, what, i);
    }

    [RelayCommand]
    private void ClearPath()
    {
        int i = SelectedMapMonster;
        if (i < 0 || i >= _placed.Count) { Status = "Pick a monster in the list (or on the map) first."; return; }
        SetPath(new(), $"Path of monster {i + 1} cleared");
    }

    // puts MonsterCount monsters on the map, spread over its floor (new places each time),
    // all with the settings showing
    [RelayCommand]
    private void PlaceMonsters()
    {
        if (Ctx == null || _monsterMap == null) return;
        var places = VisibleMonsters.AutoPlaces(Ctx.Rom, _monsterMap, MonsterCount, _monsterSeed++);
        if (places.Count == 0) { Status = "This map has no open floor away from its doors for monsters."; return; }
        var list = new List<VisibleMonsters.Monster>();
        foreach (var (x, y) in places) { if (Shown(x, y) is not { } monster) return; list.Add(monster); }
        WriteMonsters(list, $"Monsters on {MonsterMapName}", 0);
    }

    // the picked monster gets the sprite, group and roaming showing (it stays where it is)
    [RelayCommand]
    private void UpdateMonster()
    {
        int i = SelectedMapMonster;
        if (i < 0 || i >= _placed.Count) { Status = "Pick a monster in the list (or on the map) first."; return; }
        if (Shown(_placed[i].X, _placed[i].Y) is not { } monster) return;
        var list = _placed.ToList();
        var mode = (VisibleMonsters.PathMode)Math.Clamp(MonsterPathModeIndex, 0, 3);
        list[i] = _placed[i].Path is { Count: > 0 } && mode != VisibleMonsters.PathMode.None ? monster with { Mode = mode, Path = _placed[i].Path } : monster;
        WriteMonsters(list, $"Monster {i + 1} on {MonsterMapName}", i);
    }

    // every monster on the map gets the sprite, group and roaming showing (they stay where they are)
    [RelayCommand]
    private void UpdateMonsters()
    {
        if (_placed.Count == 0) { PlaceMonsters(); return; }
        var list = new List<VisibleMonsters.Monster>();
        foreach (var placed in _placed) { if (Shown(placed.X, placed.Y) is not { } monster) return; list.Add(monster with { Mode = placed.Mode, Path = placed.Path }); }
        WriteMonsters(list, $"Monsters on {MonsterMapName}", Math.Max(0, SelectedMapMonster));
    }

    [RelayCommand]
    private void RemoveMonster()
    {
        int i = SelectedMapMonster;
        if (i < 0 || i >= _placed.Count) { Status = "Pick a monster in the list (or on the map) first."; return; }
        var list = _placed.ToList();
        var removed = list[i];
        list.RemoveAt(i);
        WriteMonsters(list, $"Monster at {removed.X},{removed.Y} taken off", Math.Min(i, list.Count - 1));
    }

    [RelayCommand]
    private void RemoveMonsters()
    {
        if (_placed.Count == 0) { Status = "This map has no monsters."; return; }
        WriteMonsters(new(), $"Monsters taken off {MonsterMapName}");
    }

    // a new encounter group (at the end of the list on the left, opened there to fill in) for the picked
    // monster, or for the next ones put on the map when none is picked
    [RelayCommand]
    private void NewGroupForMonster()
    {
        int before = Count;
        NewGroupCommand.Execute(null);
        if (Count == before) return;   // NewGroup already said why not
        string made = Status;
        MonsterGroupText = Count.ToString("X2");
        if (SelectedMapMonster >= 0 && SelectedMapMonster < _placed.Count) UpdateMonster();
        Status = $"{made} The picked monster fights it: put the monsters it should have in its subgroups on the Monsters page.";
    }

    // a click on the map picture (x, y in tiles). picks a monster that's there, else puts one there with the
    // settings showing
    public void MonsterMapClick(int x, int y)
    {
        if (Ctx == null || _monsterMap is not { } map || x < 0 || y < 0 || x >= map.Width || y >= map.Height) return;
        if (MonsterClickModeIndex == 1)
        {
            int i = SelectedMapMonster;
            if (i < 0 || i >= _placed.Count) { Status = "Pick the monster whose path to draw first (in the list)."; return; }
            var points = _placed[i].Path?.ToList() ?? new();
            if (points.Count > 0 && points[^1] == (x, y)) return;
            points.Add((x, y));
            SetPath(points, $"Path of monster {i + 1}: point {points.Count} at {x},{y}");
            return;
        }
        if (MonsterClickModeIndex == 2)
        {
            if (_areas.Any(a => x >= a.X1 && x < a.X2 && y >= a.Y1 && y < a.Y2)) { Status = "There is a step-on area there already: right click to take a switch tile off."; return; }
            int door = SwitchActionIndex > 0 && SwitchActionIndex - 1 < _doors.Count ? _doors[SwitchActionIndex - 1] : -1;
            (int Area, int Event) made = (-1, -1);
            ChangeMap($"Switch tile at {x},{y}", (rom, id) => made = VisibleMonsters.AddSwitch(rom, id, x, y, door), SelectedMapMonster);
            if (made.Event >= 0)
                Status += door >= 0 ? $" It opens door {door + 1} when the party or a monster steps on it (event {made.Event})."
                                    : $" Its event is event {made.Event}: write what it does in the Events editor.";
            return;
        }
        int clicked = _placed.FindIndex(p => p.X == x && p.Y == y);
        if (clicked >= 0) { SelectedMapMonster = clicked; return; }
        if (!_monsterOpen.Contains((x, y))) { Status = "Monsters go on open floor (the dotted tiles), away from doors and arrival points."; return; }
        if (_placed.Count >= MaxMonsters) { Status = $"At most {MaxMonsters} monsters on a map."; return; }
        if (Shown(x, y) is not { } monster) return;
        var list = _placed.ToList();
        list.Add(monster);
        WriteMonsters(list, $"Monster put at {x},{y}", list.Count - 1);
    }

    // a right click on the map picture takes off the monster that's there
    public void MonsterMapRightClick(int x, int y)
    {
        if (MonsterClickModeIndex == 1)
        {
            int i = SelectedMapMonster;
            if (i < 0 || i >= _placed.Count || _placed[i].Path is not { Count: > 0 } points) return;
            var fewer = points.Take(points.Count - 1).ToList();
            SetPath(fewer, $"Path of monster {i + 1}: last point taken off");
            return;
        }
        if (MonsterClickModeIndex == 2)
        {
            var area = _areas.FirstOrDefault(a => x >= a.X1 && x < a.X2 && y >= a.Y1 && y < a.Y2, (Index: -1, X1: 0, Y1: 0, X2: 0, Y2: 0));
            if (area.Index < 0) return;
            if (area.X2 - area.X1 != 1 || area.Y2 - area.Y1 != 1) { Status = "That is one of the map's own step-on areas: change it in the Map Editor."; return; }
            ChangeMap($"Switch tile at {x},{y} taken off", (rom, id) => VisibleMonsters.RemoveArea(rom, id, area.Index), SelectedMapMonster);
            return;
        }
        int clicked = _placed.FindIndex(p => p.X == x && p.Y == y);
        if (clicked < 0) return;
        SelectedMapMonster = clicked;
        RemoveMonster();
    }

    public void MonsterMapHover(int x, int y)
    {
        if (_monsterMap is not { } map || x < 0 || y < 0 || x >= map.Width || y >= map.Height) { MonsterHoverInfo = ""; return; }
        int hovered = _placed.FindIndex(p => p.X == x && p.Y == y);
        bool onArea = _areas.Any(a => x >= a.X1 && x < a.X2 && y >= a.Y1 && y < a.Y2);
        MonsterHoverInfo = $"{x},{y}: " + MonsterClickModeIndex switch
        {
            1 => "click to add a point to the picked monster's path, right click to take its last point off",
            2 => onArea ? "a step-on area (right click to take a switch tile off)" : "click to put a switch tile here",
            _ => hovered >= 0 ? $"monster {hovered + 1}, group {_placed[hovered].Group:X2} (click to pick it, right click to take it off)"
               : onArea ? "a step-on area: monsters walking onto it set it off"
               : _monsterOpen.Contains((x, y)) ? "open floor (click to put a monster here)" : "no monsters here",
        };
    }
}

// one of the map's monsters, as listed
public sealed class MapMonsterRow
{
    public ImageSource? Image { get; init; }
    public string Title { get; init; } = "";
    // the one line under the title: group, path, chasing (the full sentence is the tooltip)
    public string Short { get; init; } = "";
    public string Detail { get; init; } = "";
}
