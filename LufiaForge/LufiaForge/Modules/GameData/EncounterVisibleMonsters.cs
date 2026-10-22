using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using LufiaForge.Core.Battle;
using LufiaForge.Core.Maps;
using System.Collections.ObjectModel;
using System.Globalization;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace LufiaForge.Modules.GameData;

/// <summary>A character sprite to pick for a map's monsters.</summary>
public sealed class MonsterSpriteChoice
{
    /// <summary>The sprite's number in the ROM; -1 = a Lufia 2 monster not in the ROM yet (added when placed).</summary>
    public int Number { get; init; }
    public ImageSource? Image { get; init; }
    /// <summary>The Lufia 2 monster it is, if it is one.</summary>
    public string? Lufia2Name { get; init; }
    /// <summary>A Lufia 2 monster's sheet (8 frames side by side), to add it to the ROM.</summary>
    public (uint[] Px, int FrameWidth, int Height)? Sheet { get; init; }
    public string Tip => Lufia2Name == null ? $"Sprite {Number:X2}"
        : Number >= 0 ? $"Lufia 2: {Lufia2Name} (sprite {Number:X2})" : $"Lufia 2: {Lufia2Name} (added to the ROM when you place it)";
}

/// <summary>
/// Visible monsters (<see cref="VisibleMonsters"/>): on a town, cave or tower map, monsters you can see walk about and
/// a fight starts when one reaches the party, in place of random battles.
/// </summary>
public partial class EncounterEditorViewModel
{
    /// <summary>Screen pixels per map tile in <see cref="MonsterMapImage"/>.</summary>
    public const int MonsterTilePixels = 32;
    public const int MaxMonsters = 12;

