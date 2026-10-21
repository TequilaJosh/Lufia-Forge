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
    public int Number { get; init; }
    public ImageSource? Image { get; init; }
    public string Tip => $"Sprite {Number:X2}";
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
        if (MonsterSpriteOptions.Count == 0)
        {
            int count = NpcSprites.Count(rom);
            for (int s = 0; s < count; s++)
            {
                if (NpcSprites.Render(rom, s) is not { } r) continue;
                var bmp = BitmapSource.Create(r.W, r.H, 96, 96, PixelFormats.Bgra32, null, r.Px, r.W * 4);
                bmp.Freeze();
                MonsterSpriteOptions.Add(new MonsterSpriteChoice { Number = s, Image = bmp });
            }
        }
        int at = keep >= 0 ? _monsterMapIds.IndexOf(keep) : _monsterMapIds.FindIndex(id => VisibleMonsters.Monsters(rom, id).Count > 0);
        SelectedMonsterMap = -1;
        SelectedMonsterMap = at >= 0 ? at : _monsterMapIds.FindIndex(id => { try { return VisibleMonsters.Group(rom, id) > 0; } catch { return false; } });
    }

    private int MonsterMapId => SelectedMonsterMap >= 0 && SelectedMonsterMap < _monsterMapIds.Count ? _monsterMapIds[SelectedMonsterMap] : -1;

    partial void OnSelectedMonsterMapChanged(int value) => LoadMonsterMap();

    /// <summary>The selected map: its picture, its monsters and their settings.</summary>
    private void LoadMonsterMap()
    {
        _monsterMap = null; _monsterMapPixels = null;
        MonsterMapImage = null;
        IsMonsterMapShown = false;
        int id = MonsterMapId;
        if (Ctx == null || id < 0) { MonsterMapInfo = ""; return; }
        var rom = Ctx.Rom;
        try
        {
            var map = LufiaMap.Load(rom, id);
            _monsterMap = map;
            _monsterOpen = VisibleMonsters.OpenTiles(rom, map).ToHashSet();
            var placed = VisibleMonsters.Placed(rom, map);
            int group = VisibleMonsters.MonsterGroup(rom, id);
            if (group <= 0) { try { group = VisibleMonsters.Group(rom, id); } catch { group = -1; } }
            MonsterGroupText = group > 0 ? group.ToString("X2") : "";
            if (placed.Count > 0)
            {
                MonsterCount = Math.Clamp(placed.Count, 1, MaxMonsters);
                int sp = MonsterSpriteOptions.ToList().FindIndex(c => c.Number == placed[0].Sprite);
                if (sp >= 0) SelectedMonsterSprite = sp;
                int r = placed[0].Range, ri = Array.FindIndex(RangeTiles, t => t == r);
                MonsterRangeIndex = ri >= 0 ? ri : r == 0 ? 0 : Array.FindIndex(RangeTiles, t => t >= r) is int k && k >= 0 ? k : RangeTiles.Length - 1;
            }
            else if (SelectedMonsterSprite < 0)
                SelectedMonsterSprite = Math.Max(0, MonsterSpriteOptions.ToList().FindIndex(c => c.Number == DefaultMonsterSprite));
            _monsterMapPixels = MapTileset.ForMap(rom, map).RenderMap(map);
            MonsterMapPixelWidth = map.Width * MonsterTilePixels;
            MonsterMapPixelHeight = map.Height * MonsterTilePixels;
            DrawMonsterMap(placed);
            IsMonsterMapShown = true;
            var (firstChar, _) = VisibleMonsters.Monsters(rom, id);
            MonsterMapInfo = placed.Count > 0
                ? $"{placed.Count} monster{(placed.Count == 1 ? "" : "s")} fight group {group:X2} when they reach the party; this map has no random battles while it has them. " +
                  $"They are its characters {firstChar}-{firstChar + placed.Count - 1}: if a cutscene on this map brings in characters of its own, try it in the game."
                : group > 0 ? $"Random battles with group {group:X2}. Put monsters here to have visible ones instead."
                            : "This map has no random battles: type the group its monsters fight.";
        }
        catch (Exception ex) { MonsterMapInfo = "This map can't be shown: " + ex.Message; }
    }

    /// <summary>The map picture with the monsters drawn on it (and the floor they may be put on, faintly marked).</summary>
    private void DrawMonsterMap(List<(int X, int Y, int Sprite, int Range)> placed)
    {
        if (Ctx == null || _monsterMap is not { } map || _monsterMapPixels == null) return;
        int w = map.Width * 16, h = map.Height * 16;
        var px = (uint[])_monsterMapPixels.Clone();
        // floor where a monster can go: a light dot in the middle
        foreach (var (x, y) in _monsterOpen)
            for (int dy = 7; dy < 9; dy++) for (int dx = 7; dx < 9; dx++) px[(y * 16 + dy) * w + x * 16 + dx] = 0xFFE8E0A0;
        foreach (var (x, y, sprite, range) in placed)
        {
            if (range > 0)
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
        }
        var bmp = BitmapSource.Create(w, h, 96, 96, PixelFormats.Bgra32, null, px, w * 4);
        bmp.Freeze();
        MonsterMapImage = bmp;
    }

    private static void Outline(uint[] px, int w, int h, int x1, int y1, int x2, int y2, uint c)
    {
        for (int x = Math.Max(0, x1); x <= Math.Min(w - 1, x2); x++) { if (y1 >= 0 && y1 < h) px[y1 * w + x] = c; if (y2 >= 0 && y2 < h) px[y2 * w + x] = c; }
        for (int y = Math.Max(0, y1); y <= Math.Min(h - 1, y2); y++) { if (x1 >= 0 && x1 < w) px[y * w + x1] = c; if (x2 >= 0 && x2 < w) px[y * w + x2] = c; }
    }

    private int MonsterSpriteNumber => SelectedMonsterSprite >= 0 && SelectedMonsterSprite < MonsterSpriteOptions.Count ? MonsterSpriteOptions[SelectedMonsterSprite].Number : DefaultMonsterSprite;

    private int? ParseMonsterGroup()
    {
        if (int.TryParse(MonsterGroupText.Trim(), NumberStyles.HexNumber, CultureInfo.InvariantCulture, out int g) && g >= 1 && g <= Count) return g;
        Status = $"The group the monsters fight: 01-{Count:X2} (the list on the left).";
        return null;
    }

    /// <summary>Writes the map's monsters at <paramref name="places"/> with the settings shown (none = take them off).</summary>
    private void WriteMonsters(List<(int X, int Y)> places, string what)
    {
        if (Ctx == null || MonsterMapId < 0) return;
        int id = MonsterMapId;
        var rom = Ctx.Rom;
        // edits of groups first: the list of each map's group holds places in the setup scripts, which adding or
        // taking off characters moves
        if (HasPendingEdits && WritePendingEdits() is string error) { Status = error; return; }
        if (places.Count == 0)
        {
            Commit(what, () => VisibleMonsters.RemoveFromMap(rom, id));
        }
        else
        {
            if (ParseMonsterGroup() is not int g) return;
            int sprite = MonsterSpriteNumber, range = RangeTiles[Math.Clamp(MonsterRangeIndex, 0, RangeTiles.Length - 1)];
            Commit(what, () => VisibleMonsters.AddToMap(rom, id, places, sprite, range, g));
        }
        BuildMapGroups();   // (new places in the setup script, and the note on the map)
        Edited = false;
        int keep = SelectedMonsterMap;
        StartMonsterMaps();
        if (SelectedMonsterMap != keep) SelectedMonsterMap = keep;
    }

    private List<(int X, int Y)> CurrentPlaces() =>
        Ctx == null || _monsterMap == null ? new() : VisibleMonsters.Placed(Ctx.Rom, _monsterMap).Select(p => (p.X, p.Y)).ToList();

    /// <summary>Puts <see cref="MonsterCount"/> monsters on the map, spread over its floor (new places each time).</summary>
    [RelayCommand]
    private void PlaceMonsters()
    {
        if (Ctx == null || _monsterMap == null) return;
        var places = VisibleMonsters.AutoPlaces(Ctx.Rom, _monsterMap, MonsterCount, _monsterSeed++);
        if (places.Count == 0) { Status = "This map has no open floor away from its doors for monsters."; return; }
        WriteMonsters(places, $"Monsters on {MonsterMapOptions[SelectedMonsterMap].Split(':')[0]}");
    }

    /// <summary>Keeps the monsters where they are with the sprite, group and range shown.</summary>
    [RelayCommand]
    private void UpdateMonsters()
    {
        var places = CurrentPlaces();
        if (places.Count == 0) { PlaceMonsters(); return; }
        WriteMonsters(places, $"Monsters on {MonsterMapOptions[SelectedMonsterMap].Split(':')[0]}");
    }

    [RelayCommand]
    private void RemoveMonsters()
    {
        if (CurrentPlaces().Count == 0) { Status = "This map has no monsters."; return; }
        WriteMonsters(new(), $"Monsters taken off {MonsterMapOptions[SelectedMonsterMap].Split(':')[0]}");
    }

    /// <summary>A click on the map picture (in tiles): a monster there goes, else one is put there.</summary>
    public void MonsterMapClick(int x, int y)
    {
        if (Ctx == null || _monsterMap is not { } map || x < 0 || y < 0 || x >= map.Width || y >= map.Height) return;
        var places = CurrentPlaces();
        int at = places.FindIndex(p => p.X == x && p.Y == y);
        if (at >= 0) { places.RemoveAt(at); WriteMonsters(places, $"Monster at {x},{y} taken off"); return; }
        if (!_monsterOpen.Contains((x, y))) { Status = "Monsters go on open floor (the dotted tiles), away from doors and arrival points."; return; }
        if (places.Count >= MaxMonsters) { Status = $"At most {MaxMonsters} monsters on a map."; return; }
        places.Add((x, y));
        WriteMonsters(places, $"Monster put at {x},{y}");
    }

    public void MonsterMapHover(int x, int y)
    {
        if (_monsterMap is not { } map || x < 0 || y < 0 || x >= map.Width || y >= map.Height) { MonsterHoverInfo = ""; return; }
        bool monster = CurrentPlacesCached().Contains((x, y));
        MonsterHoverInfo = $"{x},{y}: " + (monster ? "a monster (click to take it off)" : _monsterOpen.Contains((x, y)) ? "open floor (click to put a monster here)" : "no monsters here");
    }

    private List<(int X, int Y)>? _placesCache;
    private int _placesCacheMap = -1;
    private List<(int X, int Y)> CurrentPlacesCached()
    {
        if (_placesCache == null || _placesCacheMap != MonsterMapId || MonsterMapImage != _cachedFor) { _placesCache = CurrentPlaces(); _placesCacheMap = MonsterMapId; _cachedFor = MonsterMapImage; }
        return _placesCache;
    }
    private ImageSource? _cachedFor;
}
