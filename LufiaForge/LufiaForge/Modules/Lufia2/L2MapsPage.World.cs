using System.Collections.ObjectModel;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using LufiaForge.Core;
using LufiaForge.Core.Lufia2;
using Brush = System.Windows.Media.Brush;
using Brushes = System.Windows.Media.Brushes;
using Color = System.Windows.Media.Color;

namespace LufiaForge.Modules.Lufia2;

/// <summary>A battle zone in the world map's zone list: its colour, its groups and their monsters.</summary>
public sealed class L2ZoneRow
{
    public int Zone { get; init; }
    public string Title { get; init; } = "";
    public string Groups { get; init; } = "";
    public Brush Swatch { get; init; } = Brushes.Transparent;
    public bool IsBrush { get; init; }
}

/// <summary>
/// The world map and the sea floor in the Maps tab: 128 x 128 big blocks (32 x 32 pixels, shown at full size: one game
/// pixel per screen pixel at 100%), painted, picked, copied and pasted like a town's blocks; on the world map also the
/// random-battle zones (file 0x193, one byte per 2 x 2 big blocks, <see cref="L2Encounters.Zone"/>) and the entrances
/// (map 00's exits, edited like a town's: <see cref="L2MapEdit"/> keeps its info block). Written to the map files like a town.
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
        public readonly Dictionary<int, uint[]> Pictures = new(), Halves = new();
        public WriteableBitmap? Bitmap;
        public bool Changed => !Cells.AsSpan().SequenceEqual(OrigCells) || !Zones.AsSpan().SequenceEqual(OrigZones);
    }
    private WorldEdit? _world;
    private readonly List<(int[] Cells, byte[] Zones, L2MapEdit.State? Info, string What)> _worldHistory = new(), _worldRedo = new();

    public bool IsWorldMap => _world != null;
    public bool HasZones => _world?.Zones.Length >= 4096;
    [ObservableProperty] private bool _showZones;
    [ObservableProperty] private BitmapSource? _zoneImage;
    [ObservableProperty] private double _zoneImageSize;
    [ObservableProperty] private string _zoneBrushText = "01";
    public ObservableCollection<L2ZoneRow> ZoneRows { get; } = new();
    partial void OnShowZonesChanged(bool value) { DrawZones(); FillZoneRows(); OnPropertyChanged(nameof(ToolHint)); OnPropertyChanged(nameof(ZonePanelVisible)); }
    partial void OnZoneBrushTextChanged(string value) => FillZoneRows();
    public bool ZonePanelVisible => ShowZones && HasZones;

    /// <summary>Asks the Lufia II tab to show a battle zone on the Encounters page.</summary>
    public event Action<int>? OpenZoneRequested;

    private const int Cell = 32;    // a big block on the map, as in the game
    private const int PCell = 16;   // a big block in the block list (half size)

    private void LoadWorld(int map)
    {
        _world = null;
        if (_rom == null || map > 1) { OnPropertyChanged(nameof(IsWorldMap)); OnPropertyChanged(nameof(HasZones)); OnPropertyChanged(nameof(ZonePanelVisible)); return; }
        try
        {
            var w = new WorldEdit { Map = map, Parts = L2WorldMap.Of(map) };
            w.File = L2Rom.ReadFile(_rom, w.Parts.MapFile);
            w.Cells = L2WorldMap.Cells(w.File); w.OrigCells = (int[])w.Cells.Clone();
            w.Zones = map == 0 ? L2Rom.ReadFile(_rom, L2Encounters.ZoneFile) : Array.Empty<byte>(); w.OrigZones = (byte[])w.Zones.Clone();
            w.Tiles = L2Rom.ReadFile(_rom, w.Parts.TileFile);
            w.Pal = L2WorldMap.Colours(_rom, w.Parts);
            w.BlockCount = L2WorldMap.BigBlockCount(w.Parts, w.Cells);
            _world = w;
        }
        catch (Exception ex) { Status = "Couldn't read it: " + ex.Message; }
        _worldHistory.Clear(); _worldRedo.Clear();
        OnPropertyChanged(nameof(IsWorldMap)); OnPropertyChanged(nameof(HasZones)); OnPropertyChanged(nameof(ZonePanelVisible));
        FillZoneRows();
    }

    /// <summary>A big block at full size (32 x 32).</summary>
    private uint[] Big(int b)
    {
        var w = _world!;
        if (!w.Pictures.TryGetValue(b, out var px))
        {
            px = L2WorldMap.BigBlock(_rom!, w.Parts, w.Tiles, w.Pal, b);
            for (int i = 0; i < px.Length; i++) if (px[i] == 0) px[i] = 0xFF000000u;
            w.Pictures[b] = px;
        }
        return px;
    }

    /// <summary>A big block at half size (16 x 16), for the block list.</summary>
    private uint[] BigHalf(int b)
    {
        var w = _world!;
        if (!w.Halves.TryGetValue(b, out var px)) w.Halves[b] = px = L2WorldMap.Half(Big(b));
        return px;
    }

    private const int WorldPixels = L2WorldMap.Size * Cell;   // 4096

    private void DrawWorld()
    {
        var w = _world!;
        w.Bitmap ??= new WriteableBitmap(WorldPixels, WorldPixels, 96, 96, PixelFormats.Bgra32, null);
        DrawWorldCells(0, 0, L2WorldMap.Size, L2WorldMap.Size);
        if (MapImage != w.Bitmap) MapImage = w.Bitmap;
        MapSummary = w.Map == 0 ? "The world map: 128 x 128 big blocks of 32 x 32 pixels, at full size; battle zones are 2 x 2 big blocks; entrances are in 16-pixel blocks."
                                : "The sea floor: 128 x 128 big blocks of 32 x 32 pixels, at full size.";
        DrawZones();
    }

    /// <summary>Redraws the big blocks of a rectangle (cells, x2 / y2 not included), a row of big blocks at a time.</summary>
    private void DrawWorldCells(int x1, int y1, int x2, int y2)
    {
        var w = _world!;
        if (w.Bitmap == null) { DrawWorld(); return; }
        x1 = Math.Clamp(x1, 0, L2WorldMap.Size); x2 = Math.Clamp(x2, 0, L2WorldMap.Size);
        y1 = Math.Clamp(y1, 0, L2WorldMap.Size); y2 = Math.Clamp(y2, 0, L2WorldMap.Size);
        if (x2 <= x1 || y2 <= y1) return;
        int pw = (x2 - x1) * Cell;
        var strip = new int[pw * Cell];
        for (int cy = y1; cy < y2; cy++)
        {
            for (int cx = x1; cx < x2; cx++)
            {
                var b = Big(w.Cells[cy * L2WorldMap.Size + cx]);
                for (int y = 0; y < Cell; y++)
                    for (int x = 0; x < Cell; x++) strip[y * pw + (cx - x1) * Cell + x] = (int)b[y * Cell + x];
            }
            w.Bitmap.WritePixels(new System.Windows.Int32Rect(x1 * Cell, cy * Cell, pw, Cell), strip, pw * 4, 0);
        }
    }

    /// <summary>The colour of a zone (semi-transparent), the same in the map and the list.</summary>
    public static uint ZoneColour(int z) => z == 0 ? 0u : ZoneColours[(z - 1) % ZoneColours.Length];

    private static readonly uint[] ZoneColours =
    {
        0x90FF4040, 0x9040C040, 0x904080FF, 0x90FFC040, 0x90C040FF, 0x9040FFFF, 0x90FF80C0, 0x90A0A040,
        0x9040A0A0, 0x90A040A0, 0x90FF8040, 0x9080FF80, 0x908080FF, 0x90FFFF80, 0x90C08040, 0x9080C0FF, 0x90FFFFFF,
    };

    private void DrawZones()
    {
        if (_world == null || !HasZones || !ShowZones) { ZoneImage = null; ZoneImageSize = 0; return; }   // (its size would keep the view as big as the world map)
        // 4 pixels per zone cell: a 1-pixel darker rim shows where cells of different zones meet
        const int S = 4;
        int zoneCount = L2Encounters.ZoneCountIn(_rom!);
        var px = new int[64 * S * 64 * S];
        for (int zy = 0; zy < 64; zy++)
            for (int zx = 0; zx < 64; zx++)
            {
                int z = _world.Zones[zy * 64 + zx];
                if (z == 0) continue;
                uint c = ZoneColour(z) | (z >= zoneCount ? 0x40000000u : 0);
                bool left = zx == 0 || _world.Zones[zy * 64 + zx - 1] != z, top = zy == 0 || _world.Zones[(zy - 1) * 64 + zx] != z;
                for (int y = 0; y < S; y++)
                    for (int x = 0; x < S; x++)
                    {
                        bool rim = x == 0 && left || y == 0 && top;
                        px[(zy * S + y) * 64 * S + zx * S + x] = (int)(rim ? c & 0x00FFFFFFu | 0xE0000000u : c);
                    }
            }
        ZoneImage = BitmapSource.Create(64 * S, 64 * S, 96, 96, PixelFormats.Bgra32, null, px, 64 * S * 4);
        ZoneImageSize = WorldPixels;
    }

    /// <summary>The zone list beside the map: each zone's colour, where it is used and its groups' monsters.</summary>
    private void FillZoneRows()
    {
        ZoneRows.Clear();
        OnPropertyChanged(nameof(AddZoneNote));
        if (_rom == null || _world == null || !HasZones || !ShowZones) return;
        var sizes = L2Encounters.ZoneSizes(_world.Zones);
        int.TryParse(ZoneBrushText.Trim(), System.Globalization.NumberStyles.HexNumber, null, out int brush);
        var monsters = L2Battle.Monsters(_rom);
        string Names(int g) => string.Join(", ", L2Encounters.Group(_rom, g).Where(m => m != 0xFF).GroupBy(m => m)
            .Select(x => (x.Key < monsters.Count ? monsters[x.Key].Name.Trim() : $"monster {x.Key:X2}") + (x.Count() > 1 ? $" x{x.Count()}" : "")));
        for (int z = 0; z < L2Encounters.ZoneCountIn(_rom); z++)
        {
            var groups = L2Encounters.ZoneGroups(_rom, z);
            uint c = ZoneColour(z);
            ZoneRows.Add(new L2ZoneRow
            {
                Zone = z, IsBrush = z == brush,
                Title = $"Zone {z:X2}" + (sizes[z] > 0 ? $"  ({sizes[z]} squares on the map)" : "  (not on the map)"),
                Groups = z == 0 || groups.Count == 0 ? "No battles here." : string.Join("\n", groups.Select(g => $"{g.Share * 100:0}%  group {g.Group:X2}: {Names(g.Group)}")),
                Swatch = new SolidColorBrush(Color.FromArgb(z == 0 ? (byte)0x30 : (byte)0xFF, (byte)(c >> 16), (byte)(c >> 8), (byte)c)),
            });
        }
    }

    [RelayCommand]
    private void PickZone(int zone) { ZoneBrushText = zone.ToString("X2"); Status = $"Painting zone {zone:X2}: click or drag on the map."; }

    [RelayCommand]
    private void OpenZone(int zone) => OpenZoneRequested?.Invoke(zone);

    /// <summary>Why no zone can be added (shown under the list), or "".</summary>
    public string AddZoneNote => _rom == null ? "" : L2Encounters.CantAddZones(_rom) ?? $"{L2Encounters.ZoneCountIn(_rom)} zones; + New zone adds one (up to {L2Encounters.MaxZones}).";

    /// <summary>Adds a battle zone (the game has 17; the table moves to the added space the first time) and picks it to paint.</summary>
    [RelayCommand]
    private void AddZone()
    {
        if (_rom == null || _main == null) return;
        if (TableGuard.LockReason(_rom, "Encounters") is { } locked) { Status = locked; return; }
        try
        {
            int z = L2Encounters.AddZone(_rom);
            _main.NotifyRomModified($"Lufia II: battle zone {z:X2} added");
            ZoneBrushText = z.ToString("X2");
            FillZoneRows(); OnPropertyChanged(nameof(AddZoneNote));
            Status = $"Zone {z:X2} added (no battles yet): paint it on the map, and Monsters... gives it its groups.";
        }
        catch (Exception ex) { Status = "No zone added: " + ex.Message; }
    }

    private void DrawWorldPicker()
    {
        var w = _world!;
        _order = PaletteMode switch
        {
            2 => w.Cells.GroupBy(c => c).OrderByDescending(g => g.Count()).Select(g => g.Key).ToList(),
            _ => Enumerable.Range(0, w.BlockCount).ToList(),
        };
        int rows = Math.Max(1, (_order.Count + PickerColumns - 1) / PickerColumns), pw = PickerColumns * PCell, ph = rows * PCell;
        var px = new uint[pw * ph];
        for (int k = 0; k < _order.Count; k++)
        {
            var bp = BigHalf(_order[k]);
            int x0 = k % PickerColumns * PCell, y0 = k / PickerColumns * PCell;
            for (int y = 0; y < PCell; y++) Array.Copy(bp, y * PCell, px, (y0 + y) * pw + x0, PCell);
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
        _worldHistory.Add(((int[])w.Cells.Clone(), (byte[])w.Zones.Clone(), _edit?.Save(), what));
        if (_worldHistory.Count > 100) _worldHistory.RemoveAt(0);
        _worldRedo.Clear();
        WorldUndoState();
    }

    /// <summary>Drops the last step when nothing changed after all.</summary>
    private void WorldDropIfSame()
    {
        if (_world == null || _worldHistory.Count == 0) return;
        var (cells, zones, info, _) = _worldHistory[^1];
        if (cells.AsSpan().SequenceEqual(_world.Cells) && zones.AsSpan().SequenceEqual(_world.Zones) && (info == null || _edit == null || L2MapEdit.Same(info, _edit.Save())))
        { _worldHistory.RemoveAt(_worldHistory.Count - 1); WorldUndoState(); }
    }

    private void WorldUndoState()
    {
        MapUndoText = _worldHistory.Count > 0 ? "Undo: " + _worldHistory[^1].What + " (Ctrl+Z)" : "Nothing to undo";
        MapRedoText = _worldRedo.Count > 0 ? "Redo: " + _worldRedo[^1].What + " (Ctrl+Y)" : "Nothing to redo";
        HasUnappliedChanges = _world?.Changed == true || _edit?.HasChanges == true;
        UndoCommand.NotifyCanExecuteChanged(); RedoCommand.NotifyCanExecuteChanged();
    }

    private bool WorldUndo(bool redo)
    {
        var from = redo ? _worldRedo : _worldHistory;
        var to = redo ? _worldHistory : _worldRedo;
        if (_world == null || from.Count == 0) return false;
        _dragging = false; _worldDragging = false;
        var (cells, zones, info, what) = from[^1]; from.RemoveAt(from.Count - 1);
        to.Add(((int[])_world.Cells.Clone(), (byte[])_world.Zones.Clone(), _edit?.Save(), what));
        _world.Cells = cells; _world.Zones = zones;
        if (info != null && _edit != null) { _edit.Restore(info); if (Selection != null) Selection = Find(Selection.Kind, Selection.Index); }
        DrawWorld(); DrawOverlays(); UpdateInspector(); WorldUndoState(); FillZoneRows();
        Status = (redo ? "Redone: " : "Undone: ") + what + ".";
        return true;
    }

    private bool _worldDragging;
    private int _wLastX = -1, _wLastY = -1;

    /// <summary>Whether a press on the world map paints or picks big blocks or zones (else the town tools: select, exits, points).</summary>
    private bool WorldBlockTool => ShowZones && HasZones || Stamping || Tool is L2Tool.Paint or L2Tool.Pick or L2Tool.Area;

    private void WorldDown(double px, double py, bool right, bool ctrl)
    {
        var w = _world!;
        int cx = (int)Math.Floor(px / Cell), cy = (int)Math.Floor(py / Cell);
        if (cx < 0 || cy < 0 || cx >= L2WorldMap.Size || cy >= L2WorldMap.Size) return;
        _wLastX = cx; _wLastY = cy;
        if (ShowZones && HasZones)
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
        }
    }

    private void WorldMove(double px, double py)
    {
        var w = _world!;
        int cx = (int)Math.Floor(px / Cell), cy = (int)Math.Floor(py / Cell);
        bool inside = cx >= 0 && cy >= 0 && cx < L2WorldMap.Size && cy < L2WorldMap.Size;
        if (!inside) { HoverText = ""; BrushPreviewVisible = false; return; }
        int b = w.Cells[cy * L2WorldMap.Size + cx];
        int small = _rom!.ReadUInt16Le(w.Parts.BigBlocks + b * 8);
        string[] terrain = { "plain", "desert", "forest", "water", "mountain" };
        int t = L2WorldMap.Terrain(_rom, small) >> 4;
        int sx = (int)Math.Floor(px / 16), sy = (int)Math.Floor(py / 16);
        string text = $"block ({sx},{sy}), big block ({cx},{cy}) {b:X3}, {(t < terrain.Length ? terrain[t] : "terrain " + t)}" + (HasZones ? $"; battle zone {w.Zones[cy / 2 * 64 + cx / 2]:X2}" : "");
        if (_edit != null) foreach (var (kind, idx) in HitsAt(sx, sy)) text += "; " + Describe(kind, idx);
        HoverText = text;
        if (Tool == L2Tool.Paint && !Stamping && !ShowZones) { BrushPreviewX = cx * Cell; BrushPreviewY = cy * Cell; BrushPreviewW = BrushW * Cell; BrushPreviewH = BrushH * Cell; BrushPreviewVisible = BrushPreviewImage != null; }
        else BrushPreviewVisible = false;
        if (Stamping) { StampX = cx * Cell; StampY = cy * Cell; }
        if (!_worldDragging || cx == _wLastX && cy == _wLastY) return;
        _wLastX = cx; _wLastY = cy;
        if (ShowZones && HasZones) PaintZone(cx, cy);
        else if (Tool == L2Tool.Paint) PaintWorld(cx, cy);
        else if (Tool == L2Tool.Area && _areaStart is { } s) AreaRect = (Math.Min(s.X, cx), Math.Min(s.Y, cy), Math.Max(s.X, cx) + 1, Math.Max(s.Y, cy) + 1);
    }

    private void WorldUp()
    {
        if (_worldDragging) WorldDropIfSame();
        _worldDragging = false;
        if (!_dragging) _areaStart = null;
        WorldUndoState();
    }

    private void PaintWorld(int cx, int cy)
    {
        var w = _world!;
        for (int by = 0; by < BrushH; by++)
            for (int bx = 0; bx < BrushW; bx++)
                if (cx + bx < L2WorldMap.Size && cy + by < L2WorldMap.Size) w.Cells[(cy + by) * L2WorldMap.Size + cx + bx] = _brush[by * BrushW + bx];
        DrawWorldCells(cx, cy, cx + BrushW, cy + BrushH); WorldUndoState();
    }

    private void PaintZone(int cx, int cy)
    {
        if (!int.TryParse(ZoneBrushText.Trim(), System.Globalization.NumberStyles.HexNumber, null, out int z) || z is < 0 or > 255) { Status = "Pick the zone to paint in the list (or type it, hex; 00 = no battles)."; return; }
        if (z >= L2Encounters.ZoneCountIn(_rom!)) { Status = $"Zone {z:X2} doesn't exist yet: + New zone adds zones (there are {L2Encounters.ZoneCountIn(_rom!)})."; return; }
        if (_world!.Zones[cy / 2 * 64 + cx / 2] == z) return;
        _world.Zones[cy / 2 * 64 + cx / 2] = (byte)z;
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
        DrawWorldCells(cx, cy, cx + c.W, cy + c.H); WorldUndoState();
    }

    private string? WriteWorld()
    {
        var w = _world!;
        if (!w.Changed && _edit?.HasChanges != true) return null;
        try
        {
            if (!w.Cells.AsSpan().SequenceEqual(w.OrigCells)) L2Rom.WriteFile(_rom!, w.Parts.MapFile, L2WorldMap.WithCells(w.File, w.Cells));
            if (!w.Zones.AsSpan().SequenceEqual(w.OrigZones)) L2Rom.WriteFile(_rom!, L2Encounters.ZoneFile, w.Zones);
            string what = "";
            if (_edit?.HasChanges == true) what = " " + _edit.Apply(_rom!);
            _main!.NotifyRomModified(w.Map == 0 ? "Lufia II: world map" : "Lufia II: sea floor");
            var keep = Selection;
            LoadWorld(w.Map);
            _edit = new L2MapEdit(_rom!, w.Map);
            if (keep != null) Selection = Find(keep.Kind, keep.Index);
            Status = "Written." + what;
            return null;
        }
        catch (Exception ex) { return ex.Message; }
    }
}
