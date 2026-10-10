using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using LufiaForge.Core.Maps;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace LufiaForge.Modules.MapEditor;

/// <summary>
/// Areas of blocks: select a rectangle (Area tool), copy / cut / fill / clear it, and stamp copies (paste) anywhere,
/// also on other maps with the same tileset. A rectangle dragged in the block palette is a multi-block brush,
/// stamped the same way. Every stamp, fill and cut can be undone.
/// </summary>
public partial class MapEditorViewModel
{
    /// <summary>A rectangle of blocks. Raw = the map's own words (copied from a map), else metatile numbers (from the palette).</summary>
    public sealed record BlockPattern(int W, int H, ushort[] Tiles, bool Raw, int TilesetKey, string Source);

    /// <summary>Shared by every map (and the Map Editor windows), like a clipboard.</summary>
    private static BlockPattern? _clipboard;

    private (int X1, int Y1, int X2, int Y2)? _area;
    private (int X, int Y)? _areaStart;
    private BlockPattern? _stamp;

    [ObservableProperty] private bool _areaVisible;
    [ObservableProperty] private double _areaX;
    [ObservableProperty] private double _areaY;
    [ObservableProperty] private double _areaW;
    [ObservableProperty] private double _areaH;
    [ObservableProperty] private ImageSource? _stampImage;
    [ObservableProperty] private bool _stampVisible;
    [ObservableProperty] private double _stampX;
    [ObservableProperty] private double _stampY;
    [ObservableProperty] private double _brushBoxW = 16;
    [ObservableProperty] private double _brushBoxH = 16;

    public bool IsAreaTool { get => Tool == MapTool.Area; set { if (value) Tool = MapTool.Area; } }
    public bool IsStamping => _stamp != null;
    public bool HasArea => _area != null;
    public bool HasClipboard => _clipboard != null;

    private int TilesetKey => _map == null ? -2 : _map.IsWorld ? -1 : _map.Tileset;

    // ── area selection ──

    private void ShowArea()
    {
        if (_area is not var (x1, y1, x2, y2)) { AreaVisible = false; OnAreaChanged(); return; }
        AreaX = x1 * 16; AreaY = y1 * 16; AreaW = (x2 - x1 + 1) * 16; AreaH = (y2 - y1 + 1) * 16;
        AreaVisible = true;
        OnAreaChanged();
    }

    private void OnAreaChanged()
    {
        OnPropertyChanged(nameof(HasArea));
        CopyAreaCommand.NotifyCanExecuteChanged(); CutAreaCommand.NotifyCanExecuteChanged();
        FillAreaCommand.NotifyCanExecuteChanged(); ClearAreaCommand.NotifyCanExecuteChanged();
    }

    private void AreaDown(int x, int y)
    {
        CancelStamp();
        _areaStart = (x, y);
        _area = (x, y, x, y);
        ShowArea();
    }

    private void AreaDrag(int x, int y)
    {
        if (_map == null || _areaStart is not var (sx, sy)) return;
        x = Math.Clamp(x, 0, _map.Width - 1); y = Math.Clamp(y, 0, _map.Height - 1);
        _area = (Math.Min(sx, x), Math.Min(sy, y), Math.Max(sx, x), Math.Max(sy, y));
        ShowArea();
    }

    private void AreaUp()
    {
        if (_areaStart == null) return;
        _areaStart = null;
        if (_area is var (x1, y1, x2, y2))
            Status = $"Area {x2 - x1 + 1} x {y2 - y1 + 1} blocks at ({x1}, {y1}). Ctrl+C copy, Ctrl+X cut, Ctrl+V paste, Del clear (to the brush block), or Fill with the brush.";
    }

    public void ClearAreaSelection() { _area = null; _areaStart = null; ShowArea(); }