    /// <summary>Maps that can have monsters (all but the world map): label, and the map ids in the same order.</summary>
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
        int keep = SelectedMonsterMap >= 0 && SelectedMonsterMap < _monsterMapIds.Count ? _monsterMapIds[SelectedMonsterMap] : -1;
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
        int at = keep >= 0 ? _monsterMapIds.IndexOf(keep) : _monsterMapIds.FindIndex(id => VisibleMonsters.Monsters(rom, id).Count > 0);
        SelectedMonsterMap = -1;
        SelectedMonsterMap = at >= 0 ? at : _monsterMapIds.FindIndex(id => { try { return VisibleMonsters.Group(rom, id) > 0; } catch { return false; } });
    }

    /// <summary>The ROM's sprites, then the Lufia 2 monsters built into Lufia Forge (Sprites\L2 *.png) that the ROM
    /// doesn't have yet (one it has shows as its sprite, named).</summary>
    private void FillMonsterSprites()
    {
        if (Ctx == null) return;
        var rom = Ctx.Rom;
        MonsterSpriteOptions.Clear();
        var l2 = Lufia2Sheets();
        var named = new Dictionary<int, string>();
        int count = NpcSprites.Count(rom);
        var have = Enumerable.Range(0, count).Where(n => NpcSprites.IsNew(rom, n))
                             .Select(n => (n, sheet: NpcSprites.RenderSheet(rom, n))).Where(x => x.sheet != null).ToList();
        var missing = new List<(string Name, uint[] Px, int Fw, int H)>();
        foreach (var m in l2)
        {
            int found = have.FirstOrDefault(x => SameSheet(x.sheet!.Value.Px, x.sheet.Value.W, x.sheet.Value.H, m.Px, m.Fw * 8, m.H), (-1, null)).n;
            if (found >= 0 && have.Any(x => x.n == found)) named[found] = m.Name; else missing.Add(m);
        }
        for (int n = 0; n < count; n++)
        {
            if (NpcSprites.Render(rom, n) is not { } r) continue;
            MonsterSpriteOptions.Add(new MonsterSpriteChoice { Number = n, Image = Picture(r.Px, r.W, r.H), Lufia2Name = named.GetValueOrDefault(n) });
        }
        foreach (var m in missing)
        {
            // the front view (frame 4) as the picture
            var front = new uint[m.Fw * m.H];
            for (int y = 0; y < m.H; y++) Array.Copy(m.Px, y * m.Fw * 8 + 4 * m.Fw, front, y * m.Fw, m.Fw);
            MonsterSpriteOptions.Add(new MonsterSpriteChoice { Number = -1, Image = Picture(front, m.Fw, m.H), Lufia2Name = m.Name, Sheet = (m.Px, m.Fw, m.H) });
        }
    }

    private static ImageSource Picture(uint[] px, int w, int h)
    {
        var bmp = BitmapSource.Create(w, h, 96, 96, PixelFormats.Bgra32, null, px, w * 4);
        bmp.Freeze();
        return bmp;
    }

    private static List<(string Name, uint[] Px, int Fw, int H)>? _lufia2Sheets;

    /// <summary>The Lufia 2 monster sheets built into Lufia Forge, in <see cref="Lufia2Sprites.Monsters"/> order.</summary>
    private static List<(string Name, uint[] Px, int Fw, int H)> Lufia2Sheets()
    {
        if (_lufia2Sheets != null) return _lufia2Sheets;
        var list = new List<(string, uint[], int, int)>();
        var asm = typeof(EncounterEditorViewModel).Assembly;
        foreach (var (_, name) in Lufia2Sprites.Monsters)
        {
            using var st = asm.GetManifestResourceStream($"Sprites.L2 {name}.png");
            if (st == null) continue;
            var frame = BitmapDecoder.Create(st, BitmapCreateOptions.PreservePixelFormat, BitmapCacheOption.OnLoad).Frames[0];
            var bmp = new FormatConvertedBitmap(frame, PixelFormats.Bgra32, null, 0);
            var px = new uint[bmp.PixelWidth * bmp.PixelHeight];
            bmp.CopyPixels(px, bmp.PixelWidth * 4, 0);
            list.Add((name, px, bmp.PixelWidth / 8, bmp.PixelHeight));
        }
        return _lufia2Sheets = list;
    }

    /// <summary>Two sheets have the same shapes in every frame (frames 0-1 left out: the game draws 2-3). Colours
    /// don't count: a monster added with the closest palette has other ones.</summary>
    private static bool SameSheet(uint[] a, int aw, int ah, uint[] b, int bw, int bh)
    {
        if (aw != bw || ah != bh) return false;
        int fw = aw / 8;
        for (int y = 0; y < ah; y++)
            for (int x = 2 * fw; x < aw; x++)
                if ((a[y * aw + x] >> 24 < 128) != (b[y * aw + x] >> 24 < 128)) return false;
        return true;
    }

    private int MonsterMapId => SelectedMonsterMap >= 0 && SelectedMonsterMap < _monsterMapIds.Count ? _monsterMapIds[SelectedMonsterMap] : -1;
    private string MonsterMapName => SelectedMonsterMap >= 0 && SelectedMonsterMap < MonsterMapOptions.Count ? MonsterMapOptions[SelectedMonsterMap].Split(':')[0] : "";

    partial void OnSelectedMonsterMapChanged(int value) => LoadMonsterMap();

    /// <summary>The selected map's monsters, as placed in the ROM.</summary>
    private List<VisibleMonsters.Monster> _placed = new();

    /// <summary>The map's monsters, one row each (picked: the one the settings below are for).</summary>
    public ObservableCollection<MapMonsterRow> MapMonsterOptions { get; } = new();
    [ObservableProperty] private int _selectedMapMonster = -1;
    [ObservableProperty] private string _monsterGroupInfo = "";
    /// <summary>The picked monster comes after the party (inside its roaming area; within 6 tiles when it roams the
    /// whole map), else it only wanders.</summary>
    [ObservableProperty] private bool _monsterChases = true;

    /// <summary>What a click on the map picture does: put / pick monsters, add points to the picked monster's path,
    /// add switch tiles.</summary>
    public string[] MonsterClickModeOptions { get; } = { "Puts monsters / picks them", "Draws the picked monster's path", "Adds switch tiles" };
    [ObservableProperty] private int _monsterClickModeIndex;
    public string[] MonsterPathModeOptions { get; } = { "No path: roams", "Walks its path round and round", "Walks its path there and back", "Walks its path once, then stays" };
    [ObservableProperty] private int _monsterPathModeIndex;
    /// <summary>What a new switch tile does: an event of its own (to write in the Events editor) or opening a door.</summary>
    public ObservableCollection<string> SwitchActionOptions { get; } = new();
    [ObservableProperty] private int _switchActionIndex;
    private List<int> _doors = new();
    /// <summary>The map's step-on areas that are switched on: number, rectangle.</summary>
    private List<(int Index, int X1, int Y1, int X2, int Y2)> _areas = new();

    partial void OnMonsterClickModeIndexChanged(int value)
    {
        OnPropertyChanged(nameof(IsClickMonsters)); OnPropertyChanged(nameof(IsClickPath)); OnPropertyChanged(nameof(IsClickSwitch));
        DrawMonsterMap();
    }

    public bool IsClickMonsters { get => MonsterClickModeIndex == 0; set { if (value) MonsterClickModeIndex = 0; } }
    public bool IsClickPath { get => MonsterClickModeIndex == 1; set { if (value) MonsterClickModeIndex = 1; } }
    public bool IsClickSwitch { get => MonsterClickModeIndex == 2; set { if (value) MonsterClickModeIndex = 2; } }

    /// <summary>The page shown (0 Monsters, 1 World map, 2 Visible monsters): the list of each map's group goes on the
    /// last, which uses the room.</summary>
    [ObservableProperty] private int _encounterTabIndex;
    public bool IsMapGroupsShown => EncounterTabIndex != 2;
    public int MiddleColumnSpan => EncounterTabIndex == 2 ? 2 : 1;
    partial void OnEncounterTabIndexChanged(int value) { OnPropertyChanged(nameof(IsMapGroupsShown)); OnPropertyChanged(nameof(MiddleColumnSpan)); }

    partial void OnMonsterGroupTextChanged(string value) => MonsterGroupInfo = GroupSummary(value);

    /// <summary>"Spider, Kobold, Giant Bat": the monsters a group's battles can have (hex group number), or why not.</summary>
    private string GroupSummary(string text)
    {
        if (!int.TryParse(text.Trim(), NumberStyles.HexNumber, CultureInfo.InvariantCulture, out int g) || g < 1 || g > Count)
            return text.Trim().Length == 0 ? "" : $"No group {text.Trim()}: 01-{Count:X2}.";
        if (Ctx == null) return "";
        var names = new List<string>();
        foreach (int sub in _groups[g - 1].Distinct())
            foreach (int m in Encounters.SubgroupMonsters(Ctx.Rom, sub))
                if (m != 0xFF && m < MonsterChoices.Count - 1)
                {
                    string n = MonsterChoices[m][3..].Trim();
                    if (!names.Contains(n)) names.Add(n);
                }
        return names.Count == 0 ? $"Group {g:X2} has no monsters yet: give it subgroups on the Monsters page." : $"Group {g:X2}: {string.Join(", ", names)}";
    }

    partial void OnSelectedMapMonsterChanged(int value)
    {
        // the picked monster's settings show below, to change
        if (value < 0 || value >= _placed.Count) { DrawMonsterMap(); return; }
        var m = _placed[value];
        int sp = MonsterSpriteOptions.ToList().FindIndex(c => c.Number == m.Sprite);
        if (sp >= 0) SelectedMonsterSprite = sp;
        if (m.Group > 0) MonsterGroupText = m.Group.ToString("X2");
        MonsterRangeIndex = RangeIndex(m.Range);
        MonsterChases = m.Chases;
        MonsterPathModeIndex = (int)m.Mode;
        DrawMonsterMap();
    }

    private static int RangeIndex(int r)
    {
        int i = Array.FindIndex(RangeTiles, t => t == r);
        if (i >= 0) return i;
        i = Array.FindIndex(RangeTiles, t => t >= r);
        return r == 0 ? 0 : i >= 0 ? i : RangeTiles.Length - 1;
    }

    /// <summary>The selected map: its picture, its monsters and their settings.</summary>
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
            var on = MapEvents.EnabledAreas(rom, id);
            _areas = map.Triggers.Where(t => !t.IsUnused && on.Contains(t.Index)).Select(t => (t.Index, t.X1, t.Y1, t.X2, t.Y2)).ToList();
            _doors = VisibleMonsters.Doors(rom, id);
            int keepAction = SwitchActionIndex;
            SwitchActionOptions.Clear();
            SwitchActionOptions.Add("An event of its own (write it in the Events editor)");
            foreach (int d in _doors) SwitchActionOptions.Add($"Opens door {d + 1} for good");
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

    private MapMonsterRow Row(int i, VisibleMonsters.Monster m)
    {
        ImageSource? img = null;
        if (Ctx != null && NpcSprites.Render(Ctx.Rom, m.Sprite) is { } r) img = Picture(r.Px, r.W, r.H);
        string name = MonsterSpriteOptions.FirstOrDefault(c => c.Number == m.Sprite)?.Lufia2Name ?? $"Sprite {m.Sprite:X2}";
        string roam = m.Mode != VisibleMonsters.PathMode.None && m.Path is { Count: > 0 }
            ? $"walks a path of {m.Path.Count} points ({m.Mode switch { VisibleMonsters.PathMode.Loop => "round and round", VisibleMonsters.PathMode.BackAndForth => "there and back", _ => "once" }})"
            : m.Range == 0 ? "roams the whole map" : $"roams up to {m.Range} tiles";
        roam += m.Chases ? ", comes after the party" : ", keeps to itself";
        string path = m.Mode != VisibleMonsters.PathMode.None && m.Path is { Count: > 0 } ? $", path ({m.Path.Count})" : "";
        return new MapMonsterRow
        {
            Image = img, Title = $"{i + 1}. {name} at {m.X},{m.Y}",
            Short = $"group {m.Group:X2}{path}{(m.Chases ? ", chases" : "")}",
            Detail = $"{GroupSummary(m.Group.ToString("X2"))}; {roam}",
        };
    }

    /// <summary>The map picture with the monsters drawn on it (the picked one framed in yellow), and the floor they
    /// may be put on faintly marked.</summary>
    private void DrawMonsterMap()
    {
        if (Ctx == null || _monsterMap is not { } map || _monsterMapPixels == null) return;
        int w = map.Width * 16, h = map.Height * 16;
        var px = (uint[])_monsterMapPixels.Clone();
        foreach (var (x, y) in _monsterOpen)
            for (int dy = 7; dy < 9; dy++) for (int dx = 7; dx < 9; dx++) px[(y * 16 + dy) * w + x * 16 + dx] = 0xFFE8E0A0;
        // step-on areas (switch tiles too): what monsters and the party set off
        foreach (var (_, x1, y1, x2, y2) in _areas)
        {
            Outline(px, w, h, x1 * 16, y1 * 16, x2 * 16 - 1, y2 * 16 - 1, 0xFF40E0F0);
            Outline(px, w, h, x1 * 16 + 1, y1 * 16 + 1, x2 * 16 - 2, y2 * 16 - 2, 0xFF40E0F0);
        }
        // paths: the picked monster's in yellow, the others' in blue
        for (int i = 0; i < _placed.Count; i++)
        {
            if (_placed[i].Mode == VisibleMonsters.PathMode.None || _placed[i].Path is not { Count: > 0 } pts) continue;
            uint c = i == SelectedMapMonster ? 0xFFFFE040 : 0xFF6090FF;
            var all = new List<(int X, int Y)> { (_placed[i].X, _placed[i].Y) };
            all.AddRange(pts);
            if (_placed[i].Mode == VisibleMonsters.PathMode.Loop) all.Add(pts[0]);
            for (int k = 1; k < all.Count; k++) Line(px, w, h, all[k - 1].X * 16 + 8, all[k - 1].Y * 16 + 8, all[k].X * 16 + 8, all[k].Y * 16 + 8, c);
            foreach (var (x, y) in pts) for (int dy = 5; dy < 11; dy++) for (int dx = 5; dx < 11; dx++) px[(y * 16 + dy) * w + x * 16 + dx] = c;
        }
        for (int i = 0; i < _placed.Count; i++)
        {
            var (x, y, sprite, range, _, _, _, _) = _placed[i];
            if (range > 0 && i == SelectedMapMonster)
                Outline(px, w, h, Math.Max(0, x - range) * 16, Math.Max(0, y - range) * 16, Math.Min(map.Width, x + range + 1) * 16 - 1, Math.Min(map.Height, y + range + 1) * 16 - 1, 0xFFE05050);
            if (NpcSprites.Render(Ctx.Rom, sprite) is not { } r) { Outline(px, w, h, x * 16, y * 16, x * 16 + 15, y * 16 + 15, 0xFFFF4040); continue; }
            int ox = x * 16 + 8 - r.W / 2, oy = y * 16 + 16 - r.H;
            for (int sy = 0; sy < r.H; sy++)
                for (int sx = 0; sx < r.W; sx++)
                {
                    uint c = r.Px[sy * r.W + sx];
                    int tx = ox + sx, ty = oy + sy;
                    if ((c >> 24) != 0 && tx >= 0 && ty >= 0 && tx < w && ty < h) px[ty * w + tx] = c;
                }
            if (i == SelectedMapMonster) Outline(px, w, h, ox - 1, oy - 1, ox + r.W, oy + r.H, 0xFFFFE040);
        }
        var bmp = BitmapSource.Create(w, h, 96, 96, PixelFormats.Bgra32, null, px, w * 4);
        bmp.Freeze();
        MonsterMapImage = bmp;
    }

    private static void Line(uint[] px, int w, int h, int x1, int y1, int x2, int y2, uint c)
    {
        int n = Math.Max(Math.Abs(x2 - x1), Math.Abs(y2 - y1));
        for (int i = 0; i <= n; i++)
        {
            int x = x1 + (n == 0 ? 0 : (x2 - x1) * i / n), y = y1 + (n == 0 ? 0 : (y2 - y1) * i / n);
            if (x >= 0 && y >= 0 && x < w && y < h) px[y * w + x] = c;
        }
    }

    private static void Outline(uint[] px, int w, int h, int x1, int y1, int x2, int y2, uint c)
    {
        for (int x = Math.Max(0, x1); x <= Math.Min(w - 1, x2); x++) { if (y1 >= 0 && y1 < h) px[y1 * w + x] = c; if (y2 >= 0 && y2 < h) px[y2 * w + x] = c; }
        for (int y = Math.Max(0, y1); y <= Math.Min(h - 1, y2); y++) { if (x1 >= 0 && x1 < w) px[y * w + x1] = c; if (x2 >= 0 && x2 < w) px[y * w + x2] = c; }
    }

    private int? ParseMonsterGroup()
    {
        if (int.TryParse(MonsterGroupText.Trim(), NumberStyles.HexNumber, CultureInfo.InvariantCulture, out int g) && g >= 1 && g <= Count) return g;
        Status = $"The group the monster fights: 01-{Count:X2} (the list on the left).";
        return null;
    }

    private int RangeShown => RangeTiles[Math.Clamp(MonsterRangeIndex, 0, RangeTiles.Length - 1)];

    /// <summary>Stands for the sprite picked below in a monster written by <see cref="WriteMonsters"/> (a Lufia 2
    /// monster gets into the ROM first).</summary>
    private const int PickedSprite = -2;

    /// <summary>A monster with the settings shown (sprite, roaming, group) at x, y; null when the group isn't valid.</summary>
    private VisibleMonsters.Monster? Shown(int x, int y)
    {
        if (ParseMonsterGroup() is not int g) return null;
        var choice = SelectedMonsterSprite >= 0 && SelectedMonsterSprite < MonsterSpriteOptions.Count ? MonsterSpriteOptions[SelectedMonsterSprite] : null;
        int sprite = choice == null ? DefaultMonsterSprite : choice.Number >= 0 ? choice.Number : PickedSprite;
        return new VisibleMonsters.Monster(x, y, sprite, RangeShown, g, MonsterChases);
    }

    /// <summary>Writes the map's monsters (none = take them off), then shows the map again with monster
    /// <paramref name="pick"/> picked.</summary>
    private void WriteMonsters(List<VisibleMonsters.Monster> monsters, string what, int pick = -1)
    {
        if (Ctx == null || MonsterMapId < 0) return;
        int id = MonsterMapId;
        var rom = Ctx.Rom;
        // edits of groups first: the list of each map's group holds places in the setup scripts, which adding or
        // taking off characters moves
        if (HasPendingEdits && WritePendingEdits() is string error) { Status = error; return; }
        var choice = SelectedMonsterSprite >= 0 && SelectedMonsterSprite < MonsterSpriteOptions.Count ? MonsterSpriteOptions[SelectedMonsterSprite] : null;
        string? note = null;
        int added = -1;
        Commit(what, () =>
        {
            if (monsters.Any(m => m.Sprite == PickedSprite) && choice is { Number: < 0, Sheet: { } sh })
            {
                // a Lufia 2 monster: into the ROM first, as a sprite of its own
                added = NpcSprites.AddFromSheet(rom, sh.Px, sh.FrameWidth, sh.Height, out bool exact);
                note = exact ? $"{choice.Lufia2Name} is now sprite {added:X2}."
                             : $"{choice.Lufia2Name} is now sprite {added:X2}; the 4 spare sprite palettes were taken, so it uses the closest one.";
                monsters = monsters.Select(m => m.Sprite == PickedSprite ? m with { Sprite = added } : m).ToList();
            }
            VisibleMonsters.AddToMap(rom, id, monsters);
        });
        BuildMapGroups();   // (new places in the setup script, and the note on the map)
        Edited = false;
        if (note != null) FillMonsterSprites();
        int keep = SelectedMonsterMap;
        StartMonsterMaps();
        if (SelectedMonsterMap != keep) SelectedMonsterMap = keep;
        LoadMonsterMap(pick);
        if (note != null)
        {
            SelectedMonsterSprite = MonsterSpriteOptions.ToList().FindIndex(c => c.Number == added);
            Status += " " + note;
        }
    }

    /// <summary>A change to the map (switch tiles): written like the monsters, then the map shown again.</summary>
    private void ChangeMap(string what, Action<Core.RomBuffer, int> write, int pick)
    {
        if (Ctx == null || MonsterMapId < 0) return;
        int id = MonsterMapId;
        var rom = Ctx.Rom;
        if (HasPendingEdits && WritePendingEdits() is string error) { Status = error; return; }
        Commit(what, () => write(rom, id));
        BuildMapGroups();
        Edited = false;
        int keep = SelectedMonsterMap;
        StartMonsterMaps();
        if (SelectedMonsterMap != keep) SelectedMonsterMap = keep;
        LoadMonsterMap(pick);
    }

    /// <summary>The picked monster's path with <paramref name="points"/> (none = it roams again).</summary>
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

    /// <summary>Puts <see cref="MonsterCount"/> monsters on the map, spread over its floor (new places each time),
    /// all with the settings shown.</summary>
    [RelayCommand]
    private void PlaceMonsters()
    {
        if (Ctx == null || _monsterMap == null) return;
        var places = VisibleMonsters.AutoPlaces(Ctx.Rom, _monsterMap, MonsterCount, _monsterSeed++);
        if (places.Count == 0) { Status = "This map has no open floor away from its doors for monsters."; return; }
        var list = new List<VisibleMonsters.Monster>();
        foreach (var (x, y) in places) { if (Shown(x, y) is not { } m) return; list.Add(m); }
        WriteMonsters(list, $"Monsters on {MonsterMapName}", 0);
    }

    /// <summary>The picked monster gets the sprite, group and roaming shown (it stays where it is).</summary>
    [RelayCommand]
    private void UpdateMonster()
    {
        int i = SelectedMapMonster;
        if (i < 0 || i >= _placed.Count) { Status = "Pick a monster in the list (or on the map) first."; return; }
        if (Shown(_placed[i].X, _placed[i].Y) is not { } m) return;
        var list = _placed.ToList();
        var mode = (VisibleMonsters.PathMode)Math.Clamp(MonsterPathModeIndex, 0, 3);
        list[i] = _placed[i].Path is { Count: > 0 } && mode != VisibleMonsters.PathMode.None ? m with { Mode = mode, Path = _placed[i].Path } : m;
        WriteMonsters(list, $"Monster {i + 1} on {MonsterMapName}", i);
    }

    /// <summary>Every monster of the map gets the sprite, group and roaming shown (they stay where they are).</summary>
    [RelayCommand]
    private void UpdateMonsters()
    {
        if (_placed.Count == 0) { PlaceMonsters(); return; }
        var list = new List<VisibleMonsters.Monster>();
        foreach (var p in _placed) { if (Shown(p.X, p.Y) is not { } m) return; list.Add(m with { Mode = p.Mode, Path = p.Path }); }
        WriteMonsters(list, $"Monsters on {MonsterMapName}", Math.Max(0, SelectedMapMonster));
    }

    [RelayCommand]
    private void RemoveMonster()
    {
        int i = SelectedMapMonster;
        if (i < 0 || i >= _placed.Count) { Status = "Pick a monster in the list (or on the map) first."; return; }
        var list = _placed.ToList();
        var m = list[i];
        list.RemoveAt(i);
        WriteMonsters(list, $"Monster at {m.X},{m.Y} taken off", Math.Min(i, list.Count - 1));
    }

    [RelayCommand]
    private void RemoveMonsters()
    {
        if (_placed.Count == 0) { Status = "This map has no monsters."; return; }
        WriteMonsters(new(), $"Monsters taken off {MonsterMapName}");
    }

    /// <summary>A new encounter group (at the end of the list on the left, opened there to fill in) for the picked
    /// monster, or for the next ones put on the map when none is picked.</summary>
    [RelayCommand]
    private void NewGroupForMonster()
    {
        int before = Count;
        NewGroupCommand.Execute(null);
        if (Count == before) return;   // (NewGroup said why not)
        string made = Status;
        MonsterGroupText = Count.ToString("X2");
        if (SelectedMapMonster >= 0 && SelectedMapMonster < _placed.Count) UpdateMonster();
        Status = $"{made} The picked monster fights it: put the monsters it should have in its subgroups on the Monsters page.";
    }

    /// <summary>A click on the map picture (in tiles): a monster there is picked, else one with the settings shown
    /// is put there.</summary>
    public void MonsterMapClick(int x, int y)
    {
        if (Ctx == null || _monsterMap is not { } map || x < 0 || y < 0 || x >= map.Width || y >= map.Height) return;
        if (MonsterClickModeIndex == 1)
        {
            int i = SelectedMapMonster;
            if (i < 0 || i >= _placed.Count) { Status = "Pick the monster whose path to draw first (in the list)."; return; }
            var pts = _placed[i].Path?.ToList() ?? new();
            if (pts.Count > 0 && pts[^1] == (x, y)) return;
            pts.Add((x, y));
            SetPath(pts, $"Path of monster {i + 1}: point {pts.Count} at {x},{y}");
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
        int at = _placed.FindIndex(p => p.X == x && p.Y == y);
        if (at >= 0) { SelectedMapMonster = at; return; }
        if (!_monsterOpen.Contains((x, y))) { Status = "Monsters go on open floor (the dotted tiles), away from doors and arrival points."; return; }
        if (_placed.Count >= MaxMonsters) { Status = $"At most {MaxMonsters} monsters on a map."; return; }
        if (Shown(x, y) is not { } m) return;
        var list = _placed.ToList();
        list.Add(m);
        WriteMonsters(list, $"Monster put at {x},{y}", list.Count - 1);
    }

    /// <summary>A right click on the map picture: the monster there goes.</summary>
    public void MonsterMapRightClick(int x, int y)
    {
        if (MonsterClickModeIndex == 1)
        {
            int i = SelectedMapMonster;
            if (i < 0 || i >= _placed.Count || _placed[i].Path is not { Count: > 0 } pts) return;
            var less = pts.Take(pts.Count - 1).ToList();
            SetPath(less, $"Path of monster {i + 1}: last point taken off");
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
        int at = _placed.FindIndex(p => p.X == x && p.Y == y);
        if (at < 0) return;
        SelectedMapMonster = at;
        RemoveMonster();
    }

    public void MonsterMapHover(int x, int y)
    {
        if (_monsterMap is not { } map || x < 0 || y < 0 || x >= map.Width || y >= map.Height) { MonsterHoverInfo = ""; return; }
        int at = _placed.FindIndex(p => p.X == x && p.Y == y);
        bool area = _areas.Any(a => x >= a.X1 && x < a.X2 && y >= a.Y1 && y < a.Y2);
        MonsterHoverInfo = $"{x},{y}: " + MonsterClickModeIndex switch
        {
            1 => "click to add a point to the picked monster's path, right click to take its last point off",
            2 => area ? "a step-on area (right click to take a switch tile off)" : "click to put a switch tile here",
            _ => at >= 0 ? $"monster {at + 1}, group {_placed[at].Group:X2} (click to pick it, right click to take it off)"
               : area ? "a step-on area: monsters walking onto it set it off"
               : _monsterOpen.Contains((x, y)) ? "open floor (click to put a monster here)" : "no monsters here",
        };
    }
}

/// <summary>A monster of the map, as listed.</summary>
public sealed class MapMonsterRow
{
    public ImageSource? Image { get; init; }
    public string Title { get; init; } = "";
    /// <summary>One line under the title: group, path, chasing (the full sentence is the tooltip).</summary>
    public string Short { get; init; } = "";
    public string Detail { get; init; } = "";
}
