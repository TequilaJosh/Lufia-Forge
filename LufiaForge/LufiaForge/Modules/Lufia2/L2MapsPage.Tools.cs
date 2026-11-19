using System.Collections.ObjectModel;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using LufiaForge.Core;
using LufiaForge.Core.Lufia2;

namespace LufiaForge.Modules.Lufia2;

public enum L2Tool { Select, Paint, Fill, Pick, Area, Collision, AddExit, AddTrigger, AddCharacter, AddPoint, AddChest, AddHidden, AddPuzzle, AddPreset }

// what can be picked on the map: which list of the info block it's in, and the record's index there
public enum SelKind { Exit, Door, Trigger, Character, Point, Treasure }

public sealed record L2Selection(SelKind Kind, int Index);

public partial class L2MapsPage
{
    // tools

    [ObservableProperty] private L2Tool _tool = L2Tool.Select;
    public bool IsSelectTool { get => Tool == L2Tool.Select; set { if (value) Tool = L2Tool.Select; } }
    public bool IsPaintTool { get => Tool == L2Tool.Paint; set { if (value) Tool = L2Tool.Paint; } }
    public bool IsFillTool { get => Tool == L2Tool.Fill; set { if (value) Tool = L2Tool.Fill; } }
    public bool IsPickTool { get => Tool == L2Tool.Pick; set { if (value) Tool = L2Tool.Pick; } }
    public bool IsAreaTool { get => Tool == L2Tool.Area; set { if (value) Tool = L2Tool.Area; } }
    public bool IsCollisionTool { get => Tool == L2Tool.Collision; set { if (value) Tool = L2Tool.Collision; } }
    public bool IsAddExitTool { get => Tool == L2Tool.AddExit; set { if (value) Tool = L2Tool.AddExit; } }
    public bool IsAddTriggerTool { get => Tool == L2Tool.AddTrigger; set { if (value) Tool = L2Tool.AddTrigger; } }
    public bool IsAddCharacterTool { get => Tool == L2Tool.AddCharacter; set { if (value) Tool = L2Tool.AddCharacter; } }
    public bool IsAddPointTool { get => Tool == L2Tool.AddPoint; set { if (value) Tool = L2Tool.AddPoint; } }
    public bool IsAddChestTool { get => Tool == L2Tool.AddChest; set { if (value) Tool = L2Tool.AddChest; } }
    public bool IsAddHiddenTool { get => Tool == L2Tool.AddHidden; set { if (value) Tool = L2Tool.AddHidden; } }

    partial void OnToolChanged(L2Tool value)
    {
        if (value == L2Tool.AddChest) FillChestLooks();   // (this map's own chests as they are right now)
        if (value == L2Tool.AddPreset) FillPresets();
        _presetClicks.Clear(); _spikeRect = null; _presetCells = null;
        NotifyPresetOptions();
        OnPropertyChanged(nameof(ChestLookVisible));
        foreach (var property in new[] { nameof(IsSelectTool), nameof(IsPaintTool), nameof(IsFillTool), nameof(IsPickTool), nameof(IsAreaTool), nameof(IsCollisionTool),
                                  nameof(IsAddExitTool), nameof(IsAddTriggerTool), nameof(IsAddCharacterTool), nameof(IsAddPointTool), nameof(IsAddChestTool), nameof(IsAddHiddenTool), nameof(IsAddPuzzleTool), nameof(PuzzleOptionsVisible), nameof(IsAddPresetTool), nameof(PresetOptionsVisible) })
            OnPropertyChanged(property);
        if (value != L2Tool.Area) AreaRect = null;
        _puzzleStep = 0;
        StopStamp();
        BrushPreviewVisible = false;
        OnPropertyChanged(nameof(ToolHint));
    }

    public string ToolHint => _world != null ? WorldHint : Stamping ? "Click to place the copied blocks (both layers and their walls); Q turns the piece a quarter, F / D flip it; right-click or Esc stops." : Tool switch
    {
        L2Tool.Select => "Select (S): click an exit, step-on area, character, preset point, chest or door to edit it; drag to move it (an area's or exit's bottom-right block resizes it). Click again to reach what lies under it. Double-click an exit to go there.",
        L2Tool.Paint => $"Paint (B): paints the brush on the {(EditUpper ? "upper" : "ground")} layer; drag to paint more. Size: 1, 3, 5 or 7 blocks square ([ and ] change it). Right-click or Ctrl + click picks the block under the mouse.",
        L2Tool.Fill => $"Fill (G): click a block: it and every block joined to it that is the same become the brush, on the {(EditUpper ? "upper" : "ground")} layer. Shift + click (or Fill the layer) fills the whole layer. Right-click picks a block.",
        L2Tool.Pick => "Pick (I): click the map to take its block (on the layer being painted) as the brush.",
        L2Tool.Area => "Area (M): drag a rectangle, then Copy (Ctrl+C), Cut (Ctrl+X), Fill or Delete (Del); Paste (Ctrl+V) places what was copied, from any map with the same graphics.",
        L2Tool.Collision => WallLine
            ? "Collision (L), Wall line: drag along the grid lines to draw walls the party can't cross (the yellow lines, as the game's borders); right-drag takes them away. Pick another kind on the right for whole blocks."
            : "Collision (L): paints where the party can't walk (and doorways and walls). Drag to paint; right-click clears a block. The red, blue and orange marks show it. Wall line draws walls along the grid lines.",
        L2Tool.AddExit => "Add exit (E): drag a rectangle where the party leaves the map, then pick where it leads.",
        L2Tool.AddTrigger => "Add step-on area (T): drag a rectangle; walking onto it plays a new event (made when the map is written, then edit it in Events).",
        L2Tool.AddCharacter => "Add character (C): click where it stands, then pick its sprite and how it moves.",
        L2Tool.AddPoint => "Add preset point (A): click a block. Scenes put the party or characters on preset points (Go to another map, Put a character on a point).",
        L2Tool.AddChest => "Add chest (K): click a block; it gets the chest picture picked below (this map's own, or any chest of the game: its blocks come into this map's block set) and its own opened flag, so it gives its item (or gold) once. Pick what it holds in the inspector.",
        L2Tool.AddPuzzle => PuzzleHint,
        L2Tool.AddPreset => PresetHint,
        L2Tool.AddHidden => "Add hidden item (H): click a block; nothing shows there, and pressing A facing it finds the item, once. Pick the item in the inspector.",
        _ => "",
    };

