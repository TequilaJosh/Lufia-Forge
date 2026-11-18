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

// a battle zone in the world map's zone list: its colour, its groups and their monsters
public sealed class L2ZoneRow
{
    public int Zone { get; init; }
    public string Title { get; init; } = "";
    public string Groups { get; init; } = "";
    public Brush Swatch { get; init; } = Brushes.Transparent;
    public bool IsBrush { get; init; }
}

// the world map and the sea floor in the Maps tab: 128 x 128 big blocks (32 x 32 pixels, shown at full size, one game
// pixel per screen pixel at 100%), painted, picked, copied and pasted like a town's blocks. the world map also has the
// random-battle zones (file 0x193, one byte per 2 x 2 big blocks, L2Encounters.Zone) and the entrances (map 00's
// exits, edited like a town's, L2MapEdit keeps its info block). written to the map files like a town
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

    // asks the Lufia II tab to show a battle zone on the Encounters page
    public event Action<int>? OpenZoneRequested;

    private const int Cell = 32;    // a big block on the map, same as the game
    private const int PickerCell = 16;   // a big block in the block list (half size)

    private void LoadWorld(int map)
    {
        _world = null;
        if (_rom == null || map > 1) { OnPropertyChanged(nameof(IsWorldMap)); OnPropertyChanged(nameof(HasZones)); OnPropertyChanged(nameof(ZonePanelVisible)); return; }
        try
        {
            var world = new WorldEdit { Map = map, Parts = L2WorldMap.Of(map) };
            world.File = L2Rom.ReadFile(_rom, world.Parts.MapFile);
            world.Cells = L2WorldMap.Cells(world.File); world.OrigCells = (int[])world.Cells.Clone();
            world.Zones = map == 0 ? L2Rom.ReadFile(_rom, L2Encounters.ZoneFile) : Array.Empty<byte>(); world.OrigZones = (byte[])world.Zones.Clone();
            world.Tiles = L2Rom.ReadFile(_rom, world.Parts.TileFile);
            world.Pal = L2WorldMap.Colours(_rom, world.Parts);
            world.BlockCount = L2WorldMap.BigBlockCount(world.Parts, world.Cells);
            _world = world;
        }
        catch (Exception ex) { Status = "Couldn't read it: " + ex.Message; }
        _worldHistory.Clear(); _worldRedo.Clear();
        OnPropertyChanged(nameof(IsWorldMap)); OnPropertyChanged(nameof(HasZones)); OnPropertyChanged(nameof(ZonePanelVisible));
        FillZoneRows();
    }

    // a big block at full size (32 x 32)
    private uint[] Big(int block)
    {
        var world = _world!;
        if (!world.Pictures.TryGetValue(block, out var pixels))
        {
            pixels = L2WorldMap.BigBlock(_rom!, world.Parts, world.Tiles, world.Pal, block);
            for (int i = 0; i < pixels.Length; i++) if (pixels[i] == 0) pixels[i] = 0xFF000000u;
            world.Pictures[block] = pixels;
        }
        return pixels;
    }

    // a big block at half size (16 x 16), for the block list
    private uint[] BigHalf(int block)
    {
        var world = _world!;
        if (!world.Halves.TryGetValue(block, out var pixels)) world.Halves[block] = pixels = L2WorldMap.Half(Big(block));
        return pixels;
    }

    private const int WorldPixels = L2WorldMap.Size * Cell;   // 4096

    private void DrawWorld()
    {
        var world = _world!;
        world.Bitmap ??= new WriteableBitmap(WorldPixels, WorldPixels, 96, 96, PixelFormats.Bgra32, null);
        DrawWorldCells(0, 0, L2WorldMap.Size, L2WorldMap.Size);
        if (MapImage != world.Bitmap) MapImage = world.Bitmap;
        MapSummary = world.Map == 0 ? "The world map: 128 x 128 big blocks of 32 x 32 pixels, at full size; battle zones are 2 x 2 big blocks; entrances are in 16-pixel blocks."
                                : "The sea floor: 128 x 128 big blocks of 32 x 32 pixels, at full size.";
        DrawZones();
    }

    // redraws the big blocks in a rectangle (in cells, x2 / y2 not included), one row of big blocks at a time
    private void DrawWorldCells(int x1, int y1, int x2, int y2)
    {
        var world = _world!;
        if (world.Bitmap == null) { DrawWorld(); return; }
        x1 = Math.Clamp(x1, 0, L2WorldMap.Size); x2 = Math.Clamp(x2, 0, L2WorldMap.Size);
        y1 = Math.Clamp(y1, 0, L2WorldMap.Size); y2 = Math.Clamp(y2, 0, L2WorldMap.Size);
        if (x2 <= x1 || y2 <= y1) return;
        int stripW = (x2 - x1) * Cell;
        var strip = new int[stripW * Cell];
        for (int cy = y1; cy < y2; cy++)
        {
            for (int cx = x1; cx < x2; cx++)
            {
                var bigPixels = Big(world.Cells[cy * L2WorldMap.Size + cx]);
                for (int y = 0; y < Cell; y++)
                    for (int x = 0; x < Cell; x++) strip[y * stripW + (cx - x1) * Cell + x] = (int)bigPixels[y * Cell + x];
            }
            world.Bitmap.WritePixels(new System.Windows.Int32Rect(x1 * Cell, cy * Cell, stripW, Cell), strip, stripW * 4, 0);
        }
    }

    // a zone's colour (semi-transparent), the same on the map and in the list
    public static uint ZoneColour(int z) => z == 0 ? 0u : ZoneColours[(z - 1) % ZoneColours.Length];

    private static readonly uint[] ZoneColours =
    {
        0x90FF4040, 0x9040C040, 0x904080FF, 0x90FFC040, 0x90C040FF, 0x9040FFFF, 0x90FF80C0, 0x90A0A040,
        0x9040A0A0, 0x90A040A0, 0x90FF8040, 0x9080FF80, 0x908080FF, 0x90FFFF80, 0x90C08040, 0x9080C0FF, 0x90FFFFFF,
    };

    private void DrawZones()
    {
        if (_world == null || !HasZones || !ShowZones) { ZoneImage = null; ZoneImageSize = 0; return; }   // (its size would keep the view as big as the world map)
        // 4 pixels per zone cell, with a 1-pixel darker rim where cells of different zones meet
        const int S = 4;
        int zoneCount = L2Encounters.ZoneCountIn(_rom!);
        var pixels = new int[64 * S * 64 * S];
        for (int zy = 0; zy < 64; zy++)
            for (int zx = 0; zx < 64; zx++)
            {
                int zone = _world.Zones[zy * 64 + zx];
                if (zone == 0) continue;
                uint colour = ZoneColour(zone) | (zone >= zoneCount ? 0x40000000u : 0);
                bool left = zx == 0 || _world.Zones[zy * 64 + zx - 1] != zone, top = zy == 0 || _world.Zones[(zy - 1) * 64 + zx] != zone;
                for (int y = 0; y < S; y++)
                    for (int x = 0; x < S; x++)
                    {
                        bool rim = x == 0 && left || y == 0 && top;
                        pixels[(zy * S + y) * 64 * S + zx * S + x] = (int)(rim ? colour & 0x00FFFFFFu | 0xE0000000u : colour);
                    }
            }
        ZoneImage = BitmapSource.Create(64 * S, 64 * S, 96, 96, PixelFormats.Bgra32, null, pixels, 64 * S * 4);
        ZoneImageSize = WorldPixels;
    }

    // the zone list beside the map: each zone's colour, where it's used and its groups' monsters
    private void FillZoneRows()
    {
        ZoneRows.Clear();
        OnPropertyChanged(nameof(AddZoneNote));
        if (_rom == null || _world == null || !HasZones || !ShowZones) return;
        var sizes = L2Encounters.ZoneSizes(_world.Zones);
        int.TryParse(ZoneBrushText.Trim(), System.Globalization.NumberStyles.HexNumber, null, out int brushZone);
        var monsters = L2Battle.Monsters(_rom);
        string Names(int group) => string.Join(", ", L2Encounters.Group(_rom, group).Where(m => m != 0xFF).GroupBy(m => m)
            .Select(x => (x.Key < monsters.Count ? monsters[x.Key].Name.Trim() : $"monster {x.Key:X2}") + (x.Count() > 1 ? $" x{x.Count()}" : "")));
        for (int zone = 0; zone < L2Encounters.ZoneCountIn(_rom); zone++)
        {
            var groups = L2Encounters.ZoneGroups(_rom, zone);
            uint colour = ZoneColour(zone);
            ZoneRows.Add(new L2ZoneRow
            {
                Zone = zone, IsBrush = zone == brushZone,
                Title = $"Zone {zone:X2}" + (sizes[zone] > 0 ? $"  ({sizes[zone]} squares on the map)" : "  (not on the map)"),
                Groups = zone == 0 || groups.Count == 0 ? "No battles here." : string.Join("\n", groups.Select(g => $"{g.Share * 100:0}%  group {g.Group:X2}: {Names(g.Group)}")),
                Swatch = new SolidColorBrush(Color.FromArgb(zone == 0 ? (byte)0x30 : (byte)0xFF, (byte)(colour >> 16), (byte)(colour >> 8), (byte)colour)),
            });
        }
    }

    [RelayCommand]
    private void PickZone(int zone) { ZoneBrushText = zone.ToString("X2"); Status = $"Painting zone {zone:X2}: click or drag on the map."; }

    [RelayCommand]
    private void OpenZone(int zone) => OpenZoneRequested?.Invoke(zone);

    // why no zone can be added (shown under the list), or ""
    public string AddZoneNote => _rom == null ? "" : L2Encounters.CantAddZones(_rom) ?? $"{L2Encounters.ZoneCountIn(_rom)} zones; + New zone adds one (up to {L2Encounters.MaxZones}).";

    // adds a battle zone and picks it to paint. the game has 17, the table moves to the added space the first time
    [RelayCommand]
    private void AddZone()
    {
        if (_rom == null || _main == null) return;
        if (TableGuard.LockReason(_rom, "Encounters") is { } locked) { Status = locked; return; }
        try
        {
            int zone = L2Encounters.AddZone(_rom);
            _main.NotifyRomModified($"Lufia II: battle zone {zone:X2} added");
            ZoneBrushText = zone.ToString("X2");
            FillZoneRows(); OnPropertyChanged(nameof(AddZoneNote));
            Status = $"Zone {zone:X2} added (no battles yet): paint it on the map, and Monsters... gives it its groups.";
        }
        catch (Exception ex) { Status = "No zone added: " + ex.Message; }
    }

    private void DrawWorldPicker()
    {
        var world = _world!;
        _order = PaletteMode switch
        {
            2 => world.Cells.GroupBy(c => c).OrderByDescending(g => g.Count()).Select(g => g.Key).ToList(),
            _ => Enumerable.Range(0, world.BlockCount).ToList(),
        };
        int rows = Math.Max(1, (_order.Count + PickerColumns - 1) / PickerColumns), pickerW = PickerColumns * PickerCell, pickerH = rows * PickerCell;
        var pixels = new uint[pickerW * pickerH];
        for (int slot = 0; slot < _order.Count; slot++)
        {
            var halfPixels = BigHalf(_order[slot]);
            int x0 = slot % PickerColumns * PickerCell, y0 = slot / PickerColumns * PickerCell;
            for (int y = 0; y < PickerCell; y++) Array.Copy(halfPixels, y * PickerCell, pixels, (y0 + y) * pickerW + x0, PickerCell);
        }
        PickerImage = BitmapSource.Create(pickerW, pickerH, 96, 96, PixelFormats.Bgra32, null, pixels.Select(c => (int)(c == 0 ? 0xFF000000u : c)).ToArray(), pickerW * 4);
        _blocks = Array.Empty<uint[]>();
        BrushPreviewImage = WorldBrushPicture();
        UpdatePickerBox();
        NeighbourRows.Clear(); NeighbourNote = "";
        RecentBrushes.Clear();
    }

    private BitmapSource? WorldBrushPicture()
    {
        if (_world == null) return null;
        var (blocksW, blocksH, cells, _) = Painted();
        int w = blocksW * Cell, h = blocksH * Cell;
        var pixels = new uint[w * h];
        for (int by = 0; by < blocksH; by++)
            for (int bx = 0; bx < blocksW; bx++)
            {
                var bigPixels = Big(cells[by * blocksW + bx]);
                for (int y = 0; y < Cell; y++) Array.Copy(bigPixels, y * Cell, pixels, (by * Cell + y) * w + bx * Cell, Cell);
            }
        return BitmapSource.Create(w, h, 96, 96, PixelFormats.Bgra32, null, pixels.Select(c => (int)c).ToArray(), w * 4);
    }

    // editing

    private void WorldRecord(string what)
    {
        var world = _world!;
        _worldHistory.Add(((int[])world.Cells.Clone(), (byte[])world.Zones.Clone(), _edit?.Save(), what));
        if (_worldHistory.Count > 100) _worldHistory.RemoveAt(0);
        _worldRedo.Clear();
        WorldUndoState();
    }

    // drops the last step if nothing changed after all
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
        var takeFrom = redo ? _worldRedo : _worldHistory;
        var putOn = redo ? _worldHistory : _worldRedo;
        if (_world == null || takeFrom.Count == 0) return false;
        _dragging = false; _worldDragging = false;
        var (cells, zones, info, what) = takeFrom[^1]; takeFrom.RemoveAt(takeFrom.Count - 1);
        putOn.Add(((int[])_world.Cells.Clone(), (byte[])_world.Zones.Clone(), _edit?.Save(), what));
        _world.Cells = cells; _world.Zones = zones;
        if (info != null && _edit != null) { _edit.Restore(info); if (Selection != null) Selection = Find(Selection.Kind, Selection.Index); }
        DrawWorld(); DrawOverlays(); UpdateInspector(); WorldUndoState(); FillZoneRows();
        Status = (redo ? "Redone: " : "Undone: ") + what + ".";
        return true;
    }

    private bool _worldDragging;
    private int _worldLastX = -1, _worldLastY = -1;

    // true if a press on the world map paints or picks big blocks or zones (else it's the town tools: select, exits, points)
    private bool WorldBlockTool => ShowZones && HasZones || Stamping || Tool is L2Tool.Paint or L2Tool.Pick or L2Tool.Area;

    private void WorldDown(double px, double py, bool right, bool ctrl)
    {
        var world = _world!;
        int cx = (int)Math.Floor(px / Cell), cy = (int)Math.Floor(py / Cell);
        if (cx < 0 || cy < 0 || cx >= L2WorldMap.Size || cy >= L2WorldMap.Size) return;
        _worldLastX = cx; _worldLastY = cy;
        if (ShowZones && HasZones)
        {
            if (right || ctrl) { ZoneBrushText = world.Zones[cy / 2 * 64 + cx / 2].ToString("X2"); Status = $"Zone {ZoneBrushText} picked."; return; }
            WorldRecord("painting battle zones"); _worldDragging = true; PaintZone(cx, cy); return;
        }
        if (Stamping) { if (right) { StopStamp(); return; } WorldRecord("placing copied blocks"); StampWorld(cx, cy); return; }
        if (right || ctrl || Tool == L2Tool.Pick) { SetWorldBrush(world.Cells[cy * L2WorldMap.Size + cx]); Status = $"Big block {SelectedBlock:X3} picked from ({cx},{cy})."; return; }
        switch (Tool)
        {
            case L2Tool.Paint:
                WorldRecord("painting"); _worldDragging = true; PaintWorld(cx, cy); break;
            case L2Tool.Fill:
                WorldRecord(System.Windows.Input.Keyboard.Modifiers.HasFlag(System.Windows.Input.ModifierKeys.Shift) ? "filling the map" : "filling");
                FillWorld(cx, cy, System.Windows.Input.Keyboard.Modifiers.HasFlag(System.Windows.Input.ModifierKeys.Shift));
                WorldDropIfSame(); WorldUndoState(); break;
            case L2Tool.Area:
                _areaStart = (cx, cy); AreaRect = (cx, cy, cx + 1, cy + 1); _worldDragging = true; break;
        }
    }

    private void WorldMove(double px, double py)
    {
        var world = _world!;
        int cx = (int)Math.Floor(px / Cell), cy = (int)Math.Floor(py / Cell);
        bool inside = cx >= 0 && cy >= 0 && cx < L2WorldMap.Size && cy < L2WorldMap.Size;
        if (!inside) { HoverText = ""; BrushPreviewVisible = false; return; }
        int bigBlock = world.Cells[cy * L2WorldMap.Size + cx];
        int small = _rom!.ReadUInt16Le(world.Parts.BigBlocks + bigBlock * 8);
        string[] terrain = { "plain", "desert", "forest", "water", "mountain" };
        int terrainType = L2WorldMap.Terrain(_rom, small) >> 4;
        int sx = (int)Math.Floor(px / 16), sy = (int)Math.Floor(py / 16);
        string text = $"block ({sx},{sy}), big block ({cx},{cy}) {bigBlock:X3}, {(terrainType < terrain.Length ? terrain[terrainType] : "terrain " + terrainType)}" + (HasZones ? $"; battle zone {world.Zones[cy / 2 * 64 + cx / 2]:X2}" : "");
        if (_edit != null) foreach (var (kind, index) in HitsAt(sx, sy)) text += "; " + Describe(kind, index);
        HoverText = text;
        if (Tool == L2Tool.Paint && !Stamping && !ShowZones) { var (paintW, paintH, _, offset) = Painted(); BrushPreviewX = (cx + offset) * Cell; BrushPreviewY = (cy + offset) * Cell; BrushPreviewW = paintW * Cell; BrushPreviewH = paintH * Cell; BrushPreviewVisible = BrushPreviewImage != null; }
        else BrushPreviewVisible = false;
        if (Stamping) { StampX = cx * Cell; StampY = cy * Cell; }
        if (!_worldDragging || cx == _worldLastX && cy == _worldLastY) return;
        _worldLastX = cx; _worldLastY = cy;
        if (ShowZones && HasZones) PaintZone(cx, cy);
        else if (Tool == L2Tool.Paint) PaintWorld(cx, cy);
        else if (Tool == L2Tool.Area && _areaStart is { } start) AreaRect = (Math.Min(start.X, cx), Math.Min(start.Y, cy), Math.Max(start.X, cx) + 1, Math.Max(start.Y, cy) + 1);
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
        var world = _world!;
        var (paintW, paintH, cells, offset) = Painted();
        cx += offset; cy += offset;
        for (int by = 0; by < paintH; by++)
            for (int bx = 0; bx < paintW; bx++)
                if (cx + bx is >= 0 and < L2WorldMap.Size && cy + by is >= 0 and < L2WorldMap.Size) world.Cells[(cy + by) * L2WorldMap.Size + cx + bx] = cells[by * paintW + bx];
        DrawWorldCells(Math.Max(0, cx), Math.Max(0, cy), Math.Min(L2WorldMap.Size, cx + paintW), Math.Min(L2WorldMap.Size, cy + paintH)); WorldUndoState();
    }

    // fills the big blocks joined to (cx, cy) that are the same as it (side by side, not corner to corner) with the
    // brush. a brush of several blocks repeats, lined up with the map's corner. all = the whole map
    private void FillWorld(int cx, int cy, bool all = false)
    {
        var world = _world!;
        int changed = Fill(world.Cells, L2WorldMap.Size, L2WorldMap.Size, cx, cy, all);
        DrawWorld();
        Status = changed == 0 ? "Nothing to fill: it is the brush already." : $"{changed} big block{(changed == 1 ? "" : "s")} filled with the brush (Undo takes it back).";
    }

    private void PaintZone(int cx, int cy)
    {
        if (!int.TryParse(ZoneBrushText.Trim(), System.Globalization.NumberStyles.HexNumber, null, out int zone) || zone is < 0 or > 255) { Status = "Pick the zone to paint in the list (or type it, hex; 00 = no battles)."; return; }
        if (zone >= L2Encounters.ZoneCountIn(_rom!)) { Status = $"Zone {zone:X2} doesn't exist yet: + New zone adds zones (there are {L2Encounters.ZoneCountIn(_rom!)})."; return; }
        if (_world!.Zones[cy / 2 * 64 + cx / 2] == zone) return;
        _world.Zones[cy / 2 * 64 + cx / 2] = (byte)zone;
        DrawZones(); WorldUndoState();
    }

    private void SetWorldBrush(int block) { BrushW = 1; BrushH = 1; _brush = new[] { block }; SelectedBlock = block; BrushPreviewImage = WorldBrushPicture(); UpdatePickerBox(); }

    private Clip WorldCut((int X1, int Y1, int X2, int Y2) area)
    {
        int w = area.X2 - area.X1, h = area.Y2 - area.Y1;
        var ground = new int[w * h];
        for (int y = 0; y < h; y++) for (int x = 0; x < w; x++) ground[y * w + x] = _world!.Cells[(area.Y1 + y) * L2WorldMap.Size + area.X1 + x];
        return new Clip(w, h, ground, new int[w * h], new byte[w * h], -1 - _world!.Map, -1);
    }

    private void StampWorld(int cx, int cy)
    {
        if (_clip is not { } clip) return;
        for (int y = 0; y < clip.H; y++)
            for (int x = 0; x < clip.W; x++)
                if (cx + x < L2WorldMap.Size && cy + y < L2WorldMap.Size) _world!.Cells[(cy + y) * L2WorldMap.Size + cx + x] = clip.Ground[y * clip.W + x];
        DrawWorldCells(cx, cy, cx + clip.W, cy + clip.H); WorldUndoState();
    }

    private string? WriteWorld()
    {
        var world = _world!;
        if (!world.Changed && _edit?.HasChanges != true) return null;
        try
        {
            if (!world.Cells.AsSpan().SequenceEqual(world.OrigCells)) L2Rom.WriteFile(_rom!, world.Parts.MapFile, L2WorldMap.WithCells(world.File, world.Cells));
            if (!world.Zones.AsSpan().SequenceEqual(world.OrigZones)) L2Rom.WriteFile(_rom!, L2Encounters.ZoneFile, world.Zones);
            string what = "";
            if (_edit?.HasChanges == true) what = " " + _edit.Apply(_rom!);
            _main!.NotifyRomModified(world.Map == 0 ? "Lufia II: world map" : "Lufia II: sea floor");
            var keepSelection = Selection;
            LoadWorld(world.Map);
            _edit = new L2MapEdit(_rom!, world.Map);
            _readVersion = _rom!.Version;
            if (keepSelection != null) Selection = Find(keepSelection.Kind, keepSelection.Index);
            Status = "Written." + what;
            return null;
        }
        catch (Exception ex) { return ex.Message; }
    }
}
