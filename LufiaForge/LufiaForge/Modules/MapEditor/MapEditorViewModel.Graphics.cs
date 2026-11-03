using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using LufiaForge.Core.Maps;

namespace LufiaForge.Modules.MapEditor;

/// <summary>
/// The map's graphics as one choice (each tileset and palette pair the game's maps use, named after them), blocks of
/// other graphics to paint with (From: a block picked there is added to this map's tileset, <see cref="BlockBring"/>),
/// the brush size and the Fill tool.
/// </summary>
public partial class MapEditorViewModel
{
    // ── graphics ──

    public ObservableCollection<string> GraphicsChoices { get; } = new();
    private readonly List<(int Ts, int Pal)> _graphics = new();
    [ObservableProperty] private int _graphicsChoice = -1;

    // every (tileset, palette) the game's maps use, and the maps (read again when the ROM changes)
    private List<((int Ts, int Pal) Key, List<int> Maps)>? _mixes;
    private int _mixesVersion = -1;

    private List<((int Ts, int Pal) Key, List<int> Maps)> Mixes()
    {
        if (_mixes != null && _mixesVersion == _rom!.Version) return _mixes;
        var d = new Dictionary<(int, int), List<int>>();
        foreach (var info in Maps.OrderBy(m => m.MapId))
        {
            if (info.MapId == LufiaMap.WorldMap) continue;
            try
            {
                var m = LufiaMap.Load(_rom!, info.MapId);
                if (m.IsWorld) continue;
                var key = (m.Tileset, m.PaletteIndex);
                if (!d.TryGetValue(key, out var l)) d[key] = l = new();
                l.Add(info.MapId);
            }
            catch { }
        }
        _mixes = d.Select(kv => ((kv.Key.Item1, kv.Key.Item2), kv.Value)).ToList();
        _mixesVersion = _rom!.Version;
        return _mixes;
    }

    private string MixName(List<int> maps, bool mine)
    {
        string Name(int id) => Maps.FirstOrDefault(m => m.MapId == id) is { Name.Length: > 0 } i ? i.Name : $"map {id:X2}";
        if (maps.Count == 0) return "Only this map";
        return Name(maps[0]) + (maps.Count > 1 ? $" and {maps.Count - 1} other map{(maps.Count == 2 ? "" : "s")}" : "") + (mine ? " (and this map)" : "");
    }

    /// <summary>The graphics list and From list again (after a map loads, or Undo).</summary>
    private void RefreshGraphicsChoices()
    {
        var keep = BlockSource > 0 && BlockSource <= _sources.Count ? (_sources[BlockSource - 1].Ts, _sources[BlockSource - 1].Pal) : ((int, int)?)null;
        bool was = _populating;
        _populating = true;
        try
        {
            GraphicsChoices.Clear(); _graphics.Clear(); BlockSourceChoices.Clear(); _sources.Clear();
            if (_rom == null || _map == null || _map.IsWorld) { GraphicsChoice = -1; BlockSource = -1; return; }
            var mine = (_map.Tileset, _map.PaletteIndex);
            var mixes = Mixes().Select(x => (x.Key, Maps: x.Maps.Where(id => id != _map.MapId).ToList())).Where(x => x.Maps.Count > 0).ToList();
            if (!mixes.Any(x => x.Key == mine)) mixes.Insert(0, (mine, new List<int>()));
            int tilesRes = Tilesets.TilesResource(_rom, _map.Tileset), blocksRes = Tilesets.BlocksResource(_rom, _map.Tileset);
            foreach (var (k, maps) in mixes)
            {
                string label = $"{MixName(maps, k == mine)}   (tileset {k.Ts}, palette {k.Pal})";
                _graphics.Add(k);
                GraphicsChoices.Add(label);
                // From: graphics with other blocks (the same blocks in other colours add nothing)
                if (k != mine && maps.Count > 0 && k.Ts < Tilesets.Count(_rom) &&
                    !(Tilesets.TilesResource(_rom, k.Ts) == tilesRes && Tilesets.BlocksResource(_rom, k.Ts) == blocksRes))
                {
                    _sources.Add((k.Ts, k.Pal, maps[0]));
                    BlockSourceChoices.Add(label);
                }
            }
            BlockSourceChoices.Insert(0, "This map's graphics");
            GraphicsChoice = _graphics.IndexOf(mine);
            BlockSource = keep is { } kk && _sources.FindIndex(s => (s.Ts, s.Pal) == kk) is var at and >= 0 ? at + 1 : 0;
        }
        finally { _populating = was; }
        OnPropertyChanged(nameof(HasBlockSources));
        bool other = _srcTileset != null;
        LoadSource();
        if (other || _srcTileset != null) BuildPalette();
    }