    private string WorldHint => Stamping ? "Click to place the copied big blocks; right-click or Esc stops."
        : ShowZones && HasZones ? "Battle zones: pick a zone in the list on the right (or right-click the map), then click or drag to paint it (2 x 2 big blocks each). Turn Battle zones off to paint blocks or add entrances."
        : Tool switch
        {
            L2Tool.Paint => "Paint (B): paints big blocks (32 x 32 pixels in the game); right-click or Ctrl + click picks one from the map.",
            L2Tool.Fill => "Fill (G): click a big block: it and every block joined to it that is the same become the brush; Shift + click fills the whole map. Right-click picks one.",
            L2Tool.Pick => "Pick (I): click the map to take its big block as the brush.",
            L2Tool.Area => "Area (M): drag a rectangle, then Copy, Cut, Fill; Paste places it (an island, a forest, a coast).",
            L2Tool.Select => "Select (S): click an entrance (green, named after where it leads) to edit it; drag to move it; double-click goes into that map.",
            L2Tool.AddExit => "Add entrance (E): drag over the blocks the party walks onto (a town's picture), then pick the map and the place inside it the party arrives at.",
            L2Tool.AddPoint => "Add preset point (A): a place on the world map that scenes can put the party on.",
            _ => "On the world map: Paint, Pick and Area work on its big blocks; Select and + Exit edit the entrances to towns and dungeons; Battle zones paints where battles happen.",
        };

    [ObservableProperty] private bool _editUpper;
    partial void OnEditUpperChanged(bool value) { OnPropertyChanged(nameof(ToolHint)); OnPropertyChanged(nameof(BrushText)); }

    // collision paint
    public static readonly string[] CollisionKinds = { "Wall line (draw along the grid lines)", "Can't walk here (obstacle)", "Doorway", "Wall on the top edge", "Wall on the left edge", "Upper level", "Room border" };
    private static readonly byte[] CollisionBits = { 0x00, 0x80, 0x20, 0x04, 0x08, 0x40, 0x10 };
    // Wall line: the mouse draws walls on the grid lines it runs along, like the game's borders. a wall is the top or
    // left edge of a cell (04 / 08), so the line under a cell is the top edge of the cell below
    private bool WallLine => CollisionKind == 0;
    private (int X, int Y, byte Bit)? _lastEdge;
    public IReadOnlyList<string> CollisionChoices => CollisionKinds;
    [ObservableProperty] private int _collisionKind;
    partial void OnCollisionKindChanged(int value) => OnPropertyChanged(nameof(ToolHint));

    // mouse

    private bool _dragging;
    private int _dragStartX, _dragStartY, _lastX = -1, _lastY = -1;
    private byte[]? _dragOriginal;
    private bool _dragResize, _dragMoved;
    private (int X, int Y)? _areaStart;
    private string _strokeWhat = "";

    // a press on the map at pixel (px, py) of the unzoomed map
    public void MouseDown(double px, double py, bool right, bool ctrl, bool doubleClick)
    {
        if (_world != null)
        {
            if (!IsEditableMap) return;
            if (WorldBlockTool) { WorldDown(px, py, right, ctrl); return; }
            if (Tool is not (L2Tool.Select or L2Tool.AddExit or L2Tool.AddPoint))
            {
                Status = _world.Map == 0 ? "On the world map: Select, + Exit (entrances to towns and dungeons) and + Point work, and Paint, Pick, Area and Battle zones; treasure, characters and step-on areas go on town and dungeon maps."
                                         : "The sea floor: Paint, Pick and Area work on its big blocks.";
                return;
            }
        }
        if (_edit == null || !IsEditableMap) return;
        int cx = (int)Math.Floor(px / 16), cy = (int)Math.Floor(py / 16);
        if (cx < 0 || cy < 0 || cx >= _edit.Width || cy >= _edit.Height) return;
        _lastX = cx; _lastY = cy;
        if (Stamping)
        {
            if (right) { StopStamp(); return; }
            Stamp(cx, cy);
            return;
        }
        if (right && Tool is L2Tool.Paint or L2Tool.Fill or L2Tool.Pick or L2Tool.Area) { PickBlock(cx, cy); return; }
        if (ctrl && Tool == L2Tool.Paint) { PickBlock(cx, cy); return; }
        switch (Tool)
        {
            case L2Tool.Paint:
                Record("painting"); _strokeWhat = "painting";
                _dragging = true;
                PaintBrush(cx, cy);
                break;
            case L2Tool.Fill:
                FillAt(cx, cy, all: System.Windows.Input.Keyboard.Modifiers.HasFlag(System.Windows.Input.ModifierKeys.Shift));
                break;
            case L2Tool.Pick:
                PickBlock(cx, cy);
                break;
            case L2Tool.Collision:
                Record(right ? "clearing collision" : "painting collision");
                _dragging = true; _collisionClear = right;
                _lastEdge = null;
                if (WallLine) PaintEdge(px, py); else PaintCollision(cx, cy);
                break;
            case L2Tool.Area:
            case L2Tool.AddExit:
            case L2Tool.AddTrigger:
                _areaStart = (cx, cy);
                AreaRect = (cx, cy, cx + 1, cy + 1);
                _dragging = true;
                break;
            case L2Tool.AddCharacter:
                AddCharacter(cx, cy);
                break;
            case L2Tool.AddPoint:
                AddPoint(cx, cy);
                break;
            case L2Tool.AddChest:
            case L2Tool.AddHidden:
                AddTreasure(cx, cy, Tool == L2Tool.AddHidden);
                break;
            case L2Tool.AddPuzzle:
                PuzzleDown(cx, cy, right);
                break;
            case L2Tool.AddPreset:
                PresetDown(cx, cy, right);
                break;
            case L2Tool.Select:
                SelectAt(cx, cy, doubleClick);
                break;
        }
    }

