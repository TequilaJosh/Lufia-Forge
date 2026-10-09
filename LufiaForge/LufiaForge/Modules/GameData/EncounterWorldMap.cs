using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using LufiaForge.Core;
using LufiaForge.Core.Battle;
using LufiaForge.Core.Maps;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace LufiaForge.Modules.GameData;

/// <summary>
/// The world map's encounter zones (resource B0: 80 x 64 cells, one per 4x4 tiles, each a group number; 0 = no
/// battles, the game skips the encounter when the group is 0). Shown over the world map, picked, painted and filled.
/// </summary>
public partial class EncounterEditorViewModel
{
    /// <summary>Pixels per world map tile in <see cref="WorldImage"/> (16 would be full size).</summary>
    public const int TileScale = 4;
    public const int CellPixels = 4 * TileScale;
    public double WorldPixelWidth => Encounters.ZoneWidth * CellPixels;
    public double WorldPixelHeight => Encounters.ZoneHeight * CellPixels;

    private byte[] _zones = Array.Empty<byte>();
    [ObservableProperty] private ImageSource? _worldImage;
    [ObservableProperty] private WriteableBitmap? _zoneImage;
    [ObservableProperty] private bool _zonesDirty;
    [ObservableProperty] private bool _showAllZones = true;
    /// <summary>0 = pick (click a zone to select its group), 1 = paint, 2 = fill an area.</summary>
    [ObservableProperty] private int _toolIndex;
    [ObservableProperty] private int _brushSize = 1;
    /// <summary>Paint "no battles" (group 0) instead of the selected group.</summary>
    [ObservableProperty] private bool _eraseZones;
    [ObservableProperty] private string _hoverText = "";
    [ObservableProperty] private string _worldStatus = "";

    public string[] Tools { get; } = { "Pick (click a zone to open its group)", "Paint with the selected group", "Fill an area with the selected group", "Rectangle: drag to cover an area" };
    public int[] BrushSizes { get; } = { 1, 2, 3, 4, 6 };

    partial void OnShowAllZonesChanged(bool value) => DrawZones();

    private void StartWorldMap()
    {
        DrawZones();
        if (Ctx == null) return;
        var rom = Ctx.Rom;
        WorldStatus = "Drawing the world map...";
        Task.Run(() =>
        {
            var map = LufiaMap.Load(rom, LufiaMap.WorldMap);
            var px = MapTileset.ForMap(rom, map).RenderMap(map);
            int w = map.Width * 16, h = map.Height * 16, ow = w / 4, oh = h / 4;
            var small = new uint[ow * oh];
            for (int y = 0; y < oh; y++)
                for (int x = 0; x < ow; x++)
                {
                    uint r = 0, g = 0, b = 0;
                    for (int dy = 0; dy < 4; dy++)
                        for (int dx = 0; dx < 4; dx++)
                        {
                            uint c = px[(y * 4 + dy) * w + x * 4 + dx];
                            r += c >> 16 & 255; g += c >> 8 & 255; b += c & 255;
                        }
                    small[y * ow + x] = 0xFF000000u | (r / 16) << 16 | (g / 16) << 8 | b / 16;
                }
            return (small, ow, oh);
        }).ContinueWith(t =>
        {
            if (t.IsFaulted) { WorldStatus = "The world map could not be drawn: " + t.Exception?.GetBaseException().Message; return; }
            var (small, ow, oh) = t.Result;
            var bmp = BitmapSource.Create(ow, oh, 96, 96, PixelFormats.Bgra32, null, small, ow * 4);
            bmp.Freeze();
            WorldImage = bmp;
            WorldStatus = "";
        }, TaskScheduler.FromCurrentSynchronizationContext());
    }

    private static uint Hue(int g, uint alpha)
    {
        double h = (g * 47 % 360) / 60.0, x = 1 - Math.Abs(h % 2 - 1);
        (double r, double gg, double b) = h switch
        {
            < 1 => (1.0, x, 0.0), < 2 => (x, 1.0, 0.0), < 3 => (0.0, 1.0, x),
            < 4 => (0.0, x, 1.0), < 5 => (x, 0.0, 1.0), _ => (1.0, 0.0, x),
        };
        return alpha << 24 | (uint)(r * 255) << 16 | (uint)(gg * 255) << 8 | (uint)(b * 255);
    }

