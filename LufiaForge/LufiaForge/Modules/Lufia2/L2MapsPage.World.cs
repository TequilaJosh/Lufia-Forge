using System.Windows.Media;
using System.Windows.Media.Imaging;
using CommunityToolkit.Mvvm.ComponentModel;
using LufiaForge.Core;
using LufiaForge.Core.Lufia2;

namespace LufiaForge.Modules.Lufia2;

/// <summary>
/// The world map and the sea floor in the Maps tab: 128 x 128 big blocks (32 x 32 pixels, shown at half size), painted,
/// picked, copied and pasted like a town's blocks; on the world map also the random-battle zones (file 0x193, one byte
/// per 2 x 2 big blocks: which battle group, see Encounters). Written to the map files like a town.
/// </summary>
public partial class L2MapsPage
{
    private sealed class WorldEdit
    {
        public int Map;
        public L2WorldMap.Parts Parts = L2WorldMap.World;
        public byte[] File = Array.Empty<byte>();
        public int[] Cells = Array.Empty<int>();
        public int[] OrigCells = Array.Empty<int>();
        public byte[] Zones = Array.Empty<byte>(), OrigZones = Array.Empty<byte>();
        public byte[] Tiles = Array.Empty<byte>();
        public uint[] Pal = Array.Empty<uint>();
        public int BlockCount;
        public readonly Dictionary<int, uint[]> Pictures = new();
        public bool Changed => !Cells.AsSpan().SequenceEqual(OrigCells) || !Zones.AsSpan().SequenceEqual(OrigZones);
    }
    private WorldEdit? _world;
    private readonly List<(int[] Cells, byte[] Zones, string What)> _worldHistory = new(), _worldRedo = new();

    public bool IsWorldMap => _world != null;
    [ObservableProperty] private bool _showZones;
    [ObservableProperty] private BitmapSource? _zoneImage;
    [ObservableProperty] private double _zoneImageSize;
    [ObservableProperty] private string _zoneBrushText = "01";
    partial void OnShowZonesChanged(bool value) { DrawZones(); OnPropertyChanged(nameof(ToolHint)); }

    private const int Cell = 16;   // a big block shown at half size

    private void LoadWorld(int map)
    {
        _world = null;
        if (_rom == null || map > 1) { OnPropertyChanged(nameof(IsWorldMap)); return; }
        try
        {
            var w = new WorldEdit { Map = map, Parts = L2WorldMap.Of(map) };
            w.File = L2Rom.ReadFile(_rom, w.Parts.MapFile);
            w.Cells = L2WorldMap.Cells(w.File); w.OrigCells = (int[])w.Cells.Clone();
            w.Zones = map == 0 ? L2Rom.ReadFile(_rom, 0x193) : Array.Empty<byte>(); w.OrigZones = (byte[])w.Zones.Clone();
            w.Tiles = L2Rom.ReadFile(_rom, w.Parts.TileFile);
            w.Pal = L2WorldMap.Colours(_rom, w.Parts);
            w.BlockCount = L2WorldMap.BigBlockCount(w.Parts, w.Cells);
            _world = w;
        }
        catch (Exception ex) { Status = "Couldn't read it: " + ex.Message; }
        _worldHistory.Clear(); _worldRedo.Clear();
        OnPropertyChanged(nameof(IsWorldMap));
    }

    private uint[] Big(int b)
    {
        var w = _world!;
        if (!w.Pictures.TryGetValue(b, out var px)) w.Pictures[b] = px = L2WorldMap.BigBlockHalf(_rom!, w.Parts, w.Tiles, w.Pal, b);
        return px;
    }

