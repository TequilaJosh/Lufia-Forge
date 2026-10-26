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
        : $"Brush: block {SelectedBlock:X3}, painted on the {(EditUpper ? "upper" : "ground")} layer.";

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
        int w = BrushW * 16, h = BrushH * 16;
        var px = new uint[w * h];
        for (int by = 0; by < BrushH; by++)
            for (int bx = 0; bx < BrushW; bx++)
            {
                int b = _brush[by * BrushW + bx];
                if (b < 0 || b >= _blocks.Length) continue;
                for (int y = 0; y < 16; y++) for (int x = 0; x < 16; x++) px[(by * 16 + y) * w + bx * 16 + x] = _blocks[b][y * 16 + x];
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
        _order = PaletteMode switch
        {
            1 => Stats().Counts.Select((c, b) => (c, b)).Where(t => t.c > 0 || t.b == 0).OrderByDescending(t => t.c).Select(t => t.b).ToList(),
            2 => _edit.Map.Layers.SelectMany(l => l.Blocks).GroupBy(b => b).OrderByDescending(g => g.Count()).Select(g => g.Key).Where(b => b < _blocks.Length).ToList(),
            _ => Enumerable.Range(0, _blocks.Length).ToList(),
        };
        int rows = Math.Max(1, (_order.Count + PickerColumns - 1) / PickerColumns), w = PickerColumns * 16, h = rows * 16;
        var px = new uint[w * h];
        for (int k = 0; k < _order.Count; k++)
        {
            var bp = _blocks[_order[k]];
            int x0 = k % PickerColumns * 16, y0 = k / PickerColumns * 16;
            for (int y = 0; y < 16; y++) for (int x = 0; x < 16; x++) px[(y0 + y) * w + x0 + x] = bp[y * 16 + x] == 0 ? ((x / 4 + y / 4) % 2 == 0 ? 0xFF202028u : 0xFF2A2A34u) : bp[y * 16 + x];
        }
        PickerImage = BitmapSource.Create(w, h, 96, 96, PixelFormats.Bgra32, null, px.Select(c => (int)c).ToArray(), w * 4);
        BrushChanged();
    }

    private void UpdatePickerBox()
    {
        int k = _order.IndexOf(SelectedBlock);
        if (k < 0) { PickerBoxVisible = false; return; }
        PickerBoxX = k % PickerColumns * 16; PickerBoxY = k / PickerColumns * 16;
        bool rect = PaletteMode == 0;
        PickerBoxW = (rect ? BrushW : 1) * 16; PickerBoxH = (rect ? BrushH : 1) * 16;
        PickerBoxVisible = true;
    }

    private (int X, int Y)? _pickerDrag;

    /// <summary>A press on the block picker (pixel position in it, unzoomed).</summary>
    public void PickerDown(double x, double y)
    {
        int cx = (int)x / 16, cy = (int)y / 16, k = cy * PickerColumns + cx;
        if (cx < 0 || cx >= PickerColumns || k < 0 || k >= _order.Count) return;
        _pickerDrag = (cx, cy);
        SetBrush(_order[k]);
        if (Tool is not (L2Tool.Paint or L2Tool.Area)) Tool = L2Tool.Paint;
    }

    public void PickerMove(double x, double y)
    {
        if (_pickerDrag is not { } s || PaletteMode != 0) return;
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
        _brush = brush; BrushW = w; BrushH = h;
        SelectedBlock = brush[0];
        BrushChanged();
    }

    public void PickerUp() => _pickerDrag = null;

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
        for (int m = 2; m < L2Maps.Count; m++)
        {
            if (_rom!.ReadByte(L2Maps.BlockSetList + m) != _edit.BlockSetIndex) continue;
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
        Tool = L2Tool.Paint;
        StartStamp(w.Picked);
    }

    [RelayCommand]
    private void SaveAreaAsPreset()
    {
        if (_edit == null || AreaRect is not { } r) { Status = "Drag an area with the Area tool first."; return; }
        string? name = L2PiecesWindow.AskName(System.Windows.Application.Current.MainWindow);
        if (string.IsNullOrWhiteSpace(name)) return;
        L2Presets.Add(name.Trim(), Cut(r));
        Status = $"Kept \"{name.Trim()}\": Pieces... (My presets) places it again on any map with block set {_edit.BlockSetIndex}.";
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
