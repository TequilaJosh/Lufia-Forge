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
    // ---------------------------------------------------------------- the brush

    [ObservableProperty] private int _selectedBlock = 1;
    [ObservableProperty] private int _brushW = 1, _brushH = 1;
    private int[] _brush = { 1 };
    [ObservableProperty] private BitmapSource? _brushPreviewImage;

    public string BrushText => BrushW * BrushH > 1
        ? $"Brush: {BrushW} x {BrushH} blocks from {SelectedBlock:X3}, painted on the {(EditUpper ? "upper" : "ground")} layer."
        : $"Brush: block {SelectedBlock:X3}{(BrushSize > 1 ? $", {BrushSize} x {BrushSize}" : "")}, painted on the {(EditUpper ? "upper" : "ground")} layer.";

    /// <summary>How big a one-block brush paints: 1, 3, 5 or 7 blocks square, around the block under the mouse.</summary>
    [ObservableProperty] private int _brushSize = 1;
    public static IReadOnlyList<int> BrushSizes { get; } = new[] { 1, 3, 5, 7 };
    partial void OnBrushSizeChanged(int value) => BrushChanged();

    /// <summary>The brush as it is painted: a single block grows to <see cref="BrushSize"/> square, centred on the block
    /// under the mouse (Off: the top-left's offset from it); a brush of several blocks is painted as it was picked.</summary>
    private (int W, int H, int[] Cells, int Off) Painted()
    {
        if (BrushW * BrushH == 1 && BrushSize > 1) return (BrushSize, BrushSize, Enumerable.Repeat(_brush[0], BrushSize * BrushSize).ToArray(), -(BrushSize / 2));
        return (BrushW, BrushH, _brush, 0);
    }

    partial void OnSelectedBlockChanged(int value) { OnPropertyChanged(nameof(BrushText)); }

    /// <summary>One block as the brush.</summary>
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
        var (bw, bh, cells, _) = Painted();
        return Picture(_blocks, bw, bh, cells);
    }

    private static BitmapSource Picture(uint[][] blocks, int bw, int bh, int[] cells)
    {
        int w = bw * 16, h = bh * 16;
        var px = new uint[w * h];
        for (int by = 0; by < bh; by++)
            for (int bx = 0; bx < bw; bx++)
            {
                int b = cells[by * bw + bx];
                if (b < 0 || b >= blocks.Length) continue;
                for (int y = 0; y < 16; y++) for (int x = 0; x < 16; x++) px[(by * 16 + y) * w + bx * 16 + x] = blocks[b][y * 16 + x];
            }
        return BitmapSource.Create(w, h, 96, 96, PixelFormats.Bgra32, null, px.Select(c => (int)c).ToArray(), w * 4);
    }

    [RelayCommand]
    private void PickBlockChoice(int block) => SetBrush(block);

    // ---------------------------------------------------------------- the block picker

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
        var px = new uint[w * h];
        for (int k = 0; k < _order.Count; k++)
        {
            var bp = shown[_order[k]];
            int x0 = k % PickerColumns * 16, y0 = k / PickerColumns * 16;
            for (int y = 0; y < 16; y++) for (int x = 0; x < 16; x++) px[(y0 + y) * w + x0 + x] = bp[y * 16 + x] == 0 ? ((x / 4 + y / 4) % 2 == 0 ? 0xFF202028u : 0xFF2A2A34u) : bp[y * 16 + x];
        }
        PickerImage = BitmapSource.Create(w, h, 96, 96, PixelFormats.Bgra32, null, px.Select(c => (int)c).ToArray(), w * 4);
        BrushChanged();
    }

    private void UpdatePickerBox()
    {
        int k = PickingOther ? _srcPick : _order.IndexOf(SelectedBlock);
        if (k < 0) { PickerBoxVisible = false; return; }
        PickerBoxX = k % PickerColumns * 16; PickerBoxY = k / PickerColumns * 16;
        bool rect = PaletteMode == 0 || PickingOther;
        PickerBoxW = (rect ? PickingOther ? _srcW : BrushW : 1) * 16; PickerBoxH = (rect ? PickingOther ? _srcH : BrushH : 1) * 16;
        PickerBoxVisible = true;
    }

    private (int X, int Y)? _pickerDrag;

    /// <summary>A press on the block picker (pixel position in it, unzoomed).</summary>
    public void PickerDown(double x, double y)
    {
        int cx = (int)x / 16, cy = (int)y / 16, k = cy * PickerColumns + cx;
        if (cx < 0 || cx >= PickerColumns || k < 0 || k >= _order.Count) return;
        _pickerDrag = (cx, cy);
        if (Tool is not (L2Tool.Paint or L2Tool.Area or L2Tool.Fill)) Tool = L2Tool.Paint;
        if (PickingOther)
        {
            _srcPick = k; _srcW = _srcH = 1; _srcBrush = new[] { _order[k] }; _srcDragging = true;
            BrushChanged();
            return;
        }
        SetBrush(_order[k]);
    }

    public void PickerMove(double x, double y)
    {
        if (_pickerDrag is not { } s || PaletteMode != 0 && !PickingOther) return;
        int cx = Math.Clamp((int)x / 16, 0, PickerColumns - 1), cy = Math.Max(0, (int)y / 16);
        int x1 = Math.Min(s.X, cx), y1 = Math.Min(s.Y, cy), x2 = Math.Max(s.X, cx), y2 = Math.Max(s.Y, cy);
        int w = x2 - x1 + 1, h = y2 - y1 + 1;
        var brush = new int[w * h];
        for (int yy = 0; yy < h; yy++)
            for (int xx = 0; xx < w; xx++)
            {
                int k = (y1 + yy) * PickerColumns + x1 + xx;
                brush[yy * w + xx] = k < _order.Count ? _order[k] : 0;
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

    // ---------------------------------------------------------------- blocks of other graphics

    /// <summary>Where the picker's blocks come from: this map's graphics (0), or another mix the game's maps use (1 up,
    /// <see cref="_sources"/>: the mix and a map that has it). A block picked there is added to this map's block set
    /// (and its tiles to the tile set, recoloured to this map's colours) when the set has none that draws the same, then
    /// it is the brush as any other: the map keeps its own graphics and gains the blocks it is painted with.</summary>
    public ObservableCollection<string> BlockSourceChoices { get; } = new();
    private readonly List<((int Bs, int Ts, int Anim, int Ps, int Ca) Key, int Map)> _sources = new();
    [ObservableProperty] private int _blockSource;
    private uint[][] _srcBlocks = Array.Empty<uint[]>();
    private bool PickingOther => BlockSource > 0 && BlockSource <= _sources.Count && _srcBlocks.Length > 0;
    public bool HasBlockSources => BlockSourceChoices.Count > 1;
    public bool OrderEnabled => !PickingOther;
    // the blocks picked in the other graphics before they are brought in: their place in the picker, size, numbers
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
            var (k, m) = _sources[BlockSource - 1];
            try { _srcBlocks = L2Maps.AllBlocks(_rom, new L2Maps.Map { Id = m, BlockSet = L2Maps.BlockSetFileBase + k.Bs, TileSet = L2Maps.TileSetFileBase + k.Ts, PaletteSet = k.Ps, AnimIndex = k.Anim }); }
            catch { }
        }
        OnPropertyChanged(nameof(OrderEnabled));
    }

    /// <summary>Blocks picked in other graphics as the brush: brought into this map's block set first.</summary>
    private void UsePicked(int[] ids, int w, int h)
    {
        if (_edit == null || BlockSource < 1 || BlockSource > _sources.Count) return;
        var (k, m) = _sources[BlockSource - 1];
        int[] mine;
        if (k.Bs == _edit.BlockSetIndex && k.Ts == _edit.TileSetIndex) mine = ids;   // the same blocks, other colours
        else
        {
            if (WritePendingEdits() is { } why) { Status = "The map couldn't be written first: " + why; BrushChanged(); return; }
            var c = BringIn(new Clip(w, h, ids, new int[ids.Length], new byte[ids.Length], k.Bs, k.Ts, m, k.Ps), "brush");
            if (c == null) { BrushChanged(); return; }
            mine = c.Ground;
        }
        _brush = mine; BrushW = w; BrushH = h;
        SelectedBlock = mine[0];
        BrushChanged();
    }

    // ---------------------------------------------------------------- recent brushes, what goes with the brush

    public ObservableCollection<L2BlockChoice> RecentBrushes { get; } = new();
    private readonly Dictionary<int, List<int>> _recent = new();   // per block set

    private void RememberBrush()
    {
        if (_edit == null || BrushW * BrushH != 1) return;
        if (!_recent.TryGetValue(_edit.BlockSetIndex, out var l)) _recent[_edit.BlockSetIndex] = l = new();
        if (l.Count > 0 && l[0] == SelectedBlock) return;
        l.Remove(SelectedBlock); l.Insert(0, SelectedBlock);
        if (l.Count > 12) l.RemoveAt(l.Count - 1);
        UpdateRecent();
    }

    private void UpdateRecent()
    {
        RecentBrushes.Clear();
        if (_edit == null || !_recent.TryGetValue(_edit.BlockSetIndex, out var l)) return;
        foreach (int b in l.Where(b => b < _blocks.Length)) RecentBrushes.Add(Choice(b, $"Block {b:X3}"));
    }

    private L2BlockChoice Choice(int b, string tip)
    {
        var px = _blocks[b].Select(c => (int)(c == 0 ? 0xFF202028u : c)).ToArray();
        return new L2BlockChoice { Block = b, Tip = tip, Image = BitmapSource.Create(16, 16, 96, 96, PixelFormats.Bgra32, null, px, 64) };
    }

    public ObservableCollection<L2BlockRow> NeighbourRows { get; } = new();
    [ObservableProperty] private string _neighbourNote = "";

    /// <summary>How often each block is used, and what lies next to it, on the game's maps with the same block set
    /// (per block set and layer, worked out once).</summary>
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
        if (_stats.TryGetValue(key, out var st)) return st;
        st = new BlockStats { Counts = new int[_blocks.Length] };
        for (int d = 0; d < 4; d++) st.Next[d] = new();
        for (int m = 2; m < L2Maps.Count(_rom!); m++)
        {
            if (_rom!.ReadByte(L2Maps.BlockSetAt(_rom!, m)) != _edit.BlockSetIndex) continue;
            L2Maps.Map map;
            try { map = m == _edit.MapId ? _edit.Map : L2Maps.Read(_rom, m); } catch { continue; }
            if (layer >= map.Layers.Length) continue;
            var L = map.Layers[layer];
            for (int y = 0; y < L.Height; y++)
                for (int x = 0; x < L.Width; x++)
                {
                    int b = L.Blocks[y * L.Width + x];
                    if (b >= st.Counts.Length) continue;
                    st.Counts[b]++;
                    void Add(int d, int nx, int ny)
                    {
                        if (nx < 0 || ny < 0 || nx >= L.Width || ny >= L.Height) return;
                        int n = L.Blocks[ny * L.Width + nx];
                        if (!st.Next[d].TryGetValue(b, out var c)) st.Next[d][b] = c = new();
                        c[n] = c.GetValueOrDefault(n) + 1;
                    }
                    Add(0, x + 1, y); Add(1, x - 1, y); Add(2, x, y + 1); Add(3, x, y - 1);
                }
        }
        _stats[key] = st;
        return st;
    }

    private void UpdateNeighbours()
    {
        NeighbourRows.Clear();
        if (_rom == null || _edit == null || _blocks.Length == 0 || SelectedBlock >= _blocks.Length) { NeighbourNote = ""; return; }
        var st = Stats();
        string[] names = { "Right", "Left", "Below", "Above" };
        for (int d = 0; d < 4; d++)
        {
            if (!st.Next[d].TryGetValue(SelectedBlock, out var c)) continue;
            var top = c.Where(kv => kv.Key < _blocks.Length).OrderByDescending(kv => kv.Value).Take(8).ToList();
            if (top.Count == 0) continue;
            NeighbourRows.Add(new L2BlockRow { Label = names[d], Blocks = top.Select(kv => Choice(kv.Key, $"Block {kv.Key:X3}: {kv.Value} times {names[d].ToLowerInvariant()} of {SelectedBlock:X3} on the game's maps")).ToList() });
        }
        NeighbourNote = NeighbourRows.Count == 0 ? $"The game's maps with these graphics don't use block {SelectedBlock:X3} on the {(EditUpper ? "upper" : "ground")} layer."
                                                  : $"What the game's maps put next to block {SelectedBlock:X3} ({(EditUpper ? "upper" : "ground")} layer), most often first:";
    }

    // ---------------------------------------------------------------- pieces and presets

    [RelayCommand]
    private void PiecesFromMaps()
    {
        if (_rom == null || _edit == null) return;
        var w = new L2PiecesWindow(_rom, _edit.MapId, _edit.BlockSetIndex, _edit.TileSetIndex, NameOf) { Owner = System.Windows.Application.Current.MainWindow };
        if (w.ShowDialog() != true || w.Picked == null) return;
        var clip = w.Picked;
        if (clip.BlockSet != _edit.BlockSetIndex || clip.TileSet != _edit.TileSetIndex)
        {
            if (WritePendingEdits() is { } why) { Status = "The map couldn't be written first: " + why; return; }
            clip = BringIn(clip);
            if (clip == null) return;
        }
        Tool = L2Tool.Paint;
        StartStamp(clip);
    }

    /// <summary>A piece from other graphics (a preset kept on another map) in this map's block set: each block is a block of
    /// the set that draws the same, or one added to it (its tiles into the tile set when it has none of their shape), as
    /// chests and door pieces from other maps are (<see cref="L2ChestLooks.Translate"/>). The piece keeps its own walls.
    /// Written into the ROM at once, one Edit > Undo step; null (and nothing written) when this map's sets can't take it.</summary>
    public Clip? BringIn(Clip c, string what = "piece")
    {
        if (_rom == null || _edit == null || _main == null) return null;
        bool Has(int m, bool tiles) => _rom.ReadByte(L2Maps.BlockSetAt(_rom, m)) == c.BlockSet && (!tiles || _rom.ReadByte(L2Maps.TileSetAt(_rom, m)) == c.TileSet);
        int src = c.SourceMap >= 2 && c.SourceMap < L2Maps.Count(_rom) && Has(c.SourceMap, true) ? c.SourceMap
            : Enumerable.Range(2, L2Maps.Count(_rom) - 2).Where(m => Has(m, true)).Concat(Enumerable.Range(2, L2Maps.Count(_rom) - 2).Where(m => Has(m, false))).DefaultIfEmpty(-1).First();
        if (src < 0) { Status = $"No map uses block set {c.BlockSet} any more, so the {what}'s blocks can't be read."; return null; }
        int ps = c.PaletteSet >= 0 ? c.PaletteSet : _rom.ReadByte(L2Maps.PaletteSetAt(_rom, src));
        int n = c.W * c.H, before = L2Maps.BlockCount(_rom, _edit.MapId);
        int TileCount() => L2Rom.ReadFile(_rom, L2Maps.TileSetFileBase + _edit.TileSetIndex).Length / 32;
        int tilesBefore = TileCount();
        var g = new int[n]; var u = new int[n]; var f = new byte[n];
        var done = new Dictionary<(int, int, int), (int G, int U)>();
        try
        {
            L2Rom.AllOrNothing(_rom, () =>
            {
                for (int i = 0; i < n; i++)
                {
                    var key = (c.Ground[i], c.Upper[i], c.Flags[i] & 0xFC);
                    if (!done.TryGetValue(key, out var t))
                    {
                        var cell = new (int Block, byte Flags)[] { (c.Ground[i], (byte)0), (c.Upper[i], (byte)(c.Flags[i] & 0xFC)) };
                        var look = new L2ChestLooks.Look(src, 0, 0, 0x33, cell, cell, c.BlockSet, c.TileSet, ps);
                        var (closed, _, _) = L2ChestLooks.Translate(_rom, look, _edit);
                        // block 0 is "nothing here" on either layer, whatever it draws
                        t = (c.Ground[i] == 0 ? 0 : closed[0].Block, c.Upper[i] == 0 || closed.Length < 2 ? 0 : closed[1].Block);
                        done[key] = t;
                    }
                    g[i] = t.G; u[i] = t.U; f[i] = (byte)(c.Flags[i] & 0xFC);
                }
                return 0;
            });
        }
        catch (Exception ex) { Status = $"This {what} can't be used on this map: " + ex.Message.Replace("the chest's", $"the {what}'s").Replace("chest", what); return null; }
        int added = L2Maps.BlockCount(_rom, _edit.MapId) - before, tiles = TileCount() - tilesBefore;
        string tileText = tiles > 0 ? $" and {tiles} tile{(tiles == 1 ? "" : "s")} to tile set {_edit.TileSetIndex}" : "";
        if (added > 0)
        {
            _main.NotifyRomModified($"Lufia II: {added} block{(added == 1 ? "" : "s")} added to block set {_edit.BlockSetIndex}{tileText} for a {what} from block set {c.BlockSet}");
            ReloadBlocks();
        }
        Status = (added > 0 ? $"{added} block{(added == 1 ? "" : "s")} added to block set {_edit.BlockSetIndex}{tileText} for this {what} (recoloured to this map's closest colours; Edit, Undo takes them out). "
                            : $"Every block of the {what} was in this map's block set already. ")
            + (what == "brush" ? "Paint with it." : "Click to place it; right-click or Esc stops.");
        return new Clip(c.W, c.H, g, u, f, _edit.BlockSetIndex, _edit.TileSetIndex, _edit.MapId, _edit.PaletteSetIndex);
    }

    [RelayCommand]
    private void SaveAreaAsPreset()
    {
        if (_edit == null || AreaRect is not { } r) { Status = "Drag an area with the Area tool first."; return; }
        string? name = L2PiecesWindow.AskName(System.Windows.Application.Current.MainWindow);
        if (string.IsNullOrWhiteSpace(name)) return;
        L2Presets.Add(name.Trim(), Cut(r) with { SourceMap = _edit.MapId, PaletteSet = _edit.PaletteSetIndex });
        Status = $"Kept \"{name.Trim()}\": \"Pieces and ★ presets...\" (under the blocks) places it again on any map (on other graphics its blocks are added to that map's block set).";
    }

    // ---------------------------------------------------------------- blocks

    [RelayCommand]
    private void EditBlocks()
    {
        if (_rom == null || _main == null || _edit == null) return;
        if (WritePendingEdits() is { } why) { Status = "The map couldn't be written first: " + why; return; }
        var w = new L2BlockEditorWindow(_rom, _main, _edit.ForDrawing, SelectedBlock) { Owner = System.Windows.Application.Current.MainWindow };
        w.ShowDialog();
        if (w.Changed) { _stats.Clear(); ReloadBlocks(); Refresh(); }
    }
}
