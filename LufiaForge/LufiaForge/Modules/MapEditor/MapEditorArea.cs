using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using LufiaForge.Core.Maps;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace LufiaForge.Modules.MapEditor;

// areas of blocks: select a rectangle (Area tool), copy / cut / fill / clear it, and stamp copies (paste) anywhere,
// other maps with the same tileset too. a rectangle dragged in the block palette is a multi-block brush and gets
// stamped the same way. every stamp, fill and cut can be undone
public partial class MapEditorViewModel
{
    // a rectangle of blocks to stamp. Raw = the map's own words (copied from a map), otherwise metatile numbers (from the palette)
    // SourceThreshold = the composite threshold of the map a raw copy came from (-1 = none). a composite is threshold +
    // its pair's number in the tileset's table, and the threshold is the map's own (Make room raises it), so a pasted
    // composite gets moved to the target's threshold
    public sealed record BlockPattern(int W, int H, ushort[] Tiles, bool Raw, int TilesetKey, string Source, int SourceMap = -1, int SourceThreshold = -1);

    // shared by every map (and the Map Editor windows), like a clipboard
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

    // area selection

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
        SaveAreaAsPresetCommand.NotifyCanExecuteChanged();
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
        if (_map == null || _areaStart is not var (startX, startY)) return;
        x = Math.Clamp(x, 0, _map.Width - 1); y = Math.Clamp(y, 0, _map.Height - 1);
        _area = (Math.Min(startX, x), Math.Min(startY, y), Math.Max(startX, x), Math.Max(startY, y));
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

    // keeps the area inside the map as it is now (corners too). one picked before a resize (or an Undo of one) could
    // reach past the edge, and then Copy / Fill / Cut read and wrote the next row or past the end
    private void ClampArea()
    {
        if (_map == null || _area is not var (x1, y1, x2, y2)) return;
        int mapWidth = _map.Width, mapHeight = _map.Height;
        if (x1 >= mapWidth || y1 >= mapHeight) { ClearAreaSelection(); return; }
        _area = (x1, y1, Math.Min(x2, mapWidth - 1), Math.Min(y2, mapHeight - 1));
        if (_area != (x1, y1, x2, y2)) ShowArea();
    }

    private BlockPattern? AreaPattern()
    {
        ClampArea();
        if (_map == null || _area is not var (x1, y1, x2, y2)) return null;
        int width = x2 - x1 + 1, height = y2 - y1 + 1;
        var tiles = new ushort[width * height];
        for (int y = 0; y < height; y++) for (int x = 0; x < width; x++) tiles[y * width + x] = _map.GetTile(x1 + x, y1 + y);
        return new BlockPattern(width, height, tiles, true, TilesetKey, $"map {_map.MapId:X2}", _map.MapId, _map.CompositeThreshold);
    }

    [RelayCommand(CanExecute = nameof(HasArea))]
    private void CopyArea()
    {
        if (AreaPattern() is not { } pattern) return;
        _clipboard = pattern;
        OnPropertyChanged(nameof(HasClipboard)); PasteCommand.NotifyCanExecuteChanged();
        Status = $"Copied {pattern.W} x {pattern.H} blocks. Ctrl+V (or Paste) to stamp them, here or on another map (on other graphics their blocks are added to that map's tileset).";
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
        ClampArea();
        if (_map == null || _area is not var (x1, y1, x2, y2)) return;
        var stroke = new List<(int x, int y, ushort before)>();
        for (int y = y1; y <= y2; y++)
            for (int x = x1; x <= x2; x++)
            {
                ushort before = _map.GetTile(x, y);
                _map.SetTile(x, y, metatile);
                if (_map.GetTile(x, y) != before) { stroke.Add((x, y, before)); RedrawBlock(x, y); }
            }
        if (stroke.Count > 0) MarkEdited();
        Status = $"{verb} {x2 - x1 + 1} x {y2 - y1 + 1} blocks with block {metatile:X3} ({stroke.Count} changed). Undo takes it back.";
    }

