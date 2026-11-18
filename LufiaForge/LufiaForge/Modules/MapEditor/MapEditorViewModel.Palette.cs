using System.Collections.ObjectModel;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using LufiaForge.Core.Maps;

namespace LufiaForge.Modules.MapEditor;

// a block to click on (recent brushes, "goes with" suggestions)
public sealed class BlockChoice
{
    public int Block { get; init; }
    public ImageSource? Image { get; init; }
    public string Tip { get; init; } = "";
}

// one "goes with" row: the blocks the game puts on one side of the brush block, most often first
public sealed class NeighbourRow
{
    public string Label { get; init; } = "";
    public List<BlockChoice> Blocks { get; init; } = new();
}

// finding blocks. the palette can list the blocks the game's maps on this tileset use most first, or only the
// ones this map uses. recent brushes stay one click away, and for the brush block we show which blocks the
// game puts to its right, left, below and above (from every map with this tileset): river pieces, banks, walls...
public partial class MapEditorViewModel
{
    public static readonly string[] PaletteModes = { "Tileset order", "Most used in the game first", "Used on this map" };
    [ObservableProperty] private int _paletteMode;
    public IReadOnlyList<string> PaletteModeChoices => PaletteModes;
    partial void OnPaletteModeChanged(int value) { BuildPalette(); OnBrushBoxChanged(); }

    // the blocks in the palette in drawing order (null = tileset order)
    private int[]? _paletteOrder;
    public bool PaletteIsTilesetOrder => _paletteOrder == null;

    public ObservableCollection<BlockChoice> RecentBrushes { get; } = new();
    [ObservableProperty] private List<NeighbourRow> _neighbourRows = new();
    [ObservableProperty] private string _neighbourNote = "";

    // usage of every block of a tileset on the game's maps, and which blocks sit next to which
    private int _statsTileset = -999, _recentKey = -999;
    private int[] _usage = Array.Empty<int>();
    private Dictionary<int, Dictionary<int, int>>[] _neighbours = Array.Empty<Dictionary<int, Dictionary<int, int>>>();

    private static readonly (int Dx, int Dy, string Label)[] Sides = { (1, 0, "→ right"), (-1, 0, "← left"), (0, 1, "↓ below"), (0, -1, "↑ above") };

    // counts the blocks of every map with this map's tileset (and which blocks touch which)
    private void BuildBlockStats()
    {
        if (_rom == null || _map == null || _tileset == null) return;
        int key = _map.IsWorld ? -1 : _map.Tileset;
        if (key == _statsTileset && _usage.Length == _tileset.MetatileCount) return;
        int blockCount = _tileset.MetatileCount;
        var usage = new int[blockCount];
        var neighbours = Enumerable.Range(0, 4).Select(_ => new Dictionary<int, Dictionary<int, int>>()).ToArray();
        foreach (var info in Maps)
        {
            if ((info.MapId == LufiaMap.WorldMap ? -1 : info.Tileset) != key) continue;
            LufiaMap map;
            try { map = info.MapId == _map.MapId ? _map : LufiaMap.Load(_rom, info.MapId); } catch { continue; }
            for (int y = 0; y < map.Height; y++)
                for (int x = 0; x < map.Width; x++)
                {
                    int block = map.GetTile(x, y) & 0x3FF;
                    if (block >= blockCount) continue;
                    usage[block]++;
                    for (int side = 0; side < 4; side++)
                    {
                        int nx = x + Sides[side].Dx, ny = y + Sides[side].Dy;
                        if (nx < 0 || ny < 0 || nx >= map.Width || ny >= map.Height) continue;
                        int other = map.GetTile(nx, ny) & 0x3FF;
                        if (other == block || other >= blockCount) continue;
                        if (!neighbours[side].TryGetValue(block, out var counts)) neighbours[side][block] = counts = new();
                        counts[other] = counts.GetValueOrDefault(other) + 1;
                    }
                }
        }
        _usage = usage; _neighbours = neighbours; _statsTileset = key;
    }

    // the palette order for the chosen mode
    private int[]? PaletteOrder()
    {
        if (_tileset == null || PaletteMode == 0) return null;
        BuildBlockStats();
        int blockCount = _tileset.MetatileCount;
        if (PaletteMode == 1)
            return Enumerable.Range(0, blockCount).OrderByDescending(b => b < _usage.Length ? _usage[b] : 0).ThenBy(b => b).ToArray();
        if (_map == null) return null;
        var used = new HashSet<int>();
        for (int i = 0; i < _map.Layer.Length; i++) used.Add(_map.Layer[i] & 0x3FF);
        return Enumerable.Range(0, blockCount).Where(used.Contains).OrderByDescending(b => b < _usage.Length ? _usage[b] : 0).ToArray();
    }

    private readonly Dictionary<int, ImageSource> _blockImages = new();
    private int _blockImagesKey = -999;

    private ImageSource? BlockImage(int block)
    {
        if (_tileset == null || block < 0 || block >= _tileset.MetatileCount) return null;
        int key = (_map?.IsWorld == true ? -1 : _map?.Tileset ?? -2) * 4096 + (_map?.PaletteIndex ?? 0);
        if (key != _blockImagesKey) { _blockImages.Clear(); _blockImagesKey = key; }
        if (_blockImages.TryGetValue(block, out var img)) return img;
        var pixels = _tileset.MetatilePixels(block);
        var bmp = BitmapSource.Create(16, 16, 96, 96, PixelFormats.Bgra32, null, pixels, 64);
        bmp.Freeze();
        return _blockImages[block] = bmp;
    }