    private bool _collisionClear;

    public void MouseMove(double px, double py)
    {
        if (_world != null) { WorldMove(px, py); if (!_dragging) return; }
        if (_edit == null) return;
        int cx = (int)Math.Floor(px / 16), cy = (int)Math.Floor(py / 16);
        bool inside = cx >= 0 && cy >= 0 && cx < _edit.Width && cy < _edit.Height;
        if (_world == null) UpdateHover(cx, cy, inside);
        if (!inside || !_dragging) return;
        if (Tool == L2Tool.Collision && WallLine) { PaintEdge(px, py); return; }
        if (cx == _lastX && cy == _lastY) return;
        _lastX = cx; _lastY = cy;
        switch (Tool)
        {
            case L2Tool.Paint: PaintBrush(cx, cy); break;
            case L2Tool.Collision: PaintCollision(cx, cy); break;
            case L2Tool.Area:
            case L2Tool.AddExit:
            case L2Tool.AddTrigger:
            case L2Tool.AddPuzzle when _puzzleStep == 0:
            case L2Tool.AddPreset when _spikeRect == null:
                if (_areaStart is { } start) AreaRect = (Math.Min(start.X, cx), Math.Min(start.Y, cy), Math.Max(start.X, cx) + 1, Math.Max(start.Y, cy) + 1);
                break;
            case L2Tool.Select: DragSelection(cx, cy); break;
        }
    }

    public void MouseUp()
    {
        if (_world != null) { WorldUp(); if (!_dragging) return; }
        if (!_dragging) return;
        _dragging = false;
        switch (Tool)
        {
            case L2Tool.Paint:
            case L2Tool.Collision:
                DropIfSame();
                break;
            case L2Tool.AddExit when AreaRect is { } area:
                AddExit(area.X1, area.Y1, area.X2, area.Y2); AreaRect = null; break;
            case L2Tool.AddTrigger when AreaRect is { } area:
                AddTrigger(area.X1, area.Y1, area.X2, area.Y2); AreaRect = null; break;
            case L2Tool.AddPuzzle when _puzzleStep == 0:
                PuzzleUp(); break;
            case L2Tool.AddPreset when _spikeRect == null:
                PresetUp(); break;
            case L2Tool.Select:
                if (_dragMoved) DropIfSame(); else if (_history.Count > 0 && _dragOriginal != null) DropIfSame();
                _dragOriginal = null;
                break;
        }
        _areaStart = null;
        UpdateUndoState();
    }

    private void EndDrag() { if (_dragging) MouseUp(); }

    public void MouseLeave() { HoverText = ""; BrushPreviewVisible = false; CameraVisible = false; }

    // hover

    private void UpdateHover(int cx, int cy, bool inside)
    {
        if (_edit == null || !inside) { HoverText = ""; BrushPreviewVisible = false; CameraVisible = false; return; }
        var ground = _edit.Map.Layers[0]; var upper = _edit.Map.Layers.Length > 1 ? _edit.Map.Layers[1] : null;
        int i = cy * _edit.Width + cx;
        string text = $"({cx},{cy})  ground {ground.Blocks[i]:X3}" + (ground.Blocks[i] == 0 ? " (nothing: the game draws no ground here, so whatever was on the screen shows; paint a block, a black one for the space around rooms)" : "") +
                      (upper != null ? $", upper {upper.Blocks[i]:X3}" : "");
        byte flags = upper?.Flags[i] ?? 0;
        var walls = new List<string>();
        if ((flags & 0x80) != 0) walls.Add("can't walk"); if ((flags & 0x20) != 0) walls.Add("doorway"); if ((flags & 0x04) != 0) walls.Add("wall on top");
        if ((flags & 0x08) != 0) walls.Add("wall on the left"); if ((flags & 0x40) != 0) walls.Add("upper level"); if ((flags & 0x10) != 0) walls.Add("room border");
        if (walls.Count > 0) text += "; " + string.Join(", ", walls);
        foreach (var (kind, index) in HitsAt(cx, cy)) text += "; " + Describe(kind, index);
        HoverText = text;
        if (Tool == L2Tool.Paint && !Stamping)
        {
            var (paintW, paintH, _, offset) = Painted();
            BrushPreviewX = (cx + offset) * 16; BrushPreviewY = (cy + offset) * 16;
            BrushPreviewW = paintW * 16; BrushPreviewH = paintH * 16;
            BrushPreviewVisible = BrushPreviewImage != null;
        }
        else BrushPreviewVisible = false;
        if (Stamping) { StampX = cx * 16; StampY = cy * 16; }
        if (ShowCamera)
        {
            int mapPixelsW = _edit.Width * 16, mapPixelsH = _edit.Height * 16;
            CameraX = Math.Clamp(cx * 16 + 8 - 128, 0, Math.Max(0, mapPixelsW - 256));
            CameraY = Math.Clamp(cy * 16 + 8 - 112, 0, Math.Max(0, mapPixelsH - 224));
            var outer = new RectangleGeometry(new Rect(0, 0, mapPixelsW, mapPixelsH));
            var inner = new RectangleGeometry(new Rect(CameraX, CameraY, 256, 224));
            CameraShade = new CombinedGeometry(GeometryCombineMode.Exclude, outer, inner);
            CameraText = $"The screen with the party at ({cx},{cy})";
            CameraVisible = true;
        }
        else CameraVisible = false;
    }