    // saves the selected area as one of the user's presets (Buildings... > My presets)
    [RelayCommand(CanExecute = nameof(HasArea))]
    private void SaveAreaAsPreset()
    {
        ClampArea();
        if (_rom == null || _map == null || _area is not var (x1, y1, x2, y2)) return;
        if (_map.IsWorld) { Status = "Presets are made from town and dungeon maps (the world map has its own blocks)."; return; }
        var window = new SavePresetWindow(_rom, _map, x1, y1, x2 - x1 + 1, y2 - y1 + 1) { Owner = Application.Current?.MainWindow };
        if (window.ShowDialog() != true || window.Result is not { } preset) return;
        try
        {
            CustomPresets.Add(preset);
            Status = $"Preset \"{preset.Name}\" saved ({preset.W} x {preset.H} blocks): Buildings... > My presets places it.";
        }
        catch (Exception ex) { Status = "The preset couldn't be saved: " + ex.Message; }
    }

    // stamping (paste / multi-block brush)

    [RelayCommand(CanExecute = nameof(HasClipboard))]
    private void Paste()
    {
        if (_clipboard is not { } pattern || _map == null) return;
        if (pattern.TilesetKey != TilesetKey && pattern.TilesetKey >= 0 && TilesetKey >= 0 && pattern.SourceMap >= 0 && _rom != null)
        {
            // copied on another tileset: bring its blocks into this map's tileset first, like From does
            LufiaMap? sourceMap = null;
            try { sourceMap = LufiaMap.Load(_rom, pattern.SourceMap); } catch { }
            if (sourceMap != null && !sourceMap.IsWorld && sourceMap.Tileset == pattern.TilesetKey)
            {
                var ids = pattern.Tiles.Where(t => t != Prefabs.Keep).Select(t => t & 0x3FF).Distinct().ToList();
                if (BringBlocks(sourceMap, ids) is not { } result) return;
                var broughtBlock = ids.Select((b, i) => (b, result.Blocks[i])).ToDictionary(x => x.b, x => x.Item2);
                var tiles = pattern.Tiles.Select(t => t == Prefabs.Keep ? t : (ushort)broughtBlock[t & 0x3FF]).ToArray();
                StartStamp(new BlockPattern(pattern.W, pattern.H, tiles, false, TilesetKey, pattern.Source, _map!.MapId));
                Status = BroughtText(result) + $"Pasting {pattern.W} x {pattern.H} blocks: click to stamp (as many times as you like), Esc or right-click to stop.";
                return;
            }
        }
        if (pattern.TilesetKey != TilesetKey &&
            MessageBox.Show($"These blocks were copied from {pattern.Source}, which uses a different tileset, so they will look different here. Paste anyway?",
                "Different tileset", MessageBoxButton.YesNo) != MessageBoxResult.Yes) return;
        StartStamp(pattern);
        Status = $"Pasting {pattern.W} x {pattern.H} blocks: click to stamp (as many times as you like), Esc or right-click to stop.";
    }