    private void DrawWorld()
    {
        var w = _world!;
        int n = L2WorldMap.Size * Cell;
        var px = new uint[n * n];
        for (int cy = 0; cy < L2WorldMap.Size; cy++)
            for (int cx = 0; cx < L2WorldMap.Size; cx++)
            {
                var b = Big(w.Cells[cy * L2WorldMap.Size + cx]);
                for (int y = 0; y < Cell; y++) Array.Copy(b, y * Cell, px, (cy * Cell + y) * n + cx * Cell, Cell);
            }
        MapImage = BitmapSource.Create(n, n, 96, 96, PixelFormats.Bgra32, null, px.Select(c => (int)(c == 0 ? 0xFF000000u : c)).ToArray(), n * 4);
        MapSummary = w.Map == 0 ? "The world map: 128 x 128 big blocks (32 x 32 pixels in the game, shown at half size); battle zones are 2 x 2 big blocks."
                                : "The sea floor: 128 x 128 big blocks (32 x 32 pixels in the game, shown at half size).";
        DrawZones();
    }

    private static readonly uint[] ZoneColours =
    {
        0x00000000, 0x90FF4040, 0x9040C040, 0x904080FF, 0x90FFC040, 0x90C040FF, 0x9040FFFF, 0x90FF80C0, 0x90A0A040,
        0x9040A0A0, 0x90A040A0, 0x90FF8040, 0x9080FF80, 0x908080FF, 0x90FFFF80, 0x90C08040, 0x9080C0FF, 0x90FFFFFF,
    };

    private void DrawZones()
    {
        if (_world == null || _world.Zones.Length < 4096 || !ShowZones) { ZoneImage = null; return; }
        var px = new int[64 * 64];
        for (int i = 0; i < px.Length; i++) { int z = _world.Zones[i]; px[i] = (int)(z == 0 ? 0u : ZoneColours[z % ZoneColours.Length] | (z >= ZoneColours.Length ? 0x40000000u : 0)); }
        ZoneImage = BitmapSource.Create(64, 64, 96, 96, PixelFormats.Bgra32, null, px, 64 * 4);
        ZoneImageSize = L2WorldMap.Size * Cell;
    }

    private void DrawWorldPicker()
    {
        var w = _world!;
        _order = PaletteMode switch
        {
            2 => w.Cells.GroupBy(c => c).OrderByDescending(g => g.Count()).Select(g => g.Key).ToList(),
            _ => Enumerable.Range(0, w.BlockCount).ToList(),
        };
        int rows = Math.Max(1, (_order.Count + PickerColumns - 1) / PickerColumns), pw = PickerColumns * Cell, ph = rows * Cell;
        var px = new uint[pw * ph];
        for (int k = 0; k < _order.Count; k++)
        {
            var bp = Big(_order[k]);
            int x0 = k % PickerColumns * Cell, y0 = k / PickerColumns * Cell;
            for (int y = 0; y < Cell; y++) Array.Copy(bp, y * Cell, px, (y0 + y) * pw + x0, Cell);
        }
        PickerImage = BitmapSource.Create(pw, ph, 96, 96, PixelFormats.Bgra32, null, px.Select(c => (int)(c == 0 ? 0xFF000000u : c)).ToArray(), pw * 4);
        _blocks = Array.Empty<uint[]>();
        BrushPreviewImage = WorldBrushPicture();
        UpdatePickerBox();
        NeighbourRows.Clear(); NeighbourNote = "";
        RecentBrushes.Clear();
    }

    private BitmapSource? WorldBrushPicture()
    {
        if (_world == null) return null;
        int w = BrushW * Cell, h = BrushH * Cell;
        var px = new uint[w * h];
        for (int by = 0; by < BrushH; by++)
            for (int bx = 0; bx < BrushW; bx++)
            {
                var b = Big(_brush[by * BrushW + bx]);
                for (int y = 0; y < Cell; y++) Array.Copy(b, y * Cell, px, (by * Cell + y) * w + bx * Cell, Cell);
            }
        return BitmapSource.Create(w, h, 96, 96, PixelFormats.Bgra32, null, px.Select(c => (int)c).ToArray(), w * 4);
    }

    // ---------------------------------------------------------------- editing

