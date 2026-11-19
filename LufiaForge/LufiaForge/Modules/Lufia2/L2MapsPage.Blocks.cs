using System.Collections.ObjectModel;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using LufiaForge.Core;
using LufiaForge.Core.Lufia2;

namespace LufiaForge.Modules.Lufia2;

public partial class L2MapsPage
{
    // the brush

    [ObservableProperty] private int _selectedBlock = 1;
    [ObservableProperty] private int _brushW = 1, _brushH = 1;
    private int[] _brush = { 1 };
    [ObservableProperty] private BitmapSource? _brushPreviewImage;

    public string BrushText => BrushW * BrushH > 1
        ? $"Brush: {BrushW} x {BrushH} blocks from {SelectedBlock:X3}, painted on the {(EditUpper ? "upper" : "ground")} layer."
        : $"Brush: block {SelectedBlock:X3}{(BrushSize > 1 ? $", {BrushSize} x {BrushSize}" : "")}, painted on the {(EditUpper ? "upper" : "ground")} layer.";

    // how big a one-block brush paints: a 1, 3, 5 or 7 block square around the block under the mouse
    [ObservableProperty] private int _brushSize = 1;
    public static IReadOnlyList<int> BrushSizes { get; } = new[] { 1, 3, 5, 7 };
    partial void OnBrushSizeChanged(int value) => BrushChanged();

    // the brush the way it gets painted. a single block grows to a BrushSize square centred on the block under the
    // mouse (Off = how far the top-left is from it), a brush of several blocks paints just as it was picked
    private (int W, int H, int[] Cells, int Off) Painted()
    {
        if (BrushW * BrushH == 1 && BrushSize > 1) return (BrushSize, BrushSize, Enumerable.Repeat(_brush[0], BrushSize * BrushSize).ToArray(), -(BrushSize / 2));
        return (BrushW, BrushH, _brush, 0);
    }

    partial void OnSelectedBlockChanged(int value) { OnPropertyChanged(nameof(BrushText)); }

    // makes one block the brush
    public void SetBrush(int block)
    {
        if (block < 0) return;
        BrushW = 1; BrushH = 1; _brush = new[] { block };
        SelectedBlock = block;
        BrushChanged();
    }

    private void BrushChanged()
    {
        if (_world != null) { BrushPreviewImage = WorldBrushPicture(); UpdatePickerBox(); OnPropertyChanged(nameof(BrushText)); return; }
        BrushPreviewImage = BrushPicture();
        UpdatePickerBox();
        UpdateNeighbours();
        OnPropertyChanged(nameof(BrushText));
    }

    private BitmapSource? BrushPicture()
    {
        if (_blocks.Length == 0) return null;
        if (_srcDragging && _srcBrush != null) return Picture(_srcBlocks, _srcW, _srcH, _srcBrush);
        var (blocksW, blocksH, cells, _) = Painted();
        return Picture(_blocks, blocksW, blocksH, cells);
    }

    private static BitmapSource Picture(uint[][] blocks, int blocksW, int blocksH, int[] cells)
    {
        int w = blocksW * 16, h = blocksH * 16;
        var pixels = new uint[w * h];
        for (int by = 0; by < blocksH; by++)
            for (int bx = 0; bx < blocksW; bx++)
            {
                int block = cells[by * blocksW + bx];
                if (block < 0 || block >= blocks.Length) continue;
                for (int y = 0; y < 16; y++) for (int x = 0; x < 16; x++) pixels[(by * 16 + y) * w + bx * 16 + x] = blocks[block][y * 16 + x];
            }
        return BitmapSource.Create(w, h, 96, 96, PixelFormats.Bgra32, null, pixels.Select(c => (int)c).ToArray(), w * 4);
    }

    [RelayCommand]
    private void PickBlockChoice(int block) => SetBrush(block);

    // the block picker