    private BlockPattern? AreaPattern()
    {
        if (_map == null || _area is not var (x1, y1, x2, y2)) return null;
        int w = x2 - x1 + 1, h = y2 - y1 + 1;
        var t = new ushort[w * h];
        for (int y = 0; y < h; y++) for (int x = 0; x < w; x++) t[y * w + x] = _map.GetTile(x1 + x, y1 + y);
        return new BlockPattern(w, h, t, true, TilesetKey, $"map {_map.MapId:X2}");
    }

    [RelayCommand(CanExecute = nameof(HasArea))]
    private void CopyArea()
    {
        if (AreaPattern() is not { } p) return;
        _clipboard = p;
        OnPropertyChanged(nameof(HasClipboard)); PasteCommand.NotifyCanExecuteChanged();
        Status = $"Copied {p.W} x {p.H} blocks. Ctrl+V (or Paste) to stamp them, here or on another map with the same tileset.";
    }

    [RelayCommand(CanExecute = nameof(HasArea))]
    private void CutArea()
    {
        CopyArea();
        FillArea(Brush, "Cut");
    }

    [RelayCommand(CanExecute = nameof(HasArea))]
    private void FillArea() => FillArea(Brush, "Filled");

    [RelayCommand(CanExecute = nameof(HasArea))]
    private void ClearArea() => FillArea(Brush, "Cleared");

    private void FillArea(int metatile, string verb)
    {
        if (_map == null || _area is not var (x1, y1, x2, y2)) return;
        var stroke = new List<(int x, int y, ushort before)>();
        for (int y = y1; y <= y2; y++)
            for (int x = x1; x <= x2; x++)
            {
                ushort before = _map.GetTile(x, y);
                _map.SetTile(x, y, metatile);
                if (_map.GetTile(x, y) != before) { stroke.Add((x, y, before)); RedrawBlock(x, y); }
            }
        if (stroke.Count > 0) { _undo.Push(stroke); HasUnappliedChanges = true; }
        Status = $"{verb} {x2 - x1 + 1} x {y2 - y1 + 1} blocks with block {metatile:X3} ({stroke.Count} changed). Undo takes it back.";
    }

    // ── stamping (paste / multi-block brush) ──

    [RelayCommand(CanExecute = nameof(HasClipboard))]
    private void Paste()
    {
        if (_clipboard is not { } p || _map == null) return;
        if (p.TilesetKey != TilesetKey &&
            MessageBox.Show($"These blocks were copied from {p.Source}, which uses a different tileset, so they will look different here. Paste anyway?",
                "Different tileset", MessageBoxButton.YesNo) != MessageBoxResult.Yes) return;
        StartStamp(p);
        Status = $"Pasting {p.W} x {p.H} blocks: click to stamp (as many times as you like), Esc or right-click to stop.";
    }

    private void StartStamp(BlockPattern p)
    {
        if (_tileset == null) return;
        _stamp = p;
        var px = new uint[p.W * 16 * p.H * 16];
        for (int by = 0; by < p.H; by++)
            for (int bx = 0; bx < p.W; bx++)
            {
                ushort t = p.Tiles[by * p.W + bx];
                var block = _tileset.MetatilePixels(p.Raw ? t : (ushort)(t & 0x3FF));
                for (int y = 0; y < 16; y++)
                    for (int x = 0; x < 16; x++)
                        px[(by * 16 + y) * p.W * 16 + bx * 16 + x] = block[y * 16 + x];
            }
        var bmp = BitmapSource.Create(p.W * 16, p.H * 16, 96, 96, PixelFormats.Bgra32, null, px, p.W * 16 * 4);
        bmp.Freeze();
        StampImage = bmp;
        StampVisible = false;
        OnPropertyChanged(nameof(IsStamping));
    }

    public void CancelStamp()
    {
        if (_stamp == null) return;
        _stamp = null;
        StampVisible = false;
        StampImage = null;
        OnPropertyChanged(nameof(IsStamping));
        BrushBoxW = BrushBoxH = 16;
        OnPropertyChanged(nameof(PaletteBoxX)); OnPropertyChanged(nameof(PaletteBoxY));
    }