    private void WorldRecord(string what)
    {
        var w = _world!;
        _worldHistory.Add(((int[])w.Cells.Clone(), (byte[])w.Zones.Clone(), what));
        if (_worldHistory.Count > 100) _worldHistory.RemoveAt(0);
        _worldRedo.Clear();
        WorldUndoState();
    }

    private void WorldUndoState()
    {
        MapUndoText = _worldHistory.Count > 0 ? "Undo: " + _worldHistory[^1].What + " (Ctrl+Z)" : "Nothing to undo";
        MapRedoText = _worldRedo.Count > 0 ? "Redo: " + _worldRedo[^1].What + " (Ctrl+Y)" : "Nothing to redo";
        HasUnappliedChanges = _world?.Changed == true;
        UndoCommand.NotifyCanExecuteChanged(); RedoCommand.NotifyCanExecuteChanged();
    }

    private bool WorldUndo(bool redo)
    {
        var from = redo ? _worldRedo : _worldHistory;
        var to = redo ? _worldHistory : _worldRedo;
        if (_world == null || from.Count == 0) return false;
        var (cells, zones, what) = from[^1]; from.RemoveAt(from.Count - 1);
        to.Add(((int[])_world.Cells.Clone(), (byte[])_world.Zones.Clone(), what));
        _world.Cells = cells; _world.Zones = zones;
        DrawWorld(); WorldUndoState();
        Status = (redo ? "Redone: " : "Undone: ") + what + ".";
        return true;
    }

    private bool _worldDragging;
    private int _wLastX = -1, _wLastY = -1;

    private void WorldDown(double px, double py, bool right, bool ctrl)
    {
        var w = _world!;
        int cx = (int)(px / Cell), cy = (int)(py / Cell);
        if (cx < 0 || cy < 0 || cx >= L2WorldMap.Size || cy >= L2WorldMap.Size) return;
        _wLastX = cx; _wLastY = cy;
        if (ShowZones && w.Zones.Length >= 4096)
        {
            if (right || ctrl) { ZoneBrushText = w.Zones[cy / 2 * 64 + cx / 2].ToString("X2"); Status = $"Zone {ZoneBrushText} picked."; return; }
            WorldRecord("painting battle zones"); _worldDragging = true; PaintZone(cx, cy); return;
        }
        if (Stamping) { if (right) { StopStamp(); return; } WorldRecord("placing copied blocks"); StampWorld(cx, cy); return; }
        if (right || ctrl || Tool == L2Tool.Pick) { SetWorldBrush(w.Cells[cy * L2WorldMap.Size + cx]); Status = $"Big block {SelectedBlock:X3} picked from ({cx},{cy})."; return; }
        switch (Tool)
        {
            case L2Tool.Paint:
                WorldRecord("painting"); _worldDragging = true; PaintWorld(cx, cy); break;
            case L2Tool.Area:
                _areaStart = (cx, cy); AreaRect = (cx, cy, cx + 1, cy + 1); _worldDragging = true; break;
            default:
                Status = "The world map has blocks and battle zones: use Paint, Pick or Area (exits and characters of the world map are in its events).";
                break;
        }
    }