    partial void OnGraphicsChoiceChanged(int value)
    {
        if (_populating || _map == null || value < 0 || value >= _graphics.Count) return;
        var (ts, pal) = _graphics[value];
        _populating = true;
        TilesetChoice = ts; MapPalette = pal;
        _populating = false;
        PreviewGraphics();
        Status = $"Map {_map.MapId:X2} shown with the graphics of {GraphicsChoices[value].Split("   ")[0]} (tileset {ts}, palette {pal}). It's written with the map's other edits (Undo takes it back). " +
                 "Block numbers stay the same, so it can look scrambled until you paint it again. To keep this map's look and paint with other graphics' blocks too, pick them under From (above the blocks).";
    }

    // ── blocks of other graphics ──

    public ObservableCollection<string> BlockSourceChoices { get; } = new();
    private readonly List<(int Ts, int Pal, int Map)> _sources = new();
    [ObservableProperty] private int _blockSource;
    private MapTileset? _srcTileset;
    private LufiaMap? _srcMap;
    private int _srcPick = -1;
    private bool PickingOther => _srcTileset != null;
    public bool HasBlockSources => BlockSourceChoices.Count > 1;
    public bool OrderEnabled => !PickingOther;

    partial void OnBlockSourceChanged(int value)
    {
        if (_populating) return;
        _srcPick = -1;
        LoadSource();
        BuildPalette();
        OnBrushBoxChanged();
        if (PickingOther) Status = $"The blocks of {BlockSourceChoices[value].Split("   ")[0]}: pick one (or drag over several) and it's added to this map's tileset, " +
                                   "then paint with it. Blocks the tileset has already are used as they are.";
    }

    private void LoadSource()
    {
        _srcTileset = null; _srcMap = null;
        if (_rom != null && _map != null && !_map.IsWorld && BlockSource > 0 && BlockSource <= _sources.Count)
        {
            try { _srcMap = LufiaMap.Load(_rom, _sources[BlockSource - 1].Map); _srcTileset = MapTileset.ForMap(_rom, _srcMap); }
            catch { _srcMap = null; _srcTileset = null; }
        }
        OnPropertyChanged(nameof(OrderEnabled));
    }

    /// <summary>A click or drag on the palette while it shows other graphics: those blocks brought in as the brush.</summary>
    private void PickFromOther(int x1, int y1, int x2, int y2)
    {
        if (_rom == null || _map == null || _srcMap == null || _srcTileset == null) return;
        int n = _srcTileset.MetatileCount, w = x2 - x1 + 1, h = y2 - y1 + 1;
        var ids = new List<int>();
        for (int y = 0; y < h; y++) for (int x = 0; x < w; x++) ids.Add(Math.Min((y1 + y) * 16 + x1 + x, n - 1));
        if (ids[0] < 0) return;
        if (!WritePendingFirst()) return;
        BlockBring.Result r;
        try
        {
            try { r = BlockBring.Bring(_rom, _map, _srcMap, ids); }
            catch (BlockBring.NeedsRoomException ex)
            {
                if (!MakeRoomFor(ex.Message + " Blocks from other graphics go into spare blocks.")) return;
                if (_map == null || _srcMap == null) return;
                r = BlockBring.Bring(_rom, _map, _srcMap, ids);
            }
        }
        catch (Exception ex) { Status = "These blocks can't be added to this map's tileset: " + ex.Message; return; }
        if (r.Added > 0)
        {
            _mainVm?.NotifyRomModified($"Tileset {_map.Tileset}: {r.Added} block{(r.Added == 1 ? "" : "s")} from tileset {_srcMap.Tileset}");
            _tileset = MapTileset.ForMap(_rom, _map);
        }
        _srcPick = y1 * 16 + x1;
        if (w * h == 1)
        {
            CancelStamp();
            BrushBoxW = BrushBoxH = 16;
            Brush = r.Blocks[0];
            if (Tool is not (MapTool.Paint or MapTool.Fill)) Tool = MapTool.Paint;
        }
        else
        {
            Brush = r.Blocks[0];
            if (Tool != MapTool.Paint) Tool = MapTool.Paint;
            var (bw, bh) = (BrushBoxW, BrushBoxH);
            StartStamp(new BlockPattern(w, h, r.Blocks.Select(b => (ushort)b).ToArray(), false, TilesetKey, "other graphics"));
            BrushBoxW = bw; BrushBoxH = bh;
        }
        BuildPalette();
        OnBrushBoxChanged();
        Status = (r.Added > 0 ? $"{r.Added} block{(r.Added == 1 ? "" : "s")} added to tileset {_map.Tileset}" + (r.NewTiles > 0 ? $" ({r.NewTiles} new tile{(r.NewTiles == 1 ? "" : "s")}, colours matched to this map's palette)" : "") +
                                $": {r.Where}. Edit > Undo takes them out. "
                              : "This map's tileset has these blocks already. ")
               + (w * h == 1 ? $"Brush: block {Brush:X3}." : "Click on the map to stamp them, Esc to stop.");
    }

