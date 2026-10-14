using System.Collections.ObjectModel;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using LufiaForge.Core.Maps;

namespace LufiaForge.Modules.MapEditor;

/// <summary>A block to click on (recent brushes, "goes with" suggestions).</summary>
public sealed class BlockChoice
{
    public int Block { get; init; }
    public ImageSource? Image { get; init; }
    public string Tip { get; init; } = "";
}

/// <summary>One "goes with" row: the blocks the game puts on one side of the brush block, most often first.</summary>
public sealed class NeighbourRow
{
    public string Label { get; init; } = "";
    public List<BlockChoice> Blocks { get; init; } = new();
}

/// <summary>
/// Finding blocks: the palette can list the blocks most used by the game's maps on this tileset first, or only the
/// ones this map uses; recent brushes stay one click away; and for the brush block the editor shows which blocks the
/// game puts to its right, left, below and above (from every map with this tileset): river pieces, banks, walls...
/// </summary>
public partial class MapEditorViewModel
{
    public static readonly string[] PaletteModes = { "Tileset order", "Most used in the game first", "Used on this map" };
    [ObservableProperty] private int _paletteMode;
    public IReadOnlyList<string> PaletteModeChoices => PaletteModes;
    partial void OnPaletteModeChanged(int value) { BuildPalette(); OnBrushBoxChanged(); }

    /// <summary>The blocks in the palette, in drawing order (null: tileset order).</summary>
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

    /// <summary>Counts the blocks of every map with this map's tileset (and which blocks touch which).</summary>
    private void BuildBlockStats()
    {
        if (_rom == null || _map == null || _tileset == null) return;
        int key = _map.IsWorld ? -1 : _map.Tileset;
        if (key == _statsTileset && _usage.Length == _tileset.MetatileCount) return;
        int n = _tileset.MetatileCount;
        var usage = new int[n];
        var nb = Enumerable.Range(0, 4).Select(_ => new Dictionary<int, Dictionary<int, int>>()).ToArray();
        foreach (var info in Maps)
        {
            if ((info.MapId == LufiaMap.WorldMap ? -1 : info.Tileset) != key) continue;
            LufiaMap m;
            try { m = info.MapId == _map.MapId ? _map : LufiaMap.Load(_rom, info.MapId); } catch { continue; }
            for (int y = 0; y < m.Height; y++)
                for (int x = 0; x < m.Width; x++)
                {
                    int b = m.GetTile(x, y) & 0x3FF;
                    if (b >= n) continue;
                    usage[b]++;
                    for (int s = 0; s < 4; s++)
                    {
                        int nx = x + Sides[s].Dx, ny = y + Sides[s].Dy;
                        if (nx < 0 || ny < 0 || nx >= m.Width || ny >= m.Height) continue;
                        int o = m.GetTile(nx, ny) & 0x3FF;
                        if (o == b || o >= n) continue;
                        if (!nb[s].TryGetValue(b, out var d)) nb[s][b] = d = new();
                        d[o] = d.GetValueOrDefault(o) + 1;
                    }
                }
        }
        _usage = usage; _neighbours = nb; _statsTileset = key;
    }

    /// <summary>The palette order for the chosen mode.</summary>
    private int[]? PaletteOrder()
    {
        if (_tileset == null || PaletteMode == 0) return null;
        BuildBlockStats();
        int n = _tileset.MetatileCount;
        if (PaletteMode == 1)
            return Enumerable.Range(0, n).OrderByDescending(b => b < _usage.Length ? _usage[b] : 0).ThenBy(b => b).ToArray();
        if (_map == null) return null;
        var used = new HashSet<int>();
        for (int i = 0; i < _map.Layer.Length; i++) used.Add(_map.Layer[i] & 0x3FF);
        return Enumerable.Range(0, n).Where(used.Contains).OrderByDescending(b => b < _usage.Length ? _usage[b] : 0).ToArray();
    }

    private readonly Dictionary<int, ImageSource> _blockImages = new();
    private int _blockImagesKey = -999;

    private ImageSource? BlockImage(int b)
    {
        if (_tileset == null || b < 0 || b >= _tileset.MetatileCount) return null;
        int key = (_map?.IsWorld == true ? -1 : _map?.Tileset ?? -2) * 4096 + (_map?.PaletteIndex ?? 0);
        if (key != _blockImagesKey) { _blockImages.Clear(); _blockImagesKey = key; }
        if (_blockImages.TryGetValue(b, out var img)) return img;
        var px = _tileset.MetatilePixels(b);
        var bmp = BitmapSource.Create(16, 16, 96, 96, PixelFormats.Bgra32, null, px, 64);
        bmp.Freeze();
        return _blockImages[b] = bmp;
    }

    /// <summary>After the brush changes: the recent row and the "goes with" suggestions.</summary>
    private void BrushHelpers()
    {
        if (_tileset == null || Brush < 0 || Brush >= _tileset.MetatileCount) return;
        var existing = RecentBrushes.FirstOrDefault(r => r.Block == Brush);
        if (existing != null) RecentBrushes.Remove(existing);
        RecentBrushes.Insert(0, new BlockChoice { Block = Brush, Image = BlockImage(Brush), Tip = $"Block {Brush:X3}" });
        while (RecentBrushes.Count > 14) RecentBrushes.RemoveAt(RecentBrushes.Count - 1);

        BuildBlockStats();
        var rows = new List<NeighbourRow>();
        for (int s = 0; s < 4; s++)
        {
            if (s >= _neighbours.Length || !_neighbours[s].TryGetValue(Brush, out var d)) continue;
            int total = d.Values.Sum();
            var top = d.OrderByDescending(kv => kv.Value).Take(8)
                       .Select(kv => new BlockChoice { Block = kv.Key, Image = BlockImage(kv.Key), Tip = $"Block {kv.Key:X3}: {kv.Value} of {total} times {Sides[s].Label[2..]} block {Brush:X3} in the game's maps" }).ToList();
            if (top.Count > 0) rows.Add(new NeighbourRow { Label = Sides[s].Label, Blocks = top });
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

    /// <summary>Pieces from other maps: copy part of a map with the same tileset as a stamp brush.</summary>
    [RelayCommand]
    private void PiecesFromMaps()
    {
        if (_rom == null || _map == null) return;
        if (_map.IsWorld) { Status = "The world map has its own blocks: copy pieces of it with the Area tool."; return; }
        var w = new PiecesWindow(_rom, Maps, _map.Tileset, _map.MapId) { Owner = System.Windows.Application.Current?.MainWindow };
        if (w.ShowDialog() == true) UsePattern(w.ResultW, w.ResultH, w.ResultBlocks, w.ResultSource);
    }

    /// <summary>Uses copied blocks (e.g. from Pieces from other maps) as a stamp brush.</summary>
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