    [ObservableProperty] private double _brushPreviewX, _brushPreviewY, _brushPreviewW = 16, _brushPreviewH = 16;
    [ObservableProperty] private bool _brushPreviewVisible;
    [ObservableProperty] private double _cameraX, _cameraY;
    [ObservableProperty] private Geometry? _cameraShade;
    [ObservableProperty] private string _cameraText = "";
    [ObservableProperty] private bool _cameraVisible;
    partial void OnShowCameraChanged(bool value) { if (!value) CameraVisible = false; }
    partial void OnShowGroundChanged(bool value) => Refresh();
    partial void OnShowUpperChanged(bool value) => Refresh();
    partial void OnShowCollisionChanged(bool value) => Refresh();
    partial void OnShowCharactersChanged(bool value) => Refresh();
    partial void OnShowExitsChanged(bool value) => DrawOverlays();
    partial void OnShowDoorsChanged(bool value) => DrawOverlays();
    partial void OnShowTreasureChanged(bool value) => DrawOverlays();
    partial void OnShowTriggersChanged(bool value) => DrawOverlays();
    partial void OnShowPointsChanged(bool value) => DrawOverlays();

    // painting

    private void PaintBrush(int cx, int cy)
    {
        if (_edit == null) return;
        int layer = EditUpper ? 1 : 0;
        if (layer >= _edit.Map.Layers.Length) return;
        var mapLayer = _edit.Map.Layers[layer];
        bool any = false;
        var (paintW, paintH, cells, offset) = Painted();
        for (int by = 0; by < paintH; by++)
            for (int bx = 0; bx < paintW; bx++)
            {
                int x = cx + offset + bx, y = cy + offset + by;
                if (x < 0 || y < 0 || x >= mapLayer.Width || y >= mapLayer.Height) continue;
                int block = cells[by * paintW + bx];
                if (mapLayer.Blocks[y * mapLayer.Width + x] == block) continue;
                mapLayer.Blocks[y * mapLayer.Width + x] = block; any = true;
            }
        if (any) { RememberBrush(); DrawMap(); UpdateUndoState(); }
    }

    // Fill (G): the block at (cx, cy) and every same block joined to it (side by side) become the brush, on the layer
    // being painted. all = every block of the layer. one Undo step
    private void FillAt(int cx, int cy, bool all)
    {
        if (_edit == null) return;
        int layer = EditUpper ? 1 : 0;
        if (layer >= _edit.Map.Layers.Length) return;
        var mapLayer = _edit.Map.Layers[layer];
        Record(all ? "filling the layer" : "filling");
        int changed = Fill(mapLayer.Blocks, mapLayer.Width, mapLayer.Height, cx, cy, all);
        DropIfSame();
        if (changed > 0) { RememberBrush(); DrawMap(); }
        UpdateUndoState();
        Status = changed == 0 ? "Nothing to fill: it is the brush already." : $"{changed} block{(changed == 1 ? "" : "s")} of the {(EditUpper ? "upper" : "ground")} layer filled with the brush (Undo takes it back).";
    }

    // fills a grid of blocks with the brush starting from (cx, cy): the cells joined to it with the same block, or every
    // cell. a brush of several blocks repeats, lined up with the grid's corner. returns how many changed
    private int Fill(int[] grid, int w, int h, int cx, int cy, bool all)
    {
        int Brush(int x, int y) => _brush[y % BrushH * BrushW + x % BrushW];
        var cells = new List<int>();
        if (all) cells.AddRange(Enumerable.Range(0, w * h));
        else
        {
            int startBlock = grid[cy * w + cx];
            var seen = new bool[w * h];
            var todo = new Stack<int>();
            todo.Push(cy * w + cx); seen[cy * w + cx] = true;
            while (todo.Count > 0)
            {
                int i = todo.Pop();
                cells.Add(i);
                int x = i % w, y = i / w;
                foreach (var (nx, ny) in new[] { (x - 1, y), (x + 1, y), (x, y - 1), (x, y + 1) })
                {
                    if (nx < 0 || ny < 0 || nx >= w || ny >= h) continue;
                    int j = ny * w + nx;
                    if (seen[j] || grid[j] != startBlock) continue;
                    seen[j] = true; todo.Push(j);
                }
            }
        }
        int changed = 0;
        foreach (int i in cells)
        {
            int block = Brush(i % w, i / w);
            if (grid[i] != block) { grid[i] = block; changed++; }
        }
        return changed;
    }

    // fills the whole layer being painted (or the world map) with the brush
    [RelayCommand]
    private void FillLayer()
    {
        if (_world != null) { WorldRecord("filling the map"); FillWorld(0, 0, all: true); WorldDropIfSame(); WorldUndoState(); return; }
        if (_edit == null || !IsEditableMap) return;
        FillAt(0, 0, all: true);
    }

    private void PaintCollision(int cx, int cy)
    {
        if (_edit == null || _edit.Map.Layers.Length < 2) return;
        var upper = _edit.Map.Layers[1];
        int i = cy * upper.Width + cx;
        byte oldFlags = upper.Flags[i];
        byte newFlags = _collisionClear ? (byte)(oldFlags & 0x03) : (byte)(oldFlags | CollisionBits[Math.Clamp(CollisionKind, 0, CollisionBits.Length - 1)]);
        if (newFlags == oldFlags) return;
        upper.Flags[i] = newFlags;
        if (!ShowCollision) ShowCollision = true; else DrawMap();
        UpdateUndoState();
    }