    [ObservableProperty] private BitmapSource? _pickerImage;
    [ObservableProperty] private double _paletteZoom = 2;
    [ObservableProperty] private double _pickerBoxX, _pickerBoxY, _pickerBoxW = 16, _pickerBoxH = 16;
    [ObservableProperty] private bool _pickerBoxVisible;
    public static readonly string[] OrderModes = { "Block set order (drag for a bigger brush)", "Used most on maps with these graphics", "Used on this map" };
    public IReadOnlyList<string> PaletteModeChoices => OrderModes;
    [ObservableProperty] private int _paletteMode;
    private List<int> _order = new();
    partial void OnPaletteModeChanged(int value) => DrawPicker();

    private void DrawPicker()
    {
        if (_world != null) { DrawWorldPicker(); return; }
        if (_rom == null || _edit == null || _blocks.Length == 0) { PickerImage = null; _order.Clear(); PickerBoxVisible = false; return; }
        var shown = PickingOther ? _srcBlocks : _blocks;
        _order = PickingOther ? Enumerable.Range(0, _srcBlocks.Length).ToList() : PaletteMode switch
        {
            1 => Stats().Counts.Select((c, b) => (c, b)).Where(t => t.c > 0 || t.b == 0).OrderByDescending(t => t.c).Select(t => t.b).ToList(),
            2 => _edit.Map.Layers.SelectMany(l => l.Blocks).GroupBy(b => b).OrderByDescending(g => g.Count()).Select(g => g.Key).Where(b => b < _blocks.Length).ToList(),
            _ => Enumerable.Range(0, _blocks.Length).ToList(),
        };
        int rows = Math.Max(1, (_order.Count + PickerColumns - 1) / PickerColumns), w = PickerColumns * 16, h = rows * 16;
        var pixels = new uint[w * h];
        for (int slot = 0; slot < _order.Count; slot++)
        {
            var blockPixels = shown[_order[slot]];
            int x0 = slot % PickerColumns * 16, y0 = slot / PickerColumns * 16;
            for (int y = 0; y < 16; y++) for (int x = 0; x < 16; x++) pixels[(y0 + y) * w + x0 + x] = blockPixels[y * 16 + x] == 0 ? ((x / 4 + y / 4) % 2 == 0 ? 0xFF202028u : 0xFF2A2A34u) : blockPixels[y * 16 + x];
        }
        PickerImage = BitmapSource.Create(w, h, 96, 96, PixelFormats.Bgra32, null, pixels.Select(c => (int)c).ToArray(), w * 4);
        BrushChanged();
    }

    private void UpdatePickerBox()
    {
        int slot = PickingOther ? _srcPick : _order.IndexOf(SelectedBlock);
        if (slot < 0) { PickerBoxVisible = false; return; }
        PickerBoxX = slot % PickerColumns * 16; PickerBoxY = slot / PickerColumns * 16;
        bool rect = PaletteMode == 0 || PickingOther;
        PickerBoxW = (rect ? PickingOther ? _srcW : BrushW : 1) * 16; PickerBoxH = (rect ? PickingOther ? _srcH : BrushH : 1) * 16;
        PickerBoxVisible = true;
    }

    private (int X, int Y)? _pickerDrag;

    // a press on the block picker (pixel position in it, unzoomed)
    public void PickerDown(double x, double y)
    {
        int cx = (int)x / 16, cy = (int)y / 16, slot = cy * PickerColumns + cx;
        if (cx < 0 || cx >= PickerColumns || slot < 0 || slot >= _order.Count) return;
        _pickerDrag = (cx, cy);
        if (Tool is not (L2Tool.Paint or L2Tool.Area or L2Tool.Fill)) Tool = L2Tool.Paint;
        if (PickingOther)
        {
            _srcPick = slot; _srcW = _srcH = 1; _srcBrush = new[] { _order[slot] }; _srcDragging = true;
            BrushChanged();
            return;
        }
        SetBrush(_order[slot]);
    }