    // after the brush changes: the recent row and the "goes with" suggestions
    private void BrushHelpers()
    {
        if (_tileset == null || Brush < 0 || Brush >= _tileset.MetatileCount) return;
        var existing = RecentBrushes.FirstOrDefault(r => r.Block == Brush);
        if (existing != null) RecentBrushes.Remove(existing);
        RecentBrushes.Insert(0, new BlockChoice { Block = Brush, Image = BlockImage(Brush), Tip = $"Block {Brush:X3}" });
        while (RecentBrushes.Count > 14) RecentBrushes.RemoveAt(RecentBrushes.Count - 1);

        BuildBlockStats();
        var rows = new List<NeighbourRow>();
        for (int side = 0; side < 4; side++)
        {
            if (side >= _neighbours.Length || !_neighbours[side].TryGetValue(Brush, out var counts)) continue;
            int total = counts.Values.Sum();
            var top = counts.OrderByDescending(kv => kv.Value).Take(8)
                       .Select(kv => new BlockChoice { Block = kv.Key, Image = BlockImage(kv.Key), Tip = $"Block {kv.Key:X3}: {kv.Value} of {total} times {Sides[side].Label[2..]} block {Brush:X3} in the game's maps" }).ToList();
            if (top.Count > 0) rows.Add(new NeighbourRow { Label = Sides[side].Label, Blocks = top });
        }
        NeighbourRows = rows;
        NeighbourNote = rows.Count == 0
            ? $"Block {Brush:X3} isn't used on any map with this tileset yet, so there are no suggestions."
            : $"What the game puts next to block {Brush:X3} (most often first). Click one to paint with it.";
    }

    [RelayCommand]
    private void PickBlock(int block)
    {
        if (_tileset == null || block < 0 || block >= _tileset.MetatileCount) return;
        CancelStamp();
        Brush = block;
        if (Tool != MapTool.Paint) Tool = MapTool.Paint;
    }

    // pieces from other maps: copy part of a map with the same tileset as a stamp brush
    [RelayCommand]
    private void PiecesFromMaps()
    {
        if (_rom == null || _map == null) return;
        if (_map.IsWorld) { Status = "The world map has its own blocks: copy pieces of it with the Area tool."; return; }
        var window = new PiecesWindow(_rom, Maps, _map.Tileset, _map.MapId) { Owner = System.Windows.Application.Current?.MainWindow };
        if (window.ShowDialog() != true || window.ResultMap == null) return;
        // the other map might number its composite blocks (roofs, tree tops) from a different point, so fit them to this map
        var piece = Prefabs.Placer.FromMap(window.ResultMap, window.ResultW, window.ResultH, window.ResultBlocks, window.ResultSource);
        if (new Prefabs.Placer(_rom, _map, Maps).Place(piece) is not { } placed)
        {
            Status = $"That piece of {window.ResultSource} uses blocks this map's tileset doesn't have.";
            return;
        }
        UsePlacement(placed, window.ResultSource);
    }

    // buildings: ready-made structures from the game's maps with this tileset, as a stamp brush
    [RelayCommand]
    private void Buildings()
    {
        if (_rom == null || _map == null) return;
        if (_map.IsWorld) { Status = "The world map has its own blocks: copy pieces of it with the Area tool."; return; }
        BuildingsWindow window;
        var oldCursor = System.Windows.Input.Mouse.OverrideCursor;
        System.Windows.Input.Mouse.OverrideCursor = System.Windows.Input.Cursors.Wait;
        try { window = new BuildingsWindow(_rom, _map, Maps) { Owner = System.Windows.Application.Current?.MainWindow }; }
        catch (Exception ex) { Status = "Buildings: " + ex.Message; return; }
        finally { System.Windows.Input.Mouse.OverrideCursor = oldCursor; }
        if (window.ShowDialog() == true && window.Result is { } placement)
            UsePlacement(placement, $"{placement.Prefab.W} x {placement.Prefab.H} building from {placement.Prefab.Source}");
    }

    // stamps a structure fitted to this map. if it uses blocks this map shows as composites, the map's composites
    // start higher first (the map still looks the same, Undo takes it back)
    private void UsePlacement(Prefabs.Placement placement, string source)
    {
        if (_rom == null || _map == null) return;
        if (_map.CompositeThreshold >= 0 && placement.Threshold > _map.CompositeThreshold)
        {
            if (BlockRoom.PatternEvent(_rom, _map) is { } why) { Status = "That one uses blocks this map shows as roofs and tree tops, and they can't be moved here: " + why; return; }
            if (!_map.RaiseThreshold(placement.Threshold)) { Status = "This map has no room for the blocks that one uses."; return; }
            _tileset = MapTileset.ForMap(_rom, _map);
            BuildPalette();
            RedrawMap();
            MarkEdited();
        }
        UsePattern(placement.Prefab.W, placement.Prefab.H, placement.Cells, source);
    }

    // uses copied blocks (from Pieces from other maps, say) as a stamp brush
    public void UsePattern(int w, int h, ushort[] blocks, string source)
    {
        if (_map == null) return;
        if (Tool != MapTool.Paint) Tool = MapTool.Paint;
        StartStamp(new BlockPattern(w, h, blocks, false, TilesetKey, source));
        BrushBoxW = Math.Min(w, 16) * 16; BrushBoxH = h * 16;
        Status = $"{w} x {h} blocks from {source}: click on the map to stamp them (as often as you like), Esc or right-click to stop.";
    }

    private void OnBrushBoxChanged()
    {
        OnPropertyChanged(nameof(BrushBoxX)); OnPropertyChanged(nameof(BrushBoxY));
        OnPropertyChanged(nameof(PaletteBoxX)); OnPropertyChanged(nameof(PaletteBoxY));
        OnPropertyChanged(nameof(PaletteIsTilesetOrder));
    }
}