    // Wall line: the grid line near pixel (px, py) gets a wall (or loses it on a right-drag). only a line within 5 pixels
    // counts, and where two cross the drag keeps the line it was on
    private void PaintEdge(double px, double py)
    {
        if (_edit == null || _edit.Map.Layers.Length < 2) return;
        var upper = _edit.Map.Layers[1];
        double fx = px / 16, fy = py / 16;
        double dx = Math.Abs(fx - Math.Round(fx)) * 16, dy = Math.Abs(fy - Math.Round(fy)) * 16;
        int x, y; byte bit;
        bool across = dy <= 5, down = dx <= 5;
        // where two lines cross, the line keeps the way it was going (a drag along one doesn't catch the other there)
        if (across && down) { if (_lastEdge is not { } last) return; across = last.Bit == 0x04; down = !across; }
        if (across) { x = (int)Math.Floor(fx); y = (int)Math.Round(fy); bit = 0x04; }
        else if (down) { x = (int)Math.Round(fx); y = (int)Math.Floor(fy); bit = 0x08; }
        else return;
        // the map's own edges stop the party anyway, so a line there needs no cell (and the bottom / right ones have none)
        if (x < 0 || y < 0 || x >= upper.Width || y >= upper.Height) return;
        if (_lastEdge == (x, y, bit)) return;
        _lastEdge = (x, y, bit);
        int i = y * upper.Width + x;
        byte oldFlags = upper.Flags[i], newFlags = _collisionClear ? (byte)(oldFlags & ~bit) : (byte)(oldFlags | bit);
        if (newFlags == oldFlags) return;
        upper.Flags[i] = newFlags;
        if (!ShowCollision) ShowCollision = true; else DrawMap();
        UpdateUndoState();
    }

    private void PickBlock(int cx, int cy)
    {
        if (_edit == null) return;
        int layer = EditUpper ? 1 : 0;
        var mapLayer = _edit.Map.Layers[Math.Min(layer, _edit.Map.Layers.Length - 1)];
        SetBrush(mapLayer.Blocks[cy * mapLayer.Width + cx]);
        Status = $"Block {SelectedBlock:X3} picked from ({cx},{cy}).";
    }

    // areas, copy and paste

    // the area being dragged or picked, in blocks (x1, y1, x2, y2, x2 / y2 not included)
    public (int X1, int Y1, int X2, int Y2)? AreaRect
    {
        // clamped to the map as it is now. an area dragged before a resize (or an Undo of one) used to reach past the
        // edge, and then Copy / Cut / Fill / Delete wrote into the next row or past the end
        get
        {
            if (_areaRect is not { } area) return null;
            int w = _world != null ? L2WorldMap.Size : _edit?.Width ?? 0, h = _world != null ? L2WorldMap.Size : _edit?.Height ?? 0;
            var clamped = (X1: Math.Clamp(area.X1, 0, w), Y1: Math.Clamp(area.Y1, 0, h), X2: Math.Clamp(area.X2, 0, w), Y2: Math.Clamp(area.Y2, 0, h));
            return clamped.X2 > clamped.X1 && clamped.Y2 > clamped.Y1 ? clamped : null;
        }
        set
        {
            _areaRect = value;
            AreaVisible = value != null;
            int unit = AreaUnit;
            if (value is { } area) { AreaX = area.X1 * unit; AreaY = area.Y1 * unit; AreaW = (area.X2 - area.X1) * unit; AreaH = (area.Y2 - area.Y1) * unit; }
            OnPropertyChanged();
        }
    }
    private (int X1, int Y1, int X2, int Y2)? _areaRect;
    // pixels per area unit: big blocks for the world map's Area tool, 16-pixel blocks otherwise
    private int AreaUnit => _world != null && Tool == L2Tool.Area ? Cell : 16;
    [ObservableProperty] private double _areaX, _areaY, _areaW, _areaH;
    [ObservableProperty] private bool _areaVisible;

    // copied blocks, shared by every map: both layers and the upper layer's cell flags, plus the graphics they came
    // from. they're in block set / tile set BlockSet / TileSet, and a map with those (SourceMap) draws them with palette
    // set PaletteSet. -1 = not known, any map with those sets
    public sealed record Clip(int W, int H, int[] Ground, int[] Upper, byte[] Flags, int BlockSet, int TileSet, int SourceMap = -1, int PaletteSet = -1);
    private static Clip? _clip;

    [RelayCommand]
    private void CopyArea()
    {
        if (_world != null && AreaRect is { } worldArea) { _clip = WorldCut(worldArea); Status = $"Copied {worldArea.X2 - worldArea.X1} x {worldArea.Y2 - worldArea.Y1} big blocks."; return; }
        if (_edit == null || AreaRect is not { } area) { Status = "Drag an area with the Area tool first."; return; }
        _clip = Cut(area);
        Status = $"Copied {area.X2 - area.X1} x {area.Y2 - area.Y1} blocks. Paste (Ctrl+V) places them, on this map or another with the same graphics.";
    }

    private Clip Cut((int X1, int Y1, int X2, int Y2) area)
    {
        int w = area.X2 - area.X1, h = area.Y2 - area.Y1;
        var ground = new int[w * h]; var upper = new int[w * h]; var flags = new byte[w * h];
        var L0 = _edit!.Map.Layers[0]; var L1 = _edit.Map.Layers.Length > 1 ? _edit.Map.Layers[1] : null;
        for (int y = 0; y < h; y++)
            for (int x = 0; x < w; x++)
            {
                int i = (area.Y1 + y) * L0.Width + area.X1 + x;
                ground[y * w + x] = L0.Blocks[i];
                if (L1 != null) { upper[y * w + x] = L1.Blocks[i]; flags[y * w + x] = L1.Flags[i]; }
            }
        return new Clip(w, h, ground, upper, flags, _edit.BlockSetIndex, _edit.TileSetIndex, _edit.MapId, _edit.PaletteSetIndex);
    }

    [RelayCommand]
    private void CutArea()
    {
        if (_world != null && AreaRect is { } worldArea) { _clip = WorldCut(worldArea); WorldRecord("cutting an area"); WorldFill(worldArea); return; }
        if (_edit == null || AreaRect is not { } area) { Status = "Drag an area with the Area tool first."; return; }
        _clip = Cut(area);
        Record("cutting an area");
        FillRect(area, SelectedBlock);
        Status = "Cut: copied, then filled with the brush block.";
    }