    public void PickerMove(double x, double y)
    {
        if (_pickerDrag is not { } dragStart || PaletteMode != 0 && !PickingOther) return;
        int cx = Math.Clamp((int)x / 16, 0, PickerColumns - 1), cy = Math.Max(0, (int)y / 16);
        int x1 = Math.Min(dragStart.X, cx), y1 = Math.Min(dragStart.Y, cy), x2 = Math.Max(dragStart.X, cx), y2 = Math.Max(dragStart.Y, cy);
        int w = x2 - x1 + 1, h = y2 - y1 + 1;
        var brush = new int[w * h];
        for (int yy = 0; yy < h; yy++)
            for (int xx = 0; xx < w; xx++)
            {
                int slot = (y1 + yy) * PickerColumns + x1 + xx;
                brush[yy * w + xx] = slot < _order.Count ? _order[slot] : 0;
            }
        if (PickingOther)
        {
            _srcPick = y1 * PickerColumns + x1; _srcW = w; _srcH = h; _srcBrush = brush;
            BrushChanged();
            return;
        }
        _brush = brush; BrushW = w; BrushH = h;
        SelectedBlock = brush[0];
        BrushChanged();
    }

    public void PickerUp()
    {
        _pickerDrag = null;
        if (!_srcDragging) return;
        _srcDragging = false;
        if (PickingOther && _srcBrush != null) UsePicked(_srcBrush, _srcW, _srcH);
        else BrushChanged();
    }

    // blocks of other graphics

    // where the picker's blocks come from: this map's graphics (0) or another mix the game's maps use (1 and up, see
    // _sources: the mix plus a map that has it). a block picked there gets added to this map's block set (its tiles to
    // the tile set, recoloured to this map's colours) if the set has none that draws the same, and then it's a brush
    // like any other. the map keeps its own graphics and just gains the blocks it's painted with
    public ObservableCollection<string> BlockSourceChoices { get; } = new();
    private readonly List<((int Bs, int Ts, int Anim, int Ps, int Ca) Key, int Map)> _sources = new();
    [ObservableProperty] private int _blockSource;
    private uint[][] _srcBlocks = Array.Empty<uint[]>();
    private bool PickingOther => BlockSource > 0 && BlockSource <= _sources.Count && _srcBlocks.Length > 0;
    public bool HasBlockSources => BlockSourceChoices.Count > 1;
    public bool OrderEnabled => !PickingOther;
    // blocks picked in the other graphics before they're brought in: their place in the picker, size, numbers
    private int _srcPick = -1, _srcW = 1, _srcH = 1;
    private int[]? _srcBrush;
    private bool _srcDragging;

    partial void OnBlockSourceChanged(int value)
    {
        if (_loadingSettings) return;
        _srcPick = -1; _srcBrush = null;
        LoadSourceBlocks();
        DrawPicker();
        if (PickingOther) Status = $"The blocks of {BlockSourceChoices[value].Split("   ")[0]}: pick one (or drag over several) and it is added to this map's graphics, then paint with it. " +
                                   "Blocks this map has already are used as they are.";
    }

    private void LoadSourceBlocks()
    {
        _srcBlocks = Array.Empty<uint[]>();
        if (_rom != null && _edit != null && BlockSource > 0 && BlockSource <= _sources.Count)
        {
            var (mix, sourceMap) = _sources[BlockSource - 1];
            try { _srcBlocks = L2Maps.AllBlocks(_rom, new L2Maps.Map { Id = sourceMap, BlockSet = L2Maps.BlockSetFileBase + mix.Bs, TileSet = L2Maps.TileSetFileBase + mix.Ts, PaletteSet = mix.Ps, AnimIndex = mix.Anim }); }
            catch { }
        }
        OnPropertyChanged(nameof(OrderEnabled));
    }

    // uses blocks picked in other graphics as the brush, after bringing them into this map's block set
    private void UsePicked(int[] ids, int w, int h)
    {
        if (_edit == null || BlockSource < 1 || BlockSource > _sources.Count) return;
        var (mix, sourceMap) = _sources[BlockSource - 1];
        int[] mine;
        if (mix.Bs == _edit.BlockSetIndex && mix.Ts == _edit.TileSetIndex) mine = ids;   // same blocks, other colours
        else
        {
            if (WritePendingEdits() is { } why) { Status = "The map couldn't be written first: " + why; BrushChanged(); return; }
            var broughtIn = BringIn(new Clip(w, h, ids, new int[ids.Length], new byte[ids.Length], mix.Bs, mix.Ts, sourceMap, mix.Ps), "brush");
            if (broughtIn == null) { BrushChanged(); return; }
            mine = broughtIn.Ground;
        }
        _brush = mine; BrushW = w; BrushH = h;
        SelectedBlock = mine[0];
        BrushChanged();
    }