    // ── brush size and fill ──

    /// <summary>How big the brush paints: 1, 3, 5 or 7 blocks square, around the block under the mouse.</summary>
    [ObservableProperty] private int _brushSize = 1;
    public static IReadOnlyList<int> BrushSizes { get; } = new[] { 1, 3, 5, 7 };
    public double BrushPreviewSize => BrushSize * 16;
    partial void OnBrushSizeChanged(int value) { RefreshBrushPreview(); OnPropertyChanged(nameof(BrushPreviewSize)); }

    public bool IsFillTool { get => Tool == MapTool.Fill; set { if (value) Tool = MapTool.Fill; } }

    /// <summary>Fill: the blocks joined to (x, y) that are the same block become the brush; the whole map with
    /// <paramref name="all"/>.</summary>
    private void FillAt(int x, int y, bool all)
    {
        if (_map == null || _tileset == null) return;
        int target = _map.GetTile(x, y) & 0x3FF, n = 0;
        if (all)
        {
            for (int yy = 0; yy < _map.Height; yy++)
                for (int xx = 0; xx < _map.Width; xx++)
                    if ((_map.GetTile(xx, yy) & 0x3FF) != Brush) { _map.SetTile(xx, yy, Brush); n++; }
        }
        else if (target != Brush)
        {
            var seen = new bool[_map.Width * _map.Height];
            var todo = new Stack<(int, int)>();
            todo.Push((x, y));
            while (todo.Count > 0)
            {
                var (cx, cy) = todo.Pop();
                if (cx < 0 || cy < 0 || cx >= _map.Width || cy >= _map.Height || seen[cy * _map.Width + cx]) continue;
                seen[cy * _map.Width + cx] = true;
                if ((_map.GetTile(cx, cy) & 0x3FF) != target) continue;
                _map.SetTile(cx, cy, Brush); n++;
                todo.Push((cx + 1, cy)); todo.Push((cx - 1, cy)); todo.Push((cx, cy + 1)); todo.Push((cx, cy - 1));
            }
        }
        if (n == 0) { Status = $"Nothing to fill: {(all ? "the whole map is" : "that block is")} block {Brush:X3} already."; return; }
        RedrawMap();
        MarkEdited();
        Status = $"Filled {n} block{(n == 1 ? "" : "s")} with block {Brush:X3}{(all ? " (the whole map)" : "")}. Undo takes it back.";
    }

    [RelayCommand]
    private void FillMap()
    {
        if (_map == null) return;
        FillAt(0, 0, all: true);
    }

    /// <summary>[ and ] change the brush size.</summary>
    public bool BrushSizeKey(System.Windows.Input.Key key)
    {
        int i = BrushSizes.ToList().IndexOf(BrushSize);
        if (key == System.Windows.Input.Key.OemOpenBrackets && i > 0) { BrushSize = BrushSizes[i - 1]; return true; }
        if (key == System.Windows.Input.Key.OemCloseBrackets && i < BrushSizes.Count - 1) { BrushSize = BrushSizes[i + 1]; return true; }
        return key is System.Windows.Input.Key.OemOpenBrackets or System.Windows.Input.Key.OemCloseBrackets;
    }
}