    [RelayCommand]
    private void FillArea()
    {
        if (_world != null && AreaRect is { } worldArea) { WorldRecord("filling an area"); WorldFill(worldArea); return; }
        if (_edit == null || AreaRect is not { } area) { Status = "Drag an area with the Area tool first."; return; }
        Record("filling an area");
        FillRect(area, SelectedBlock);
        Status = $"Filled with block {SelectedBlock:X3} on the {(EditUpper ? "upper" : "ground")} layer.";
    }

    [RelayCommand]
    private void WorldFill((int X1, int Y1, int X2, int Y2) area)
    {
        for (int y = area.Y1; y < area.Y2; y++) for (int x = area.X1; x < area.X2; x++) _world!.Cells[y * L2WorldMap.Size + x] = SelectedBlock;
        DrawWorld(); WorldUndoState();
    }

    [RelayCommand]
    private void DeleteArea()
    {
        if (_world != null) return;
        if (_edit == null || AreaRect is not { } area) return;
        Record("clearing an area");
        FillRect(area, 0);
        Status = $"Cleared the {(EditUpper ? "upper" : "ground")} layer there (block 000 draws nothing).";
    }

    private void FillRect((int X1, int Y1, int X2, int Y2) area, int block)
    {
        int layer = EditUpper ? 1 : 0;
        var mapLayer = _edit!.Map.Layers[Math.Min(layer, _edit.Map.Layers.Length - 1)];
        for (int y = area.Y1; y < area.Y2; y++) for (int x = area.X1; x < area.X2; x++) mapLayer.Blocks[y * mapLayer.Width + x] = block;
        DropIfSame();
        DrawMap(); UpdateUndoState();
    }

    // stamping what was copied
    [ObservableProperty] private bool _stamping;
    [ObservableProperty] private BitmapSource? _stampImage;
    [ObservableProperty] private double _stampX, _stampY;
    partial void OnStampingChanged(bool value) => OnPropertyChanged(nameof(ToolHint));

    [RelayCommand]
    private void Paste()
    {
        if (_world != null)
        {
            if (_clip == null || _clip.BlockSet != -1 - _world.Map) { Status = "Copy an area of this map first (big blocks can't go between the world map, the sea floor and towns)."; return; }
            StartStamp(_clip); return;
        }
        if (_edit == null || _clip == null) { Status = "Nothing copied yet: drag an area with the Area tool and Copy it (or use Pieces and presets...)."; return; }
        var clip = _clip;
        if (clip.BlockSet != _edit.BlockSetIndex || clip.TileSet != _edit.TileSetIndex)
        {
            // copied on other graphics: bring its blocks into this map's block set first, same as presets and From
            if (WritePendingEdits() is { } why) { Status = "The map couldn't be written first: " + why; return; }
            clip = BringIn(clip, "copy");
            if (clip == null) return;
            string note = Status;
            StartStamp(clip);
            Status = note;
            return;
        }
        StartStamp(clip);
    }

    // starts placing clip (from Copy, Pieces... or a preset)
    public void StartStamp(Clip clip)
    {
        if (_world != null)
        {
            _clip = clip;
            int imageW = clip.W * Cell, imageH = clip.H * Cell;
            var worldPixels = new uint[imageW * imageH];
            for (int y = 0; y < clip.H; y++)
                for (int x = 0; x < clip.W; x++)
                {
                    var bigPixels = Big(clip.Ground[y * clip.W + x]);
                    for (int yy = 0; yy < Cell; yy++) Array.Copy(bigPixels, yy * Cell, worldPixels, (y * Cell + yy) * imageW + x * Cell, Cell);
                }
            StampImage = BitmapSource.Create(imageW, imageH, 96, 96, PixelFormats.Bgra32, null, worldPixels.Select(c => (int)c).ToArray(), imageW * 4);
            Stamping = true;
            return;
        }
        if (_edit == null) return;
        _clip = clip;
        var pixels = new uint[clip.W * 16 * clip.H * 16];
        int w = clip.W * 16;
        for (int y = 0; y < clip.H; y++)
            for (int x = 0; x < clip.W; x++)
                foreach (int block in new[] { clip.Ground[y * clip.W + x], clip.Upper[y * clip.W + x] })
                {
                    if (block <= 0 || block >= _blocks.Length) continue;
                    var blockPixels = _blocks[block];
                    for (int yy = 0; yy < 16; yy++) for (int xx = 0; xx < 16; xx++) if (blockPixels[yy * 16 + xx] != 0) pixels[(y * 16 + yy) * w + x * 16 + xx] = blockPixels[yy * 16 + xx];
                }
        StampImage = BitmapSource.Create(w, clip.H * 16, 96, 96, PixelFormats.Bgra32, null, pixels.Select(c => (int)c).ToArray(), w * 4);
        Stamping = true;
        Status = clip.BlockSet != _edit.BlockSetIndex || clip.TileSet != _edit.TileSetIndex
            ? $"These blocks come from block set {clip.BlockSet} / tile set {clip.TileSet}; this map uses {_edit.BlockSetIndex} / {_edit.TileSetIndex}, so they may look different here. Click to place them; Esc stops."
            : "Click to place the copied blocks; right-click or Esc stops.";
        OnPropertyChanged(nameof(ToolHint));
    }

    public void StopStamp() { Stamping = false; StampImage = null; OnPropertyChanged(nameof(ToolHint)); }

    private void Stamp(int cx, int cy)
    {
        if (_edit == null || _clip is not { } clip) return;
        Record("placing copied blocks");
        var L0 = _edit.Map.Layers[0]; var L1 = _edit.Map.Layers.Length > 1 ? _edit.Map.Layers[1] : null;
        for (int y = 0; y < clip.H; y++)
            for (int x = 0; x < clip.W; x++)
            {
                int mx = cx + x, my = cy + y;
                if (mx >= L0.Width || my >= L0.Height) continue;
                int i = my * L0.Width + mx;
                L0.Blocks[i] = clip.Ground[y * clip.W + x];
                if (L1 != null) { L1.Blocks[i] = clip.Upper[y * clip.W + x]; L1.Flags[i] = clip.Flags[y * clip.W + x]; }
            }
        DropIfSame();
        DrawMap(); UpdateUndoState();
    }