    // recent brushes, what goes with the brush

    public ObservableCollection<L2BlockChoice> RecentBrushes { get; } = new();
    private readonly Dictionary<int, List<int>> _recent = new();   // per block set

    private void RememberBrush()
    {
        if (_edit == null || BrushW * BrushH != 1) return;
        if (!_recent.TryGetValue(_edit.BlockSetIndex, out var recent)) _recent[_edit.BlockSetIndex] = recent = new();
        if (recent.Count > 0 && recent[0] == SelectedBlock) return;
        recent.Remove(SelectedBlock); recent.Insert(0, SelectedBlock);
        if (recent.Count > 12) recent.RemoveAt(recent.Count - 1);
        UpdateRecent();
    }

    private void UpdateRecent()
    {
        RecentBrushes.Clear();
        if (_edit == null || !_recent.TryGetValue(_edit.BlockSetIndex, out var recent)) return;
        foreach (int block in recent.Where(b => b < _blocks.Length)) RecentBrushes.Add(Choice(block, $"Block {block:X3}"));
    }

    private L2BlockChoice Choice(int block, string tip)
    {
        var pixels = _blocks[block].Select(c => (int)(c == 0 ? 0xFF202028u : c)).ToArray();
        return new L2BlockChoice { Block = block, Tip = tip, Image = BitmapSource.Create(16, 16, 96, 96, PixelFormats.Bgra32, null, pixels, 64) };
    }

    public ObservableCollection<L2BlockRow> NeighbourRows { get; } = new();
    [ObservableProperty] private string _neighbourNote = "";

    // how often each block is used and what sits next to it, on the game's maps with the same block set
    // (per block set and layer, worked out once)
    private sealed class BlockStats
    {
        public int[] Counts = Array.Empty<int>();
        public Dictionary<int, Dictionary<int, int>>[] Next = new Dictionary<int, Dictionary<int, int>>[4];   // right, left, below, above
    }
    private readonly Dictionary<(int Set, int Layer), BlockStats> _stats = new();

    private BlockStats Stats()
    {
        int layer = EditUpper ? 1 : 0;
        var key = (_edit!.BlockSetIndex, layer);
        if (_stats.TryGetValue(key, out var stats)) return stats;
        stats = new BlockStats { Counts = new int[_blocks.Length] };
        for (int dir = 0; dir < 4; dir++) stats.Next[dir] = new();
        for (int mapId = 2; mapId < L2Maps.Count(_rom!); mapId++)
        {
            if (_rom!.ReadByte(L2Maps.BlockSetAt(_rom!, mapId)) != _edit.BlockSetIndex) continue;
            L2Maps.Map map;
            try { map = mapId == _edit.MapId ? _edit.Map : L2Maps.Read(_rom, mapId); } catch { continue; }
            if (layer >= map.Layers.Length) continue;
            var mapLayer = map.Layers[layer];
            for (int y = 0; y < mapLayer.Height; y++)
                for (int x = 0; x < mapLayer.Width; x++)
                {
                    int block = mapLayer.Blocks[y * mapLayer.Width + x];
                    if (block >= stats.Counts.Length) continue;
                    stats.Counts[block]++;
                    void Add(int dir, int nx, int ny)
                    {
                        if (nx < 0 || ny < 0 || nx >= mapLayer.Width || ny >= mapLayer.Height) return;
                        int neighbour = mapLayer.Blocks[ny * mapLayer.Width + nx];
                        if (!stats.Next[dir].TryGetValue(block, out var counts)) stats.Next[dir][block] = counts = new();
                        counts[neighbour] = counts.GetValueOrDefault(neighbour) + 1;
                    }
                    Add(0, x + 1, y); Add(1, x - 1, y); Add(2, x, y + 1); Add(3, x, y - 1);
                }
        }
        _stats[key] = stats;
        return stats;
    }