    private void StartStamp(BlockPattern pattern)
    {
        if (_tileset == null) return;
        _stamp = pattern;
        var pixels = new uint[pattern.W * 16 * pattern.H * 16];
        for (int by = 0; by < pattern.H; by++)
            for (int bx = 0; bx < pattern.W; bx++)
            {
                ushort tile = pattern.Tiles[by * pattern.W + bx];
                if (tile == Prefabs.Keep) continue;   // see-through, the map keeps its own block there
                var block = _tileset.MetatilePixels(pattern.Raw ? tile : (ushort)(tile & 0x3FF));
                for (int y = 0; y < 16; y++)
                    for (int x = 0; x < 16; x++)
                        pixels[(by * 16 + y) * pattern.W * 16 + bx * 16 + x] = block[y * 16 + x];
            }
        var bmp = BitmapSource.Create(pattern.W * 16, pattern.H * 16, 96, 96, PixelFormats.Bgra32, null, pixels, pattern.W * 16 * 4);
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
        if (_map == null || _stamp is not { } pattern) return;
        var stroke = new List<(int x, int y, ushort before)>();
        for (int by = 0; by < pattern.H; by++)
            for (int bx = 0; bx < pattern.W; bx++)
            {
                int x = x0 + bx, y = y0 + by;
                if (x < 0 || y < 0 || x >= _map.Width || y >= _map.Height) continue;
                ushort before = _map.GetTile(x, y), tile = pattern.Tiles[by * pattern.W + bx];
                if (tile == Prefabs.Keep) continue;
                // a composite copied with a different threshold (another map, or this one before Make room) becomes the
                // same pair under this map's threshold. one this map can't have (no composites, past 3FF) isn't stamped
                if (pattern.Raw && pattern.SourceThreshold >= 0 && (tile & 0x3FF) >= pattern.SourceThreshold && pattern.SourceThreshold != _map.CompositeThreshold)
                {
                    int newBlock = _map.CompositeThreshold < 0 ? -1 : _map.CompositeThreshold + ((tile & 0x3FF) - pattern.SourceThreshold);
                    if (newBlock < 0 || newBlock > 0x3FF) continue;
                    tile = (ushort)(tile & ~0x3FF | newBlock);
                }
                if (pattern.Raw && _map.IsWorld == (pattern.TilesetKey == -1)) _map.Layer[y * _map.Width + x] = tile;
                else _map.SetTile(x, y, tile & 0x3FF);
                if (_map.GetTile(x, y) != before) { stroke.Add((x, y, before)); RedrawBlock(x, y); }
            }
        if (stroke.Count > 0) MarkEdited();
        Status = $"Stamped {pattern.W} x {pattern.H} blocks at ({x0}, {y0}). Click again to stamp more, Esc to stop, Undo to take it back.";
    }

    // multi-block brush from the palette

    private (int X, int Y)? _paletteStart;

    public void PaletteDown(double px, double py)
    {
        if (_tileset == null) return;
        _paletteStart = ((int)(px / 16), (int)(py / 16));
        PaletteDrag(px, py);
    }

    public void PaletteDrag(double px, double py)
    {
        if (_paletteStart is not var (startX, startY)) return;
        int x = Math.Clamp((int)(px / 16), 0, 15), y = Math.Max(0, (int)(py / 16));
        BrushBoxW = (Math.Abs(x - startX) + 1) * 16; BrushBoxH = (Math.Abs(y - startY) + 1) * 16;
        _paletteRect = (Math.Min(startX, x), Math.Min(startY, y), Math.Max(startX, x), Math.Max(startY, y));
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
        if (_srcTileset != null) { PickFromOther(x1, y1, x2, y2); OnPropertyChanged(nameof(PaletteBoxX)); OnPropertyChanged(nameof(PaletteBoxY)); return; }
        if (x1 == x2 && y1 == y2 || _paletteOrder != null)   // sorted palette = one block only (its neighbours there aren't neighbours in the tileset)
        {
            BrushBoxW = BrushBoxH = 16;
            CancelStamp();
            PickBrushFromPalette(x1 * 16 + 1, y1 * 16 + 1);
            OnPropertyChanged(nameof(PaletteBoxX)); OnPropertyChanged(nameof(PaletteBoxY));
            return;
        }
        int width = x2 - x1 + 1, height = y2 - y1 + 1;
        var tiles = new ushort[width * height];
        for (int y = 0; y < height; y++)
            for (int x = 0; x < width; x++)
                tiles[y * width + x] = (ushort)Math.Min((y1 + y) * 16 + x1 + x, _tileset.MetatileCount - 1);
        Brush = tiles[0];
        if (Tool != MapTool.Paint) Tool = MapTool.Paint;
        var (boxW, boxH) = (BrushBoxW, BrushBoxH);
        StartStamp(new BlockPattern(width, height, tiles, false, TilesetKey, "the block palette"));
        BrushBoxW = boxW; BrushBoxH = boxH;
        Status = $"Multi-block brush {width} x {height}: click on the map to stamp it, Esc or right-click to stop.";
    }
}