    // Esc: stops placing, else drops the area, else the picked thing
    public void Escape()
    {
        if (Stamping) { StopStamp(); return; }
        if (AreaRect != null) { AreaRect = null; return; }
        Selection = null; DrawOverlays(); UpdateInspector();
    }

    // adding things

    private void AddExit(int x1, int y1, int x2, int y2)
    {
        if (_edit == null) return;
        var exits = _edit.Records(L2MapEdit.Exits);
        int id = exits.Select(e => (int)e.Bytes[0]).DefaultIfEmpty(-1).Max() + 1;
        // the arrival style most of the map's exits use (F3 = the usual one)
        byte style = exits.GroupBy(e => e.Bytes[5]).OrderByDescending(g => g.Count()).Select(g => g.Key).DefaultIfEmpty(_edit.IsWorld ? (byte)0x40 : (byte)0xF3).First();
        if (id > 0xFE) { Status = "The map has no free exit number."; return; }
        Record(_edit.IsWorld ? "adding an entrance" : "adding an exit");
        try { _edit.AddRecord(L2MapEdit.Exits, new byte[] { (byte)id, (byte)x1, (byte)y1, (byte)x2, (byte)y2, style, (byte)x1, (byte)y1, (byte)_edit.MapId }); }
        catch (Exception ex) { Status = ex.Message; UndoPaint(); return; }
        Selection = new L2Selection(SelKind.Exit, _edit.Records(L2MapEdit.Exits).Count - 1);
        Tool = L2Tool.Select;
        Refresh();
        Status = _edit.IsWorld
            ? $"Entrance {id} added. Now click \"Pick on map...\" in the inspector: pick the town or dungeon and click where the party arrives in it."
            : $"Exit {id} added: it leads back here for now. Click \"Pick on map...\" in the inspector to pick the map and place it leads to.";
    }

    private void AddTrigger(int x1, int y1, int x2, int y2)
    {
        if (_edit == null || _rom == null) return;
        if (L2Events.Block(_rom, _edit.MapId) < 0) { Status = "This map has no event block, so it can't have step-on areas."; return; }
        int id = _edit.NextTriggerId(_rom);
        if (id > 0xFE) { Status = "The map has no free area number."; return; }
        Record("adding a step-on area");
        try { _edit.AddRecord(L2MapEdit.Triggers, new byte[] { (byte)id, (byte)x1, (byte)y1, (byte)x2, (byte)y2 }); }
        catch (Exception ex) { Status = ex.Message; UndoPaint(); return; }
        Selection = new L2Selection(SelKind.Trigger, _edit.Records(L2MapEdit.Triggers).Count - 1);
        Tool = L2Tool.Select;
        Refresh();
        Status = $"Step-on area {id} added. Its event is made when the map is written; \"Event...\" writes it and opens it in Events.";
    }

    private void AddCharacter(int x, int y)
    {
        if (_edit == null || _rom == null) return;
        int id = _edit.NextCharacterId(_rom);
        if (id > 0x6F) { Status = "The map has 32 characters already."; return; }
        Record("adding a character");
        try
        {
            _edit.AddRecord(L2MapEdit.Characters, new byte[] { (byte)(id - 0x4F), (byte)x, (byte)y, (byte)Math.Max(0, x - 2), (byte)Math.Max(0, y - 2),
                (byte)Math.Min(_edit.Width, x + 3), (byte)Math.Min(_edit.Height, y + 3), 0x05 });
        }
        catch (Exception ex) { Status = ex.Message; UndoPaint(); return; }
        _edit.Sprites[id] = 0x0C;
        Selection = new L2Selection(SelKind.Character, _edit.Records(L2MapEdit.Characters).Count - 1);
        Tool = L2Tool.Select;
        Refresh();
        Status = $"Character {id - 0x4F} added (sprite 0C, standing facing down). Pick its sprite and how it moves; \"Talk event...\" gives it something to say.";
    }

    private void AddPoint(int x, int y)
    {
        if (_edit == null) return;
        int id = _edit.NextPointId();
        if (id > 0xFE) return;
        Record("adding a preset point");
        try { _edit.AddRecord(L2MapEdit.Points, new byte[] { (byte)id, (byte)x, (byte)y }); }
        catch (Exception ex) { Status = ex.Message; UndoPaint(); return; }
        ShowPoints = true;
        Selection = new L2Selection(SelKind.Point, _edit.Records(L2MapEdit.Points).Count - 1);
        Tool = L2Tool.Select;
        Refresh();
        Status = $"Preset point {id} added at ({x},{y}).";
    }