    private void DrawZones()
    {
        int w = Encounters.ZoneWidth, h = Encounters.ZoneHeight;
        if (_zones.Length < w * h) { ZoneImage = null; return; }
        int sel = SelectedGroup;
        var px = new uint[w * h];
        for (int i = 0; i < px.Length; i++)
        {
            int g = _zones[i];
            px[i] = g == 0 ? 0u : g == sel ? 0xA8FFE040u : ShowAllZones ? Hue(g, 0x38) : 0u;
        }
        var bmp = new WriteableBitmap(w, h, 96, 96, PixelFormats.Bgra32, null);
        bmp.WritePixels(new Int32Rect(0, 0, w, h), px, w * 4, 0);
        ZoneImage = bmp;
    }

    private bool InGrid(int cx, int cy) => cx >= 0 && cy >= 0 && cx < Encounters.ZoneWidth && cy < Encounters.ZoneHeight && _zones.Length >= Encounters.ZoneWidth * Encounters.ZoneHeight;

    /// <summary>Mouse over cell (cx, cy).</summary>
    public void HoverCell(int cx, int cy)
    {
        if (!InGrid(cx, cy)) { HoverText = ""; return; }
        int g = _zones[cy * Encounters.ZoneWidth + cx];
        HoverText = $"Zone {cx},{cy} (tiles {cx * 4}-{cx * 4 + 3}, {cy * 4}-{cy * 4 + 3}): " + (g == 0 ? "no battles" : $"group {g:X2}" + (g <= Count ? $" - {GroupMonsterSummary(g)}" : " (no such group!)"));
    }

    private string GroupMonsterSummary(int g) =>
        string.Join(", ", _groups[g - 1].Distinct().SelectMany(s => Monsters(s)).Where(m => m != 0xFF).Select(MonsterName).Distinct().Take(6));

    // ── tools: a stroke is mouse down .. mouse up; each stroke can be undone ──

    private readonly Stack<byte[]> _undo = new();
    [ObservableProperty] private bool _canUndo;
    private (int X, int Y)? _rectStart;
    private byte[]? _beforeRect;
    /// <summary>A "no battles" area bigger than this is joined to the open map; filling it asks first.</summary>
    private const int BigEmptyArea = 150;

    private int Brush => EraseZones ? 0 : SelectedGroup;

    private bool CheckBrush()
    {
        if (Brush == 0 && !EraseZones) { Status = "Select a battle group in the list first (boss formations can't be painted)."; return false; }
        return true;
    }

    private void PushUndo()
    {
        _undo.Push(_zones.ToArray());
        if (_undo.Count > 50) { var keep = _undo.Take(50).Reverse().ToList(); _undo.Clear(); foreach (var z in keep) _undo.Push(z); }
        CanUndo = true;
    }

    /// <summary>Mouse down on cell (cx, cy).</summary>
    public void BeginStroke(int cx, int cy)
    {
        if (!InGrid(cx, cy)) return;
        int i = cy * Encounters.ZoneWidth + cx;
        switch (ToolIndex)
        {
            case 0:   // pick
                int g = _zones[i];
                if (g == 0) { Status = "No battles in this zone. Pick a group in the list and paint to add some."; return; }
                if (g <= Count) SelectedIndex = g - 1;
                return;
            case 1:   // paint
                if (!CheckBrush()) return;
                PushUndo();
                PaintAt(cx, cy);
                return;
            case 2:   // fill
                if (!CheckBrush()) return;
                Fill(cx, cy);
                return;
            case 3:   // rectangle
                if (!CheckBrush()) return;
                _rectStart = (cx, cy);
                _beforeRect = _zones.ToArray();
                DragStroke(cx, cy);
                return;
        }
    }

    /// <summary>Mouse moved with the button down.</summary>
    public void DragStroke(int cx, int cy)
    {
        cx = Math.Clamp(cx, 0, Encounters.ZoneWidth - 1); cy = Math.Clamp(cy, 0, Encounters.ZoneHeight - 1);
        if (ToolIndex == 1) { if (Brush != 0 || EraseZones) PaintAt(cx, cy); return; }
        if (ToolIndex == 3 && _rectStart is var (sx, sy) && _beforeRect != null)
        {
            // preview: the zones before the stroke, with the rectangle on top
            _beforeRect.CopyTo(_zones, 0);
            for (int y = Math.Min(sy, cy); y <= Math.Max(sy, cy); y++)
                for (int x = Math.Min(sx, cx); x <= Math.Max(sx, cx); x++)
                    _zones[y * Encounters.ZoneWidth + x] = (byte)Brush;
            DrawZones();
            HoverText = $"Rectangle {Math.Abs(cx - sx) + 1} x {Math.Abs(cy - sy) + 1} zones";
        }
    }

