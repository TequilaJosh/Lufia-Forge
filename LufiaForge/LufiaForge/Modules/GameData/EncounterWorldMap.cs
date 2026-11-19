using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using LufiaForge.Core;
using LufiaForge.Core.Battle;
using LufiaForge.Core.Maps;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace LufiaForge.Modules.GameData;

// the world map's encounter zones. resource B0: 80 x 64 cells, one per 4x4 tiles, each one a group number
// (0 = no battles, the game skips the encounter when the group is 0). shown over the world map, you can pick,
// paint and fill them
public partial class EncounterEditorViewModel
{
    // pixels per world map tile in WorldImage (16 would be full size)
    public const int TileScale = 4;
    public const int CellPixels = 4 * TileScale;
    public double WorldPixelWidth => Encounters.ZoneWidth * CellPixels;
    public double WorldPixelHeight => Encounters.ZoneHeight * CellPixels;

    private byte[] _zones = Array.Empty<byte>();
    [ObservableProperty] private ImageSource? _worldImage;
    [ObservableProperty] private WriteableBitmap? _zoneImage;
    [ObservableProperty] private bool _zonesDirty;
    [ObservableProperty] private bool _showAllZones = true;
    // 0 = pick (click a zone to select its group), 1 = paint, 2 = fill an area
    [ObservableProperty] private int _toolIndex;
    [ObservableProperty] private int _brushSize = 1;
    // paint "no battles" (group 0) instead of the selected group
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
            var pixels = MapTileset.ForMap(rom, map).RenderMap(map);
            int w = map.Width * 16, h = map.Height * 16, smallWidth = w / 4, smallHeight = h / 4;
            var small = new uint[smallWidth * smallHeight];
            for (int y = 0; y < smallHeight; y++)
                for (int x = 0; x < smallWidth; x++)
                {
                    uint r = 0, g = 0, b = 0;
                    for (int dy = 0; dy < 4; dy++)
                        for (int dx = 0; dx < 4; dx++)
                        {
                            uint color = pixels[(y * 4 + dy) * w + x * 4 + dx];
                            r += color >> 16 & 255; g += color >> 8 & 255; b += color & 255;
                        }
                    small[y * smallWidth + x] = 0xFF000000u | (r / 16) << 16 | (g / 16) << 8 | b / 16;
                }
            return (small, smallWidth, smallHeight);
        }).ContinueWith(task =>
        {
            if (task.IsFaulted) { WorldStatus = "The world map could not be drawn: " + task.Exception?.GetBaseException().Message; return; }
            var (small, smallWidth, smallHeight) = task.Result;
            var bmp = BitmapSource.Create(smallWidth, smallHeight, 96, 96, PixelFormats.Bgra32, null, small, smallWidth * 4);
            bmp.Freeze();
            WorldImage = bmp;
            WorldStatus = "";
        }, TaskScheduler.FromCurrentSynchronizationContext());
    }

    private static uint Hue(int group, uint alpha)
    {
        double h = (group * 47 % 360) / 60.0, x = 1 - Math.Abs(h % 2 - 1);
        (double red, double green, double blue) = h switch
        {
            < 1 => (1.0, x, 0.0), < 2 => (x, 1.0, 0.0), < 3 => (0.0, 1.0, x),
            < 4 => (0.0, x, 1.0), < 5 => (x, 0.0, 1.0), _ => (1.0, 0.0, x),
        };
        return alpha << 24 | (uint)(red * 255) << 16 | (uint)(green * 255) << 8 | (uint)(blue * 255);
    }

    private void DrawZones()
    {
        int w = Encounters.ZoneWidth, h = Encounters.ZoneHeight;
        if (_zones.Length < w * h) { ZoneImage = null; return; }
        int selected = SelectedGroup;
        var pixels = new uint[w * h];
        for (int i = 0; i < pixels.Length; i++)
        {
            int group = _zones[i];
            pixels[i] = group == 0 ? 0u : group == selected ? 0xA8FFE040u : ShowAllZones ? Hue(group, 0x38) : 0u;
        }
        var bmp = new WriteableBitmap(w, h, 96, 96, PixelFormats.Bgra32, null);
        bmp.WritePixels(new Int32Rect(0, 0, w, h), pixels, w * 4, 0);
        ZoneImage = bmp;
    }

    private bool InGrid(int cx, int cy) => cx >= 0 && cy >= 0 && cx < Encounters.ZoneWidth && cy < Encounters.ZoneHeight && _zones.Length >= Encounters.ZoneWidth * Encounters.ZoneHeight;

    // mouse over cell (cx, cy)
    public void HoverCell(int cx, int cy)
    {
        if (!InGrid(cx, cy)) { HoverText = ""; return; }
        int group = _zones[cy * Encounters.ZoneWidth + cx];
        HoverText = $"Zone {cx},{cy} (tiles {cx * 4}-{cx * 4 + 3}, {cy * 4}-{cy * 4 + 3}): " + (group == 0 ? "no battles" : $"group {group:X2}" + (group <= Count ? $" - {GroupMonsterSummary(group)}" : " (no such group!)"));
    }

    private string GroupMonsterSummary(int group) =>
        string.Join(", ", _groups[group - 1].Distinct().SelectMany(s => Monsters(s)).Where(m => m != 0xFF).Select(MonsterName).Distinct().Take(6));

    // tools: a stroke is mouse down .. mouse up, and each stroke can be undone

    private readonly Stack<byte[]> _undo = new(), _redo = new();
    [ObservableProperty] private bool _canUndo;
    [ObservableProperty] private bool _canRedo;
    [ObservableProperty] private string _undoText = "Nothing to undo";
    [ObservableProperty] private string _redoText = "Nothing to redo";

    private void UpdateUndo()
    {
        CanUndo = _undo.Count > 0; CanRedo = _redo.Count > 0;
        UndoText = _undo.Count > 0 ? $"Undo the last paint, fill or rectangle ({_undo.Count} step{(_undo.Count == 1 ? "" : "s")} back, Ctrl+Z)" : "Nothing to undo";
        RedoText = _redo.Count > 0 ? $"Redo ({_redo.Count} step{(_redo.Count == 1 ? "" : "s")}, Ctrl+Y)" : "Nothing to redo";
    }
    private (int X, int Y)? _rectStart;
    private byte[]? _beforeRect;
    // a "no battles" area bigger than this is joined up with the open map, so filling it asks first
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
        if (_undo.Count > 300) { var kept = _undo.Take(300).Reverse().ToList(); _undo.Clear(); foreach (var zones in kept) _undo.Push(zones); }
        _redo.Clear();
        UpdateUndo();
    }

    // mouse down on cell (cx, cy)
    public void BeginStroke(int cx, int cy)
    {
        if (!InGrid(cx, cy)) return;
        int i = cy * Encounters.ZoneWidth + cx;
        switch (ToolIndex)
        {
            case 0:   // pick
                int group = _zones[i];
                if (group == 0) { Status = "No battles in this zone. Pick a group in the list and paint to add some."; return; }
                if (group <= Count) SelectedIndex = group - 1;
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

    // mouse moved with the button down
    public void DragStroke(int cx, int cy)
    {
        cx = Math.Clamp(cx, 0, Encounters.ZoneWidth - 1); cy = Math.Clamp(cy, 0, Encounters.ZoneHeight - 1);
        if (ToolIndex == 1) { if (Brush != 0 || EraseZones) PaintAt(cx, cy); return; }
        if (ToolIndex == 3 && _rectStart is var (startX, startY) && _beforeRect != null)
        {
            // preview: the zones from before the stroke, with the rectangle on top
            _beforeRect.CopyTo(_zones, 0);
            for (int y = Math.Min(startY, cy); y <= Math.Max(startY, cy); y++)
                for (int x = Math.Min(startX, cx); x <= Math.Max(startX, cx); x++)
                    _zones[y * Encounters.ZoneWidth + x] = (byte)Brush;
            DrawZones();
            HoverText = $"Rectangle {Math.Abs(cx - startX) + 1} x {Math.Abs(cy - startY) + 1} zones";
        }
    }

    // mouse up
    public void EndStroke(int cx, int cy)
    {
        if (ToolIndex == 3 && _beforeRect != null)
        {
            DragStroke(cx, cy);
            if (!_zones.SequenceEqual(_beforeRect))
            {
                _undo.Push(_beforeRect); _redo.Clear(); UpdateUndo();
                ZonesDirty = true;
            }
            _beforeRect = null; _rectStart = null;
        }
    }

    private void PaintAt(int cx, int cy)
    {
        int brush = Brush, half = (BrushSize - 1) / 2;
        bool changed = false;
        for (int y = cy - half; y < cy - half + BrushSize; y++)
            for (int x = cx - half; x < cx - half + BrushSize; x++)
                if (InGrid(x, y) && _zones[y * Encounters.ZoneWidth + x] != brush) { _zones[y * Encounters.ZoneWidth + x] = (byte)brush; changed = true; }
        if (changed) { ZonesDirty = true; DrawZones(); HoverCell(cx, cy); }
    }

    // the connected cells that have the same group as (cx, cy)
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
        _redo.Push(_zones.ToArray());
        _zones = _undo.Pop();
        UpdateUndo();
        ZonesDirty = true;
        DrawZones();
        Status = $"Undone ({_undo.Count} more step{(_undo.Count == 1 ? "" : "s")} can be undone).";
    }

    [RelayCommand]
    private void RedoZones()
    {
        if (_redo.Count == 0) return;
        _undo.Push(_zones.ToArray());
        _zones = _redo.Pop();
        UpdateUndo();
        ZonesDirty = true;
        DrawZones();
        Status = "Redone.";
    }

    public override bool HasPendingEdits => base.HasPendingEdits || ZonesDirty && Ctx != null;

    public override string? WritePendingEdits()
    {
        bool zones = ZonesDirty;
        string? error = base.HasPendingEdits ? base.WritePendingEdits() : null;
        if (error != null) return error;
        if (zones && ZonesDirty)
        {
            _autoZones = true;
            try { WriteZones(); }
            finally { _autoZones = false; }
            if (ZonesDirty) return $"{PageName}: {Status}";   // still not written, Status says why
        }
        return null;
    }

    // the app is writing the zones (along with the other edits), so no questions then
    private bool _autoZones;

    [RelayCommand]
    private void WriteZones()
    {
        if (Ctx == null || _zones.Length == 0) return;
        var rom = Ctx.Rom;
        int size = LufiaCompression.Compress(_zones).Length;
        int slot = ResourceWriter.SlotSize(rom, Encounters.ZoneResource);
        if (size > slot && rom.Length < MapWriter.ExpandedSize && !AutoWriting && !_autoZones &&
            MessageBox.Show("The edited zones don't fit in their original space. Expand the ROM to 2 MB?", "Expand ROM?", MessageBoxButton.YesNo) != MessageBoxResult.Yes)
        { Status = "Zones not written: the ROM was not expanded."; return; }
        var zones = _zones.ToArray();
        bool ok = false;
        Commit("World map battle zones", () => { Encounters.WriteZones(rom, zones, allowExpand: true); ok = true; });
        if (!ok) return;
        ZonesDirty = false;
        int keepIndex = SelectedIndex;
        BuildEntries();
        SelectedIndex = -1; SelectedIndex = keepIndex;
    }

    [RelayCommand]
    private void RevertZones()
    {
        if (Ctx == null) return;
        try { _zones = Encounters.ReadZones(Ctx.Rom); } catch { _zones = Array.Empty<byte>(); }
        ZonesDirty = false;
        _undo.Clear(); _redo.Clear(); UpdateUndo();
        DrawZones();
        Status = "Zones put back as they are in the project.";
    }
}