    private void AddTreasure(int x, int y, bool hidden)
    {
        if (_edit == null || _rom == null || _main == null) return;
        if (TableGuard.LockReason(_rom, "Treasure") is { } locked) { Status = locked; return; }
        var look = hidden ? null : SelectedChestLook?.Look;
        Record(hidden ? "adding a hidden item" : "adding a chest");
        int spot; string note;
        int blocks = L2Maps.BlockCount(_rom, _edit.MapId);
        try { (spot, note) = _edit.AddTreasure(_rom, x, y, hidden, 0x00, look); }
        catch (Exception ex) { UndoPaint(); _redo.Clear(); _worldRedo.Clear(); UpdateUndoState(); Status = "No chest added: " + ex.Message; return; }
        if (!hidden && look != null && look.Map != _edit.MapId && note.Contains("added to block set"))
        {
            // the chest's blocks went into the block set, and its tiles into the tile set if it had none of that shape.
            // that's in the ROM already, like the block editor's changes, and one Edit > Undo step takes both out
            _main.NotifyRomModified(L2ChestLooks.LastTiles is { } lastTiles
                ? $"Lufia II: chest tiles added to tile set {lastTiles.TileSet} and blocks to block set {_edit.BlockSetIndex}"
                : $"Lufia II: chest blocks added to block set {_edit.BlockSetIndex}");
            ReloadBlocks();
        }
        ShowTreasure = true;
        Selection = new L2Selection(SelKind.Treasure, _edit.Records(L2MapEdit.Treasure).Count - 1);
        Tool = L2Tool.Select;
        Refresh();
        Status = (hidden ? $"Hidden item {spot} added at ({x},{y}): pressing A facing that block finds it, once." :
                     $"Chest {spot} added at ({x},{y}), with the picture of {(look == null || look.Map == _edit.MapId ? "this map's chests" : $"the chest of {look.Map:X2} {NameOf(look.Map)}")}; it opens once.")
                 + (note.Length > 0 ? " " + note : "") + " Pick what it holds in the inspector.";
    }

    // chest pictures

    private List<L2ChestLooks.Look>? _gameLooks;
    // the chest picture list only shows for the + Chest tool on a town or dungeon map
    public bool ChestLookVisible => Tool == L2Tool.AddChest && _world == null && _edit != null;

    // the chest pictures + Chest can use: this map's own first, then the game's other chests
    public System.Collections.ObjectModel.ObservableCollection<L2ChestLookChoice> ChestLookChoices { get; } = new();
    [ObservableProperty] private L2ChestLookChoice? _selectedChestLook;

    // fills the chest picture list for the map (the game's looks are only read once per ROM)
    private void FillChestLooks()
    {
        if (_rom == null || _edit == null || _edit.IsWorld) { ChestLookChoices.Clear(); SelectedChestLook = null; return; }
        _gameLooks ??= L2ChestLooks.GameLooks(_rom);
        ChestLookChoices.Clear();
        int blockSet = _edit.BlockSetIndex, tileSet = _edit.TileSetIndex, paletteSet = _edit.PaletteSetIndex;
        // this map's own chests the way the edit has them now, then the other maps'
        var all = L2ChestLooks.LooksOf(_edit).Concat(_gameLooks.Where(l => l.Map != _edit.MapId));
        foreach (var look in all.OrderBy(l => l.Map == _edit.MapId ? 0 : l.BlockSet == blockSet && l.TileSet == tileSet && l.PaletteSet == paletteSet ? 1 : l.TileSet == tileSet ? 2 : 3).ThenBy(l => l.Map))
        {
            string where = look.Map == _edit.MapId ? "This map's chest" : $"{look.Map:X2} {NameOf(look.Map)}";
            string fit = look.Map == _edit.MapId ? "" : look.BlockSet == blockSet && look.TileSet == tileSet && look.PaletteSet == paletteSet ? " (same graphics)" : look.TileSet == tileSet ? " (same tiles)" : "";
            var (w, h, pixels) = L2ChestLooks.Picture(_rom, look);
            var image = BitmapSource.Create(w, h, 96, 96, PixelFormats.Bgra32, null, pixels.Select(c => (int)(c == 0 ? 0xFF000000u : c)).ToArray(), w * 4);
            image.Freeze();
            ChestLookChoices.Add(new L2ChestLookChoice { Look = look, Label = where + fit, Image = image });
        }
        SelectedChestLook = ChestLookChoices.FirstOrDefault();
    }

    // keys

    // a key pressed on the map (tool letters, Del, Esc, Ctrl+C / X / V). returns true if we used it
    public bool Key(System.Windows.Input.Key key, bool ctrl)
    {
        if (ctrl)
        {
            switch (key)
            {
                case System.Windows.Input.Key.C: CopyArea(); return true;
                case System.Windows.Input.Key.X: CutArea(); return true;
                case System.Windows.Input.Key.V: Paste(); return true;
            }
            return false;
        }
        switch (key)
        {
            case System.Windows.Input.Key.S: case System.Windows.Input.Key.V: Tool = L2Tool.Select; return true;
            case System.Windows.Input.Key.B: case System.Windows.Input.Key.P: Tool = L2Tool.Paint; return true;
            case System.Windows.Input.Key.I: Tool = L2Tool.Pick; return true;
            case System.Windows.Input.Key.G: Tool = L2Tool.Fill; return true;
            case System.Windows.Input.Key.OemOpenBrackets: BrushSize = BrushSizes[Math.Max(0, BrushSizes.ToList().IndexOf(BrushSize) - 1)]; return true;
            case System.Windows.Input.Key.OemCloseBrackets: BrushSize = BrushSizes[Math.Min(BrushSizes.Count - 1, BrushSizes.ToList().IndexOf(BrushSize) + 1)]; return true;
            case System.Windows.Input.Key.M: Tool = L2Tool.Area; return true;
            case System.Windows.Input.Key.L: Tool = L2Tool.Collision; return true;
            case System.Windows.Input.Key.E: Tool = L2Tool.AddExit; return true;
            case System.Windows.Input.Key.T: Tool = L2Tool.AddTrigger; return true;
            case System.Windows.Input.Key.C: Tool = L2Tool.AddCharacter; return true;
            case System.Windows.Input.Key.A: Tool = L2Tool.AddPoint; return true;
            case System.Windows.Input.Key.K: Tool = L2Tool.AddChest; return true;
            case System.Windows.Input.Key.H: Tool = L2Tool.AddHidden; return true;
            case System.Windows.Input.Key.U: Tool = L2Tool.AddPuzzle; return true;
            case System.Windows.Input.Key.R: Tool = L2Tool.AddPreset; return true;
            case System.Windows.Input.Key.Q: RotateBrush(); return true;
            case System.Windows.Input.Key.F: FlipBrushAcross(); return true;
            case System.Windows.Input.Key.D: FlipBrushUpDown(); return true;
            case System.Windows.Input.Key.Escape: Escape(); return true;
            case System.Windows.Input.Key.Delete:
                if (Tool == L2Tool.Area && AreaRect != null) DeleteArea(); else DeleteSelected();
                return true;
        }
        return false;
    }
}