    /// <summary>Mouse up.</summary>
    public void EndStroke(int cx, int cy)
    {
        if (ToolIndex == 3 && _beforeRect != null)
        {
            DragStroke(cx, cy);
            if (!_zones.SequenceEqual(_beforeRect))
            {
                _undo.Push(_beforeRect); CanUndo = true;
                ZonesDirty = true;
            }
            _beforeRect = null; _rectStart = null;
        }
    }

    private void PaintAt(int cx, int cy)
    {
        int brush = Brush, r0 = (BrushSize - 1) / 2;
        bool changed = false;
        for (int y = cy - r0; y < cy - r0 + BrushSize; y++)
            for (int x = cx - r0; x < cx - r0 + BrushSize; x++)
                if (InGrid(x, y) && _zones[y * Encounters.ZoneWidth + x] != brush) { _zones[y * Encounters.ZoneWidth + x] = (byte)brush; changed = true; }
        if (changed) { ZonesDirty = true; DrawZones(); HoverCell(cx, cy); }
    }

    /// <summary>The connected cells with the same group as (cx, cy).</summary>
    private List<int> Area(int cx, int cy)
    {
        int from = _zones[cy * Encounters.ZoneWidth + cx];
        var seen = new HashSet<int>();
        var todo = new Stack<(int, int)>(); todo.Push((cx, cy));
        while (todo.Count > 0)
        {
            var (x, y) = todo.Pop();
            if (!InGrid(x, y)) continue;
            int i = y * Encounters.ZoneWidth + x;
            if (_zones[i] != from || !seen.Add(i)) continue;
            todo.Push((x + 1, y)); todo.Push((x - 1, y)); todo.Push((x, y + 1)); todo.Push((x, y - 1));
        }
        return seen.ToList();
    }

    private void Fill(int cx, int cy)
    {
        int from = _zones[cy * Encounters.ZoneWidth + cx], brush = Brush;
        if (from == brush) return;
        var area = Area(cx, cy);
        // "no battles" cells usually join up across the sea and most of the map
        if (from == 0 && area.Count > BigEmptyArea &&
            MessageBox.Show($"This 'no battles' area is joined to the rest of the map's empty space ({area.Count} zones, {area.Count * 100 / _zones.Length}% of the map), " +
                            "so filling it would cover all of that.\n\nFill it anyway? (No = cancel; use Paint or Rectangle to place the group exactly.)",
                            "Fill a big area?", MessageBoxButton.YesNo) != MessageBoxResult.Yes)
        { Status = "Fill cancelled. Tip: the Rectangle tool places a group over exactly the zones you drag across."; return; }
        PushUndo();
        foreach (int i in area) _zones[i] = (byte)brush;
        ZonesDirty = true; DrawZones(); HoverCell(cx, cy);
        Status = $"Filled {area.Count} zones with {(brush == 0 ? "no battles" : $"group {brush:X2}")}.";
    }

    [RelayCommand]
    private void UndoZones()
    {
        if (_undo.Count == 0) return;
        _zones = _undo.Pop();
        CanUndo = _undo.Count > 0;
        ZonesDirty = true;
        DrawZones();
        Status = "Undone.";
    }

    [RelayCommand]
    private void WriteZones()
    {
        if (Ctx == null || _zones.Length == 0) return;
        var rom = Ctx.Rom;
        int size = LufiaCompression.Compress(_zones).Length;
        int slot = ResourceWriter.SlotSize(rom, Encounters.ZoneResource);
        if (size > slot && rom.Length < MapWriter.ExpandedSize &&
            MessageBox.Show("The edited zones don't fit in their original space. Expand the ROM to 2 MB?", "Expand ROM?", MessageBoxButton.YesNo) != MessageBoxResult.Yes)
        { Status = "Zones not written: the ROM was not expanded."; return; }
        var zones = _zones.ToArray();
        bool ok = false;
        Commit("World map battle zones", () => { Encounters.WriteZones(rom, zones, allowExpand: true); ok = true; });
        if (!ok) return;
        ZonesDirty = false;
        int keep = SelectedIndex;
        BuildEntries();
        SelectedIndex = -1; SelectedIndex = keep;
    }

    [RelayCommand]
    private void RevertZones()
    {
        if (Ctx == null) return;
        try { _zones = Encounters.ReadZones(Ctx.Rom); } catch { _zones = Array.Empty<byte>(); }
        ZonesDirty = false;
        _undo.Clear(); CanUndo = false;
        DrawZones();
        Status = "Zones reverted to the ROM.";
    }
}