    private void UpdateNeighbours()
    {
        NeighbourRows.Clear();
        if (_rom == null || _edit == null || _blocks.Length == 0 || SelectedBlock >= _blocks.Length) { NeighbourNote = ""; return; }
        var stats = Stats();
        string[] names = { "Right", "Left", "Below", "Above" };
        for (int dir = 0; dir < 4; dir++)
        {
            if (!stats.Next[dir].TryGetValue(SelectedBlock, out var counts)) continue;
            var top = counts.Where(kv => kv.Key < _blocks.Length).OrderByDescending(kv => kv.Value).Take(8).ToList();
            if (top.Count == 0) continue;
            NeighbourRows.Add(new L2BlockRow { Label = names[dir], Blocks = top.Select(kv => Choice(kv.Key, $"Block {kv.Key:X3}: {kv.Value} times {names[dir].ToLowerInvariant()} of {SelectedBlock:X3} on the game's maps")).ToList() });
        }
        NeighbourNote = NeighbourRows.Count == 0 ? $"The game's maps with these graphics don't use block {SelectedBlock:X3} on the {(EditUpper ? "upper" : "ground")} layer."
                                                  : $"What the game's maps put next to block {SelectedBlock:X3} ({(EditUpper ? "upper" : "ground")} layer), most often first:";
    }

    // pieces and presets

    [RelayCommand]
    private void PiecesFromMaps()
    {
        if (_rom == null || _edit == null) return;
        var window = new L2PiecesWindow(_rom, _edit.MapId, _edit.BlockSetIndex, _edit.TileSetIndex, NameOf) { Owner = System.Windows.Application.Current.MainWindow };
        if (window.ShowDialog() != true || window.Picked == null) return;
        var clip = window.Picked;
        if (clip.BlockSet != _edit.BlockSetIndex || clip.TileSet != _edit.TileSetIndex)
        {
            if (WritePendingEdits() is { } why) { Status = "The map couldn't be written first: " + why; return; }
            clip = BringIn(clip);
            if (clip == null) return;
            string note = Status;
            Tool = L2Tool.Paint;
            StartStamp(clip);
            Status = note;   // say what was added, not just "click to place"
            return;
        }
        Tool = L2Tool.Paint;
        StartStamp(clip);
    }