    private void WorldMove(double px, double py)
    {
        var w = _world!;
        int cx = (int)(px / Cell), cy = (int)(py / Cell);
        bool inside = cx >= 0 && cy >= 0 && cx < L2WorldMap.Size && cy < L2WorldMap.Size;
        if (!inside) { HoverText = ""; BrushPreviewVisible = false; return; }
        int b = w.Cells[cy * L2WorldMap.Size + cx];
        int small = _rom!.ReadUInt16Le(w.Parts.BigBlocks + b * 8);
        string[] terrain = { "plain", "desert", "forest", "water", "mountain" };
        int t = L2WorldMap.Terrain(_rom, small) >> 4;
        HoverText = $"({cx},{cy})  big block {b:X3}, {(t < terrain.Length ? terrain[t] : "terrain " + t)}" + (w.Zones.Length >= 4096 ? $"; battle zone {w.Zones[cy / 2 * 64 + cx / 2]:X2}" : "");
        if (Tool == L2Tool.Paint && !Stamping && !ShowZones) { BrushPreviewX = cx * Cell; BrushPreviewY = cy * Cell; BrushPreviewW = BrushW * Cell; BrushPreviewH = BrushH * Cell; BrushPreviewVisible = BrushPreviewImage != null; }
        else BrushPreviewVisible = false;
        if (Stamping) { StampX = cx * Cell; StampY = cy * Cell; }
        if (!_worldDragging || cx == _wLastX && cy == _wLastY) return;
        _wLastX = cx; _wLastY = cy;
        if (ShowZones && w.Zones.Length >= 4096) PaintZone(cx, cy);
        else if (Tool == L2Tool.Paint) PaintWorld(cx, cy);
        else if (Tool == L2Tool.Area && _areaStart is { } s) AreaRect = (Math.Min(s.X, cx), Math.Min(s.Y, cy), Math.Max(s.X, cx) + 1, Math.Max(s.Y, cy) + 1);
    }

    private void WorldUp() { _worldDragging = false; _areaStart = null; WorldUndoState(); }

    private void PaintWorld(int cx, int cy)
    {
        var w = _world!;
        for (int by = 0; by < BrushH; by++)
            for (int bx = 0; bx < BrushW; bx++)
                if (cx + bx < L2WorldMap.Size && cy + by < L2WorldMap.Size) w.Cells[(cy + by) * L2WorldMap.Size + cx + bx] = _brush[by * BrushW + bx];
        DrawWorld(); WorldUndoState();
    }

    private void PaintZone(int cx, int cy)
    {
        if (!int.TryParse(ZoneBrushText.Trim(), System.Globalization.NumberStyles.HexNumber, null, out int z) || z is < 0 or > 255) { Status = "Type the zone to paint (hex, 00 = no battles)."; return; }
        _world!.Zones[cy / 2 * 64 + cx / 2] = (byte)z;
        DrawZones(); WorldUndoState();
    }

    private void SetWorldBrush(int b) { BrushW = 1; BrushH = 1; _brush = new[] { b }; SelectedBlock = b; BrushPreviewImage = WorldBrushPicture(); UpdatePickerBox(); }

    private Clip WorldCut((int X1, int Y1, int X2, int Y2) r)
    {
        int w = r.X2 - r.X1, h = r.Y2 - r.Y1;
        var g = new int[w * h];
        for (int y = 0; y < h; y++) for (int x = 0; x < w; x++) g[y * w + x] = _world!.Cells[(r.Y1 + y) * L2WorldMap.Size + r.X1 + x];
        return new Clip(w, h, g, new int[w * h], new byte[w * h], -1 - _world!.Map, -1);
    }

    private void StampWorld(int cx, int cy)
    {
        if (_clip is not { } c) return;
        for (int y = 0; y < c.H; y++)
            for (int x = 0; x < c.W; x++)
                if (cx + x < L2WorldMap.Size && cy + y < L2WorldMap.Size) _world!.Cells[(cy + y) * L2WorldMap.Size + cx + x] = c.Ground[y * c.W + x];
        DrawWorld(); WorldUndoState();
    }

    private string? WriteWorld()
    {
        var w = _world!;
        if (!w.Changed) return null;
        try
        {
            if (!w.Cells.AsSpan().SequenceEqual(w.OrigCells)) L2Rom.WriteFile(_rom!, w.Parts.MapFile, L2WorldMap.WithCells(w.File, w.Cells));
            if (!w.Zones.AsSpan().SequenceEqual(w.OrigZones)) L2Rom.WriteFile(_rom!, 0x193, w.Zones);
            _main!.NotifyRomModified(w.Map == 0 ? "Lufia II: world map" : "Lufia II: sea floor");
            LoadWorld(w.Map);
            Status = "Written.";
            return null;
        }
        catch (Exception ex) { return ex.Message; }
    }
}