    private void StampHover(int x, int y)
    {
        if (_stamp == null) return;
        StampX = x * 16; StampY = y * 16; StampVisible = true;
    }

    private void StampAt(int x0, int y0)
    {
        if (_map == null || _stamp is not { } p) return;
        var stroke = new List<(int x, int y, ushort before)>();
        for (int by = 0; by < p.H; by++)
            for (int bx = 0; bx < p.W; bx++)
            {
                int x = x0 + bx, y = y0 + by;
                if (x < 0 || y < 0 || x >= _map.Width || y >= _map.Height) continue;
                ushort before = _map.GetTile(x, y), t = p.Tiles[by * p.W + bx];
                if (p.Raw && _map.IsWorld == (p.TilesetKey == -1)) _map.Layer[y * _map.Width + x] = t;
                else _map.SetTile(x, y, t & 0x3FF);
                if (_map.GetTile(x, y) != before) { stroke.Add((x, y, before)); RedrawBlock(x, y); }
            }
        if (stroke.Count > 0) { _undo.Push(stroke); HasUnappliedChanges = true; }
        Status = $"Stamped {p.W} x {p.H} blocks at ({x0}, {y0}). Click again to stamp more, Esc to stop, Undo to take it back.";
    }

    // ── multi-block brush from the palette ──

    private (int X, int Y)? _paletteStart;

    public void PaletteDown(double px, double py)
    {
        if (_tileset == null) return;
        _paletteStart = ((int)(px / 16), (int)(py / 16));
        PaletteDrag(px, py);
    }

    public void PaletteDrag(double px, double py)
    {
        if (_paletteStart is not var (sx, sy)) return;
        int x = Math.Clamp((int)(px / 16), 0, 15), y = Math.Max(0, (int)(py / 16));
        BrushBoxW = (Math.Abs(x - sx) + 1) * 16; BrushBoxH = (Math.Abs(y - sy) + 1) * 16;
        _paletteRect = (Math.Min(sx, x), Math.Min(sy, y), Math.Max(sx, x), Math.Max(sy, y));
        OnPropertyChanged(nameof(PaletteBoxX)); OnPropertyChanged(nameof(PaletteBoxY));
    }

    private (int X1, int Y1, int X2, int Y2) _paletteRect;
    public double PaletteBoxX => _paletteStart != null || BrushBoxW > 16 || BrushBoxH > 16 ? _paletteRect.X1 * 16 : BrushBoxX;
    public double PaletteBoxY => _paletteStart != null || BrushBoxW > 16 || BrushBoxH > 16 ? _paletteRect.Y1 * 16 : BrushBoxY;

    public void PaletteUp()
    {
        if (_paletteStart == null || _tileset == null) return;
        _paletteStart = null;
        var (x1, y1, x2, y2) = _paletteRect;
        if (x1 == x2 && y1 == y2)
        {
            BrushBoxW = BrushBoxH = 16;
            CancelStamp();
            PickBrushFromPalette(x1 * 16 + 1, y1 * 16 + 1);
            OnPropertyChanged(nameof(PaletteBoxX)); OnPropertyChanged(nameof(PaletteBoxY));
            return;
        }
        int w = x2 - x1 + 1, h = y2 - y1 + 1;
        var t = new ushort[w * h];
        for (int y = 0; y < h; y++)
            for (int x = 0; x < w; x++)
                t[y * w + x] = (ushort)Math.Min((y1 + y) * 16 + x1 + x, _tileset.MetatileCount - 1);
        Brush = t[0];
        if (Tool != MapTool.Paint) Tool = MapTool.Paint;
        var (bw, bh) = (BrushBoxW, BrushBoxH);
        StartStamp(new BlockPattern(w, h, t, false, TilesetKey, "the block palette"));
        BrushBoxW = bw; BrushBoxH = bh;
        Status = $"Multi-block brush {w} x {h}: click on the map to stamp it, Esc or right-click to stop.";
    }
}