    // puts a piece from other graphics (a preset kept on another map) into this map's block set. each block becomes a
    // block of the set that draws the same, or one added to it (its tiles go into the tile set if it has none of that
    // shape), same as chests and door pieces from other maps (L2ChestLooks.Translate). the piece keeps its own walls.
    // written into the ROM right away as one Edit > Undo step. returns null (nothing written) if this map's sets can't take it
    public Clip? BringIn(Clip piece, string what = "piece")
    {
        if (_rom == null || _edit == null || _main == null) return null;
        bool Has(int m, bool tiles) => _rom.ReadByte(L2Maps.BlockSetAt(_rom, m)) == piece.BlockSet && (!tiles || _rom.ReadByte(L2Maps.TileSetAt(_rom, m)) == piece.TileSet);
        int sourceMap = piece.SourceMap >= 2 && piece.SourceMap < L2Maps.Count(_rom) && Has(piece.SourceMap, true) ? piece.SourceMap
            : Enumerable.Range(2, L2Maps.Count(_rom) - 2).Where(m => Has(m, true)).Concat(Enumerable.Range(2, L2Maps.Count(_rom) - 2).Where(m => Has(m, false))).DefaultIfEmpty(-1).First();
        if (sourceMap < 0) { Status = $"No map uses block set {piece.BlockSet} any more, so the {what}'s blocks can't be read."; return null; }
        int paletteSet = piece.PaletteSet >= 0 ? piece.PaletteSet : _rom.ReadByte(L2Maps.PaletteSetAt(_rom, sourceMap));
        int cellCount = piece.W * piece.H, blocksBefore = L2Maps.BlockCount(_rom, _edit.MapId);
        int TileCount() => L2Rom.ReadFile(_rom, L2Maps.TileSetFileBase + _edit.TileSetIndex).Length / 32;
        int tilesBefore = TileCount();
        var ground = new int[cellCount]; var upper = new int[cellCount]; var flags = new byte[cellCount];
        try
        {
            // every block of the piece in one go, so the tile set and block set only get written once
            var ids = piece.Ground.Concat(piece.Upper).Where(b => b != 0).Distinct().ToList();
            var list = ids.Select(b => (b, (byte)0)).ToArray();
            var look = new L2ChestLooks.Look(sourceMap, 0, 0, -1, list, Array.Empty<(int, byte)>(), piece.BlockSet, piece.TileSet, paletteSet);
            var prepared = L2ChestLooks.Prepare(_rom, look, _edit);
            var newBlockOf = ids.Select((b, i) => (b, prepared.Closed[i].Block)).ToDictionary(x => x.b, x => x.Block);
            newBlockOf[0] = 0;   // block 0 = "nothing here" on either layer, whatever it draws
            L2Rom.AllOrNothing(_rom, () => { prepared.Write(); return 0; });
            for (int i = 0; i < cellCount; i++) { ground[i] = newBlockOf[piece.Ground[i]]; upper[i] = newBlockOf[piece.Upper[i]]; flags[i] = (byte)(piece.Flags[i] & 0xFC); }
        }
        catch (Exception ex) { Status = $"This {what} can't be used on this map: " + ex.Message.Replace("the chest's", $"the {what}'s").Replace("chest", what); return null; }
        int added = L2Maps.BlockCount(_rom, _edit.MapId) - blocksBefore, tiles = TileCount() - tilesBefore;
        string tileText = tiles > 0 ? $" and {tiles} tile{(tiles == 1 ? "" : "s")} to tile set {_edit.TileSetIndex}" : "";
        if (added > 0)
        {
            _main.NotifyRomModified($"Lufia II: {added} block{(added == 1 ? "" : "s")} added to block set {_edit.BlockSetIndex}{tileText} for a {what} from block set {piece.BlockSet}");
            ReloadBlocks();
        }
        Status = (added > 0 ? $"{added} block{(added == 1 ? "" : "s")} added to block set {_edit.BlockSetIndex}{tileText} for this {what} (recoloured to this map's closest colours; Edit, Undo takes them out). "
                            : $"Every block of the {what} was in this map's block set already. ")
            + (what == "brush" ? "Paint with it." : "Click to place it; right-click or Esc stops.");
        return new Clip(piece.W, piece.H, ground, upper, flags, _edit.BlockSetIndex, _edit.TileSetIndex, _edit.MapId, _edit.PaletteSetIndex);
    }

    [RelayCommand]
    private void SaveAreaAsPreset()
    {
        if (_edit == null || AreaRect is not { } area) { Status = "Drag an area with the Area tool first."; return; }
        string? name = L2PiecesWindow.AskName(System.Windows.Application.Current.MainWindow);
        if (string.IsNullOrWhiteSpace(name)) return;
        L2Presets.Add(name.Trim(), Cut(area) with { SourceMap = _edit.MapId, PaletteSet = _edit.PaletteSetIndex });
        Status = $"Kept \"{name.Trim()}\": \"Pieces and ★ presets...\" (under the blocks) places it again on any map (on other graphics its blocks are added to that map's block set).";
    }

    // blocks

    [RelayCommand]
    private void EditBlocks()
    {
        if (_rom == null || _main == null || _edit == null) return;
        if (WritePendingEdits() is { } why) { Status = "The map couldn't be written first: " + why; return; }
        var window = new L2BlockEditorWindow(_rom, _main, _edit.ForDrawing, SelectedBlock) { Owner = System.Windows.Application.Current.MainWindow };
        window.ShowDialog();
        if (window.Changed) { _stats.Clear(); ReloadBlocks(); Refresh(); }
    }
}
